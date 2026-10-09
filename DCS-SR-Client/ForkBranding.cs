using System;
using System.IO;

namespace Ciribob.DCS.SimpleRadio.Standalone.Client;

/// <summary>
/// Side-by-side identity for the 455th SRS client fork.
/// Network protocol and DCS integration remain stock SRS-compatible.
/// </summary>
public static class ForkBranding
{
    public const string ProductName = "455th SRS";
    public const string ExecutableName = "455th-SR-ClientRadio.exe";
    public const string SingleInstanceMutex = "455th-DCS-SimpleRadioStandalone";
    public const string RegistryKey = @"HKEY_CURRENT_USER\SOFTWARE\455th-DCS-SR-Standalone";

    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "455th-SRS");
}
