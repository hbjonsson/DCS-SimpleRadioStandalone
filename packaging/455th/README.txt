455th SRS
=========

Run Install-455th-SRS.cmd to install the client for the current Windows user.

The client installs to:
  %LOCALAPPDATA%\Programs\455th-SRS

Its settings are separate from official SRS:
  %LOCALAPPDATA%\455th-SRS

The installer does not modify the official SRS installation or the DCS-SRS
scripts in Saved Games. Keep official SRS installed so those scripts remain
available.

Official SRS and 455th SRS can be installed side by side. Do not run both
clients against DCS simultaneously: both listen on the same DCS export ports.

Run Uninstall-455th-SRS.cmd from this package to remove the client. Settings
are retained by default.
