# 455th vAEW SRS Changelog

## 2.4.1.0+7

### Enhancements
- **Every radio now uses the OpenFreq receiver sound, not just AM.**
  - **FM and SINCGARS** use a new FM receiver modelled on the AN/ARC-210 FM mode: hiss falls away on strong signals, weak signals get noisy and clicky, the stronger of two stations takes over, and there is a short burst of squelch noise when the other station unkeys.
  - **Encrypted voice (KY-58), MIDS and SATCOM** use 16 kbit/s CVSD digital voice: a slightly grainy, robotic tone with no hiss. Encrypted radio garbles near the edge of range.
  - **Intercom** is wired: wider and clearer than radio, with no static.
- **Much louder radio audio.** Voice plays about 12 dB louder than in +6 at the same volume settings, and every kind of radio plays at the same level. A soft limiter replaces the hard clip at full volume, so overlapping radios and a high Speaker Boost duck briefly instead of crackling.
- **Cockpit background stays under the voice.** Each talker's voice is levelled before their cockpit noise is added, so engine noise no longer swells to voice level between words. It now sits about 17 dB under the voice for every talker.
- **Automatic updates.** At startup the client checks for a newer 455th release and offers Update now, Later or Skip this version. Update now downloads the installer, verifies it, installs it and restarts the client. Settings → "Check for updates at startup" turns this off, "Check now" checks on demand, and "Include test (pre-release) versions" opts into test builds. Portable ZIP copies get a link to the download page instead.

### Fixes
- **No more crack when a transmission starts.** Squelch opening no longer sends a burst over full scale into the audio.
- **The mic passthrough output (used as a stream source) now carries the radio sound.** It used to play your plain microphone. Own-voice recordings now use the processed audio too.

### Removed
- The "Use OpenFreq Receive Effects" switch, and the settings the OpenFreq sound replaces: radio effects volume, per-radio-model effects, clipping, NATO tone, HAVEQUICK tone, background noise and noise gain. Players who prefer stock SRS sound can use the official SRS client; both still work together on the same servers.

### Install notes
- **Install this version manually once.** +6 and earlier cannot update themselves; from +7 on the client offers updates on its own.
- Use the MSI for in-place upgrade into `Program Files\455th-SRS\Client`.
- ZIP remains available as a portable fallback, but it cannot update itself.
- Based on upstream SRS **2.4.1.0**.

## 2.4.1.0+6

### Enhancements
- **More realistic OpenFreq AM radio sound, tuned to the AN/ARC-210 specification (MIL-R-29583).** Each transmitting station now has automatic level control and a soft speech clipper, so every voice is driven to about 85% modulation regardless of mic level. Loud syllables pick up the characteristic radio grit, and quiet and loud talkers come out at similar levels.
- **Receiver passband matches the ARC-210 spec.** A steeper 7th-order high-pass gives the thinner, bass-cut radio voice (150 Hz now at least 37 dB down).
- **Custom sounds folder for mic clicks.** Settings → Custom Sounds Folder lets you keep your own `RADIO_TRANS_START/END` and `INTERCOM_TRANS_START/END` .wav files (48 kHz, 16-bit, mono) outside the install folder, so updates no longer overwrite them. A file with the same name as a bundled sound replaces it.

### Fixes
- **EAM: VHF radios can now select ENC and fill.** Previously only the UHF and FM radios in External AWACS Mode offered encryption.
- Reloading sound effects can no longer briefly leave the effect lists empty while audio is playing.

### Install notes
- Use the MSI for in-place upgrade into `Program Files\455th-SRS\Client`.
- ZIP remains available as a portable fallback.
- Based on upstream SRS **2.4.1.0**.

## 2.4.1.0+5

### Fixes
- **Session recordings now capture post-DSP receive audio.** OpenFreq AM and classic SRS radio character (filtering, noise, AGC, squelch, levels) and OpenFreq post-TX drain tails are recorded the same way they are heard, instead of dry pre-effects PCM.

### Enhancements
- **Recording permission is now an application policy.** Using the 455th SRS client implies transmissions may be saved; the obsolete per-user recording opt-out and replacement-tone settings have been removed. Legacy `AllowRecord` wire metadata is ignored for recording decisions.

### Install notes
- Use the MSI for in-place upgrade into `Program Files\455th-SRS\Client`.
- ZIP remains available as a portable fallback.
- Based on upstream SRS **2.4.1.0**.

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
