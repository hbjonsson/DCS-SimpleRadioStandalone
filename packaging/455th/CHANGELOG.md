# 455th vAEW SRS Changelog

## 2.4.1.0+4

### Fixes
- **Overlay and cockpit radio volume work again for OpenFreq AM.** Radio loudness is applied after OpenFreq AGC and clip, so overlay sliders and in-jet volume knobs change what you hear. Full volume stays bit-identical to the previous full-knob path.
- **RX/TX radio tones follow the radio volume knob.** Generic clicks, KY-58, MIDS, and intercom tones scale with radio volume (including mute at zero).
- **`NoAudioEffects` / global-frequency override still respects volume.** Effects are skipped, but the radio knob still applies.
- **Failed decrypt static respects volume** and stays off the OpenFreq AM demodulator.
- **Mixed receive batches no longer break OpenFreq AM.** Clear AM is partitioned from FM / failed-decrypt / no-effects segments so one non-AM segment cannot force all AM onto the SRS mixer after its TX stage was skipped.
- **FM capture selects the strongest signal.** Capture now uses the lowest combined distance/LOS loss (previous comparison preferred the weakest). Capture applies to FM and SINCGARS; HaveQuick remains outside FM capture.

### Enhancements
- **Modulation-safe OpenFreq routing.** OpenFreq owns eligible clear AM only. FM, SINCGARS, HaveQuick, MIDS, SATCOM, and intercom keep their established SRS character (filtering, NATO/HQ tones, capture, ambient rules).
- **OpenFreq AM drain no longer bleeds across mode switches.** The post-TX squelch tail continues only for OpenFreq-AM-only activity; fallback content cancels drain.
- **Settings clarity without moving familiar controls.** Short help text under the OpenFreq toggle plus tooltips on SRS effect controls that OpenFreq AM bypasses. Control order, labels, and ranges are unchanged.
- KY-58 encryption tones remain audible under OpenFreq AM (generic AM clicks stay suppressed; encryption tone path is unchanged).

### Install notes
- Use the MSI for in-place upgrade into `Program Files\455th-SRS\Client`.
- ZIP remains available as a portable fallback.
- Based on upstream SRS **2.4.1.0**.

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
