# 455th vAEW SRS

<p align="center">
  <img src="docs/455th-vaew-logo.png" alt="455th vAir Expeditionary Wing" width="220" />
  &nbsp;&nbsp;&nbsp;
  <img src="packaging/455th-msi/455th-srs-icon.png" alt="455th vAEW SRS app icon" width="220" />
</p>

A 455th-maintained SRS client for DCS World. Built on [DCS SimpleRadio Standalone](https://github.com/ciribob/DCS-SimpleRadioStandalone) and the [OpenFreqAudio](https://github.com/UOAF/OpenFreqAudio) receiver DSP, then extended for AEW / multiplayer use — not a straight drop-in of either project.

**Download:** https://github.com/hbjonsson/DCS-SimpleRadioStandalone/releases/latest

Prefer the MSI for in-place upgrades into `Program Files\455th-SRS\Client`. A ZIP portable build is also published on each release.

## What’s different in this fork

Based on upstream SRS **2.4.1.0**, with fork iterations published as `2.4.1.0+N`. Full cockpit / FC3 integration from SRS is retained.

### Radio audio (OpenFreq + 455th work)

- **OpenFreq AM path by default** — clear AM runs through OpenFreq envelope / AGC / squelch / noise / band-pass instead of the stock SRS AM effects stack.
- **Modulation-safe routing** — OpenFreq owns eligible clear AM only. FM, SINCGARS, HaveQuick, MIDS, SATCOM, and intercom keep SRS filtering, tones, capture, and ambient rules. Mixed receive batches partition AM from non-AM so one foreign segment cannot knock all AM off OpenFreq.
- **Natural squelch drain** — post-TX squelch tail stays on OpenFreq-AM-only activity; switching modes does not leave a wrong drain bleeding across.
- **Working radio volume on OpenFreq AM** — overlay sliders and in-jet knobs apply after AGC/clip; RX/TX tones (including KY-58 / MIDS / intercom) follow the knob, including mute at zero.
- **Strongest-signal FM capture** — FM / SINCGARS capture picks the lowest combined distance/LOS loss (HaveQuick stays outside FM capture).
- **Hybrid cockpit ambience** — procedural ambience blended with WAV assets, with ground / stationary cases and event-driven flutter / deep-fade behaviour tuned for this client.

### Client reliability & recording

- **Auto-reconnect** after unexpected server drops (backoff ~2s → 30s, in-window status, no focus-stealing dialogs). Intentional disconnect, app close, idle timeout, and version mismatch do not reconnect.
- **Recording ready for debrief** — transmission recording on by default for new installs; configurable recordings folder; UTC filenames for Tacview / media sync; a failed recorder start no longer kills radio audio.

### Packaging & product

- **Side-by-side install** — separate 455th product, icon, and settings under `%LOCALAPPDATA%\455th-SRS`. MSI upgrades in place (including same upstream version via `+N` builds). Does not modify official SRS or its DCS scripts.
- **English-only UI** for this fork build.

See [packaging/455th/CHANGELOG.md](packaging/455th/CHANGELOG.md) for per-release detail.

## Install notes

- Official SRS and 455th vAEW SRS can be installed together. Do not run both clients against DCS at once (same DCS export ports).
- In DCS **Options → Special → DCS-SRS**, disable **Auto Launch SRS** so official SRS does not start while you use 455th. AutoConnect still works if 455th is already running.
- Keep official SRS installed if you still want its Saved Games scripts; this client does not replace them.

## Credits & licensing

This is a **modified** version of DCS SimpleRadio Standalone. Credit for the original radio client belongs to **ciribob** and the SRS contributors:

- Upstream SRS: https://github.com/ciribob/DCS-SimpleRadioStandalone
- Upstream releases and donation links remain on that repository

OpenFreq AM demodulation / DSP primitives come from **OpenFreqAudio** (Mozilla Public License 2.0). Integration into SRS, modulation-safe routing, volume/tone behaviour, ambience hybridisation, reconnect, recording UX, and packaging are 455th fork work on top of that base:

- OpenFreqAudio: https://github.com/UOAF/OpenFreqAudio

SRS-derived code remains under **GNU GPL v3** (see [LICENSE](LICENSE)). OpenFreqAudio remains under **MPL-2.0**. Corresponding source for this fork:

- https://github.com/hbjonsson/DCS-SimpleRadioStandalone

Installer distribution notices: [packaging/455th/SOURCE.txt](packaging/455th/SOURCE.txt).
