These fixtures were generated for Folio's native decoder regression tests from synthetic sources.
Tests decode the committed files through Windows Media Foundation; FFmpeg is only needed to regenerate them.

```powershell
ffmpeg -f lavfi -i "color=red:size=64x48:rate=10:duration=1" -c:v libx264 -bf 0 -pix_fmt yuv420p -an red.mp4
ffmpeg -f lavfi -i "sine=frequency=440:sample_rate=22050:duration=0.1" -ac 1 -c:a libmp3lame tone.mp3
```
