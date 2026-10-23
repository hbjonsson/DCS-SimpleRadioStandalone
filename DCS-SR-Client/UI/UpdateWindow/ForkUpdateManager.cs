using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Helpers;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Settings;
using NLog;

namespace Ciribob.DCS.SimpleRadio.Standalone.Client.UI.UpdateWindow;

/// <summary>
/// Checks the fork's GitHub releases and, when the player agrees, installs the new MSI over this one.
/// The install runs from a detached helper after the client exits, then starts the client again.
/// </summary>
public static class ForkUpdateManager
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    internal static string DownloadFolder => Path.Combine(Path.GetTempPath(), "455th-SRS-update");

    private static int _checking;

    /// <summary>
    /// Startup (<paramref name="userRequested"/> false) honours the setting and skipped versions and stays
    /// silent on errors; "Check now" always checks and reports the outcome.
    /// </summary>
    public static async Task CheckAsync(bool userRequested)
    {
        var settings = GlobalSettingsStore.Instance;
        if (!userRequested && !settings.GetClientSettingBool(GlobalSettingsKeys.CheckForUpdates))
            return;
        if (Interlocked.Exchange(ref _checking, 1) == 1)
            return;

        try
        {
            CleanDownloads();

            var (owner, repository) = Repository();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var releases = await ForkUpdates.FetchReleasesAsync(Http, owner, repository, timeout.Token);
            var skipped = userRequested ? null : settings.GetClientSetting(GlobalSettingsKeys.SkippedUpdateVersion).RawValue;
            var update = ForkUpdates.SelectUpdate(releases, Version.Parse(ForkBranding.BaseVersion),
                ForkBranding.ForkBuild, settings.GetClientSettingBool(GlobalSettingsKeys.CheckForBetaUpdates), skipped);

            if (update == null)
            {
                Logger.Info("No update: running {Version}", ForkBranding.DisplayVersion);
                if (userRequested)
                    MessageBox.Show($"You have the latest version ({ForkBranding.DisplayVersion}).",
                        ForkBranding.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Logger.Info("Update available: {Version}", update.DisplayVersion);
            var window = new UpdateWindow(update);
            var mainWindow = Application.Current.MainWindow;
            if (mainWindow is { IsVisible: true })
            {
                window.Owner = mainWindow;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }

            window.ShowDialog();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Update check failed");
            if (userRequested)
                MessageBox.Show($"Couldn't check for updates: {ex.Message}", ForkBranding.ProductName,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    /// <summary>
    /// Hands over to a hidden helper that waits for this process to exit, runs the MSI into the same folder
    /// (Windows asks for admin rights), and starts the client again whether or not the install went ahead.
    /// </summary>
    internal static void InstallAndRestart(string msiPath, string clientFolder)
    {
        var installFolder = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(clientFolder));
        var exe = Path.Combine(clientFolder, ForkBranding.ExecutableName);
        var log = Path.Combine(DownloadFolder, "install.log");
        var script = Path.Combine(DownloadFolder, "install-update.cmd");
        var pid = Environment.ProcessId;

        File.WriteAllText(script, string.Join("\r\n",
            "@echo off",
            ":wait",
            $"tasklist /FI \"PID eq {pid}\" /NH | find \"{pid}\" >nul && (ping -n 2 127.0.0.1 >nul & goto wait)",
            $"msiexec /i \"{msiPath}\" INSTALLFOLDER=\"{installFolder}\" /passive /norestart /l*v \"{log}\"",
            $"start \"\" \"{exe}\"",
            ""));

        Logger.Info("Installing {Msi} into {Folder} and restarting", msiPath, installFolder);
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = DownloadFolder,
        });

        Application.Current.MainWindow?.Close();
    }

    private static (string Owner, string Repository) Repository()
    {
#if DEBUG
        // End-to-end testing against a scratch repository, e.g. SRS455_UPDATE_REPO=someone/srs-update-test.
        var overrideRepo = Environment.GetEnvironmentVariable("SRS455_UPDATE_REPO");
        if (overrideRepo?.Split('/') is [var testOwner, var testRepository])
            return (testOwner, testRepository);
#endif
        return (ForkBranding.UpdateRepositoryOwner, ForkBranding.UpdateRepositoryName);
    }

    // Installers from earlier updates are not needed once the client runs again.
    private static void CleanDownloads()
    {
        try
        {
            if (!Directory.Exists(DownloadFolder))
                return;
            foreach (var msi in Directory.EnumerateFiles(DownloadFolder, "*.msi"))
                File.Delete(msi);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Could not clean old update downloads");
        }
    }
}
