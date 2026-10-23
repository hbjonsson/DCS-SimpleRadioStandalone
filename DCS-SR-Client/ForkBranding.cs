using System;
using System.IO;

namespace Ciribob.DCS.SimpleRadio.Standalone.Client;

/// <summary>
/// Side-by-side identity for the 455th SRS client fork.
/// Network protocol and DCS integration remain stock SRS-compatible.
/// </summary>
public static class ForkBranding
{
    public const string ProductName = "455th vAEW SRS";
    public const string ExecutableName = "455th-SR-ClientRadio.exe";
    public const string SingleInstanceMutex = "455th-DCS-SimpleRadioStandalone";
    public const string RegistryKey = @"HKEY_CURRENT_USER\SOFTWARE\455th-DCS-SR-Standalone";

    /// <summary>Upstream SRS base version (Assembly/File/MSI ProductVersion).</summary>
    public const string BaseVersion = "2.4.1.0";

    /// <summary>Fork packaging iteration for SemVer build metadata (+N).</summary>
    public const int ForkBuild = 7;

    /// <summary>User-facing version, e.g. 2.4.1.0+2.</summary>
    public static string DisplayVersion => $"{BaseVersion}+{ForkBuild}";

    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "455th-SRS");

    /// <summary>Default transmission recordings folder under the per-user settings directory.</summary>
    public static string RecordingsDirectory => Path.Combine(SettingsDirectory, "Recordings");

    /// <summary>GitHub repository whose releases (tags 455th-v{base}+{build}) the client updates from.</summary>
    public const string UpdateRepositoryOwner = "hbjonsson";
    public const string UpdateRepositoryName = "DCS-SimpleRadioStandalone";

    /// <summary>Where the MSI records its client folder (HKLM, 64-bit view); see packaging/455th-msi/Package.wxs.</summary>
    private const string InstallRegistryKey = @"SOFTWARE\455th-SRS";
    private const string InstallRegistryValue = "ClientFolder";

    /// <summary>
    /// The MSI's client folder when this process runs from it, else null (portable ZIP copy, dev build, or an
    /// install from before the MSI recorded its folder).
    /// </summary>
    public static string MsiClientFolder
    {
        get
        {
            try
            {
                using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(
                    Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
                using var key = hklm.OpenSubKey(InstallRegistryKey);
                if (key?.GetValue(InstallRegistryValue) is not string folder || string.IsNullOrWhiteSpace(folder))
                    return null;

                var installed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
                var running = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
                return string.Equals(installed, running, StringComparison.OrdinalIgnoreCase) ? installed : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
