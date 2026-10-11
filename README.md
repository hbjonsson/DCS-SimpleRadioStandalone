# 455th vAEW SRS

A modified [DCS SimpleRadio Standalone (SRS)](https://github.com/ciribob/DCS-SimpleRadioStandalone) client for DCS World, maintained by the 455th. It keeps full cockpit and FC3 radio integration from upstream SRS, and adds OpenFreq receiver DSP, hybrid cockpit ambience, and a side-by-side install that does not replace official SRS.

**Download:** https://github.com/hbjonsson/DCS-SimpleRadioStandalone/releases/latest

Prefer the MSI for in-place upgrades into `Program Files\455th-SRS\Client`. A ZIP portable build is also published on each release.

## High-level changes vs upstream SRS

Based on upstream SRS **2.4.1.0**, with fork iterations published as `2.4.1.0+N`.

- **OpenFreq AM receiver DSP** — clear AM receives through OpenFreq (envelope, AGC, squelch, noise, band-pass). FM, SINCGARS, HaveQuick, MIDS, SATCOM, and intercom keep established SRS character.
- **Modulation-safe routing** — OpenFreq owns eligible clear AM only; mixed batches and non-AM modes do not force AM off the OpenFreq path incorrectly.
- **Hybrid cockpit ambience** — procedural ambience blended with WAV assets, including ground/stationary cases.
- **Auto-reconnect** — retries after unexpected server drops (no focus-stealing dialogs while flying); intentional disconnects stay disconnected.
- **Recording defaults & paths** — transmission recording on by default for new installs; configurable recordings folder; UTC filenames for Tacview-friendly sync.
- **Side-by-side install** — separate product, icon, settings under `%LOCALAPPDATA%\455th-SRS`, and branded MSI. Does not modify official SRS or its DCS scripts.
- **English-only UI** for this fork build.

See [packaging/455th/CHANGELOG.md](packaging/455th/CHANGELOG.md) for per-release detail.

## Install notes

- Official SRS and 455th vAEW SRS can be installed together. Do not run both clients against DCS at once (same DCS export ports).
- In DCS **Options → Special → DCS-SRS**, disable **Auto Launch SRS** so official SRS does not start while you use 455th. AutoConnect still works if 455th is already running.
- Keep official SRS installed if you still want its Saved Games scripts; this client does not replace them.

## Credits & licensing

This is a **modified** version of DCS SimpleRadio Standalone. Credit for the original project belongs to **ciribob** and the SRS contributors:

- Upstream SRS: https://github.com/ciribob/DCS-SimpleRadioStandalone
- Upstream releases / donation links live on that repository

OpenFreq receiver DSP is based on **OpenFreqAudio** (Mozilla Public License 2.0):

- https://github.com/UOAF/OpenFreqAudio

SRS-derived code remains under **GNU GPL v3** (see [LICENSE](LICENSE)). OpenFreqAudio remains under **MPL-2.0**. Corresponding source for this fork:

- https://github.com/hbjonsson/DCS-SimpleRadioStandalone

Distribution notices used by the installer are also under [packaging/455th/SOURCE.txt](packaging/455th/SOURCE.txt).

<a href="https://www.jetbrains.com/?from=DCS-SimpleRadioStandalone">Proudly supported by JetBrains Open Source
  <br><br><img src="https://github.com/ciribob/DCS-SimpleRadioStandalone/raw/master/jetbrains-variant-2.png" width="100" /></a>
