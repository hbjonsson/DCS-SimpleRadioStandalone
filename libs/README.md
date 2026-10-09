# Third-party libraries

## OpenFreqAudio

SRS vendors the BASS-free `OpenFreqAudio.Dsp` project for optional OpenFreq-style AM receive effects.

Expected layout (referenced from `Common/Common.csproj`):

```
libs/OpenFreqAudio/OpenFreqAudio.Dsp/OpenFreqAudio.Dsp.csproj
```

The other OpenFreqAudio projects are intentionally ignored. Local development may replace the
`libs/OpenFreqAudio` directory with a junction to an existing OpenFreqAudio checkout:

```bat
mklink /J libs\OpenFreqAudio D:\path\to\OpenFreq\libs\OpenFreqAudio
```

Only `OpenFreqAudio.Dsp` is tracked by this repository so fresh clones build without requiring
the full OpenFreq audio backend.
