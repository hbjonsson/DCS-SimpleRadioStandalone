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
    public const int ForkBuild = 5;

    /// <summary>User-facing version, e.g. 2.4.1.0+2.</summary>
    public static string DisplayVersion => $"{BaseVersion}+{ForkBuild}";

    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "455th-SRS");

    /// <summary>Default transmission recordings folder under the per-user settings directory.</summary>
    public static string RecordingsDirectory => Path.Combine(SettingsDirectory, "Recordings");
}
