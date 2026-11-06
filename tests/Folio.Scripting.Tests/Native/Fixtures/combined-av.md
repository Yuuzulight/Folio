Synthetic combined A/V fixtures (generated with existing FFmpeg 8.1.2; no production dependency)

Regenerate from the repository root with the development-only commands below. These are generated synthetic colors and sine tones, with no external input assets. FFmpeg is not invoked by tests.

```powershell
ffmpeg -hide_banner -loglevel error -f lavfi -i "color=red:size=64x48:rate=10:duration=1" -itsoffset 0.25 -f lavfi -i "sine=frequency=440:sample_rate=48000:duration=0.5" -map 0:v -map 1:a -c:v libx264 -bf 0 -pix_fmt yuv420p -c:a aac -ac 2 -b:a 96k -movflags +faststart tests/Folio.Scripting.Tests/Native/Fixtures/av-offset-stereo.mp4
ffmpeg -hide_banner -loglevel error -f lavfi -i "color=blue:size=64x48:rate=10:duration=0.3" -f lavfi -i "sine=frequency=660:sample_rate=48000:duration=0.8" -map 0:v -map 1:a -c:v libx264 -bf 0 -pix_fmt yuv420p -c:a aac -ac 1 -b:a 64k -movflags +faststart tests/Folio.Scripting.Tests/Native/Fixtures/av-long-mono.mp4
ffmpeg -hide_banner -loglevel error -f lavfi -i "color=green:size=64x48:rate=10:duration=0.2" -f lavfi -i "sine=frequency=440:sample_rate=48000:duration=0.2" -map 0:v -map 1:a -c:v libx264 -bf 0 -pix_fmt yuv420p -c:a aac -ac 6 -b:a 192k -movflags +faststart tests/Folio.Scripting.Tests/Native/Fixtures/av-surround.mp4
```

FFprobe confirms av-offset-stereo.mp4 has 10 H.264 frames starting at zero, video duration 1s, stereo AAC at 48000Hz starting at 0.228s with duration 0.521333s. The requested 0.25s offset includes an AAC priming packet. Audio ends before video.

av-long-mono.mp4 has 3 H.264 frames, video duration 0.3s, and mono AAC at 48000Hz with duration 0.8s. Video ends before audio.

av-surround.mp4 has 2 H.264 frames and 5.1 AAC at 48000Hz; its test verifies Media Foundation negotiates stereo output.

Tests verify both stream tails survive, actual decoded mono/stereo formats, preserved leading silence, aggregate budget rejection, exact retained-output budget acceptance, and decoding after failure. Pure timeline tests additionally cover preroll, overlap, discontinuity silence, malformed channel alignment, decreasing timestamps, and excessive timestamp gaps. Native tests decode only local fixtures and never open an audio output or capture device.

The aggregate limit bounds retained decoded float samples plus RGBA frames; timeline silence is additionally checked before allocation. Raw preroll/overlap packets are charged until timeline assembly, so budgets can conservatively reject a stream even when its final trimmed output would fit. The exact combined-budget test uses the positive-offset stereo fixture with no trimmed preroll.

Offset regression diagnosis (Windows MF, first integration wave): native AAC and decoded float samples both start at timestamp zero, and the first decoded value is -1.0667050673873746e-06. FFprobe reports a first packet at 0.228s with normal edit handling and at zero with `-ignore_editlist 1`. The file's audio edit list has a 228ms initial empty edit followed by 522ms of media. This is a real presentation offset, not PCM noise to tolerate away.

The decoder parses bounded version-0/version-1 unit-rate edits (one media edit, optionally preceded by one empty edit), hides the edit container as a same-size `free` box only in its temporary input copy, and applies the presentation mapping itself. Original fixture bytes and sample/chunk offsets are unchanged. Multiple same-kind tracks, repeated/dwell/rate-changing edits, and malformed metadata fail closed. Audio is clipped to its media edit window, including AAC preroll and trailing encoder padding; video outside its edit window is drained but not retained.

A build-free direct Windows MF probe confirmed the unchanged first sample/timestamp after hiding the edits. Applying the parsed mapping to actual MF PCM yielded zero peak through 0.2s, first signal above 0.01 at 0.249375s, and an audio endpoint of 0.75s. This diagnostic is not a substitute for rerunning the C# regressions after build coordination. Silence assertions remain exact and now report one aggregate peak/onset instead of dumping every sample.
