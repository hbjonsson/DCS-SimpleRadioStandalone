# 455th SRS MSI

`publish-455th.ps1` builds a self-contained x64 MSI using WiX Toolset 5.0.2.

The MSI:

- installs to `Program Files\455th-SRS\Client`;
- creates Desktop and Start Menu shortcuts pointed at that Client folder;
- registers with Windows Apps & Features for in-place upgrades and uninstall;
- uses a stable UpgradeCode so newer MSIs replace the existing install (no
  uninstall first, no second product entry);
- displays a GPLv3/MPLv2/MIT notice without requiring acceptance;
- installs the GPLv3, OpenFreqAudio MPLv2, and corresponding-source notices;
- keeps settings isolated in `%LOCALAPPDATA%\455th-SRS`; and
- does not modify official SRS or its DCS scripts.

Paths after install:

- exe: `C:\Program Files\455th-SRS\Client\455th-SR-ClientRadio.exe`
- log: `C:\Program Files\455th-SRS\Client\clientlog.txt`

MSI ProductVersion stays aligned with upstream SRS (`2.4.1.0`). Windows Installer
compares `major.minor.build` only, so same-version fork releases rely on
`AllowSameVersionUpgrades` plus a new ProductCode each build. User-facing
iteration is SemVer build metadata in the window title (`2.4.1.0+N`). When
upstream ships `2.4.2.0`, adopt that base and reset fork build to `+1`.

Build both the MSI and fallback ZIP from the repository root:

```powershell
.\publish-455th.ps1
```
