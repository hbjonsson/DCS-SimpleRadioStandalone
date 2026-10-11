455th SRS
=========

Run Install-455th-SRS.cmd to install the client for the current Windows user.

The client installs to:
  %LOCALAPPDATA%\Programs\455th-SRS\Client

Executable:
  %LOCALAPPDATA%\Programs\455th-SRS\Client\455th-SR-ClientRadio.exe

Log:
  %LOCALAPPDATA%\Programs\455th-SRS\Client\clientlog.txt

Its settings are separate from official SRS:
  %LOCALAPPDATA%\455th-SRS

Re-running the installer updates the existing Client folder in place. It does
not create a second installation.

The preferred wing distribution is the MSI. Running a newer MSI upgrades the
existing Apps & Features entry in place - no uninstall first.

The installer does not modify the official SRS installation or the DCS-SRS
scripts in Saved Games. Keep official SRS installed so those scripts remain
available.

Official SRS and 455th SRS can be installed side by side. Do not run both
clients against DCS simultaneously: both listen on the same DCS export ports.

In DCS Options -> Special -> DCS-SRS, disable Auto Launch SRS so official SRS
does not start while you use 455th. AutoConnect still works if 455th is already
running.

Run Uninstall-455th-SRS.cmd from this package to remove the client. Settings
are retained by default.
