using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Helpers;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Settings;
using MahApps.Metro.Controls;
using NLog;

namespace Ciribob.DCS.SimpleRadio.Standalone.Client.UI.UpdateWindow;

/// <summary>
/// Offers a newer fork release: Update now (MSI installs), Later, or Skip this version.
/// </summary>
public partial class UpdateWindow : MetroWindow
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly ForkRelease _release;
    private readonly string _msiClientFolder = ForkBranding.MsiClientFolder;
    private CancellationTokenSource _download;

    public UpdateWindow(ForkRelease release)
    {
        InitializeComponent();
        _release = release;

        Headline.Text = $"{ForkBranding.ProductName} {release.DisplayVersion} is available. "
                        + $"You have {ForkBranding.DisplayVersion}.";
        Notes.Text = string.IsNullOrWhiteSpace(release.Notes) ? release.Name : release.Notes.Trim();
        DcsWarning.Visibility = Process.GetProcessesByName("dcs").Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_msiClientFolder == null)
        {
            PortableNote.Visibility = Visibility.Visible;
            UpdateNow.Visibility = Visibility.Collapsed;
            OpenPage.Visibility = Visibility.Visible;
            OpenPage.IsDefault = true;
        }
    }

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        UpdateNow.IsEnabled = Skip.IsEnabled = false;
        Progress.Visibility = Status.Visibility = Visibility.Visible;
        Status.Text = $"Downloading {_release.DisplayVersion}...";
        _download = new CancellationTokenSource();

        try
        {
            var msiPath = Path.Combine(ForkUpdateManager.DownloadFolder, Path.GetFileName(new Uri(_release.MsiUrl).LocalPath));
            await ForkUpdates.DownloadMsiAsync(ForkUpdateManager.Http, _release, msiPath,
                new Progress<double>(fraction => Progress.Value = fraction), _download.Token);

            Status.Text = "Starting the installer...";
            ForkUpdateManager.InstallAndRestart(msiPath, _msiClientFolder);
        }
        catch (OperationCanceledException)
        {
            // Window closed during the download.
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Update to {Version} failed", _release.DisplayVersion);
            Status.Text = $"The update couldn't be downloaded: {ex.Message}";
            UpdateNow.Visibility = Visibility.Collapsed;
            OpenPage.Visibility = Visibility.Visible;
            Skip.IsEnabled = true;
        }
    }

    private void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(_release.PageUrl) { UseShellExecute = true });
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e) => Close();

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        GlobalSettingsStore.Instance.SetClientSetting(GlobalSettingsKeys.SkippedUpdateVersion, _release.Tag);
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _download?.Cancel();
        base.OnClosed(e);
    }
}
