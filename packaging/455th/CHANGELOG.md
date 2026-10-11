# 455th vAEW SRS Changelog

## 2.4.1.0+3

### Features
- **Auto-reconnect** after unexpected server drops. If the SRS TCP connection drops while you are connected, the client keeps retrying with backoff (about 2s → 30s) until it recovers or you cancel.
- **Reconnect status** in the main window (`Attempting to reconnect…`) so you can see recovery progress without leaving the cockpit.
- **Auto Reconnect** toggle in Client Settings (on by default).
- **Custom recordings folder** — browse, reset to default, or open the folder from Client Settings. Default recordings path is under your 455th SRS LocalAppData settings directory.
- **UTC recording filenames** (`SRS_yyyyMMddTHHmmssZ…`) for reliable Tacview / external media sync.

### Enhancements
- **Allow Transmissions To Be Recorded** now defaults **on** for new installs (existing saved settings are unchanged).
- Recording start failures no longer kill radio audio — radio keeps working even if recording cannot start.
- New 455th SRS application icon and updated installer artwork.
- English-only UI (Chinese and other satellite language packs removed).

### Fixes
- Uncommanded disconnects no longer leave you stuck offline until you manually press Connect.
- Intentional disconnect, app close, idle timeout, and version mismatch correctly **do not** auto-reconnect.
- Auto-reconnect never steals focus with modal dialogs (safe while flying).
- Clearer disconnect reasons internally so user disconnect vs connection loss is handled correctly.

### Install notes
- Use the MSI for in-place upgrade into `Program Files\455th-SRS\Client`.
- ZIP remains available as a portable fallback.
- Based on upstream SRS **2.4.1.0**.
