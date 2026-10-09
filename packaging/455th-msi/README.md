# 455th SRS MSI

`publish-455th.ps1` builds a self-contained x64 MSI using WiX Toolset 5.0.2.

The MSI:

- installs to `Program Files\455th-SRS`;
- creates Desktop and Start Menu shortcuts;
- registers with Windows Apps & Features for upgrades and uninstall;
- displays a GPLv3/MPLv2/MIT notice without requiring acceptance;
- installs the GPLv3, OpenFreqAudio MPLv2, and corresponding-source notices;
- keeps settings isolated in `%LOCALAPPDATA%\455th-SRS`; and
- does not modify official SRS or its DCS scripts.

Build both the MSI and fallback ZIP from the repository root:

```powershell
.\publish-455th.ps1
```
