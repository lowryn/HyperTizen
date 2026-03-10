# HyperTizen (Performance Fork)

A performance-optimised fork of [reisxd/HyperTizen](https://github.com/reisxd/HyperTizen) — a HyperHDR/Hyperion ambient lighting capturer for Samsung Tizen TVs.

## What this fork improves

The original implementation ran at approximately **3fps** due to several blocking issues. This fork addresses all of them and improves capture zone coverage.

### Bug fixes

**Thread pool starvation (critical)**
The original used `Thread.Sleep()` inside an `async` capture loop. On a resource-constrained Tizen TV, this blocked the .NET thread pool, starving the WebSocket server of threads and making it unresponsive. Fixed by replacing all `Thread.Sleep` calls with `await Task.Delay`.

**Infinite pixel-read loop on invalid positions**
If a capture position is not supported by the hardware, `MeasurePixel` returns an error indefinitely — the original code had no retry limit, causing the loop to spin and block for ~1 second per invalid slot. Fixed with a 20-retry cap per pixel before skipping.

**HyperHDR silently ignoring frames**
`ImageCommand` was missing required fields (`imagewidth`, `imageheight`, `duration`). HyperHDR silently dropped every frame. Added the required fields.

**Pong protocol violation**
`ClientWebSocket` was sending unsolicited Pong frames that HyperHDR rejected. Fixed by setting `KeepAliveInterval = TimeSpan.Zero`.

**Safe batch sizing**
The original could request more capture points per batch than `ScreenCapturePoints` allows, causing index-out-of-range errors. Fixed with `batchSize = Math.Min(ScreenCapturePoints, remaining)`.

### Performance improvements

**Removed unnecessary delay**
A `Task.Delay(50)` in the main capture loop was adding 50ms of dead time per frame on top of the hardware sleep. Removed.

**Faster image rendering**
`SKBitmap.SetPixel` loops replaced with `SKCanvas.DrawRect` — significantly faster for filled rectangles.

**Reduced capture points**
Reduced from 16 capture points (8 batches × 20ms = ~160ms/frame) to 8 points (4 batches × 20ms = ~80ms/frame). The `libvideoenhance.so` API has a mandatory 20ms hardware sleep per batch of 2 points — this is the dominant bottleneck and cannot be reduced.

### Capture zone layout

8 zones, 2 per side:

```
        [top-left]   [top-right]
[left-top ]                    [ right-top]
[left-bottom]                  [right-bottom]
        [bot-left]   [bot-right]
```

All capture positions are validated against Samsung's `libvideoenhance.so` API. Invalid positions cause the hardware to return bad data indefinitely — only positions from the original validated set are used.

## Performance summary

| Version | Points | FPS | Notes |
|---------|--------|-----|-------|
| Original | 16 | ~3fps | Thread.Sleep starvation + missing HyperHDR fields |
| Thread fix only | 16 | ~6fps | Starvation resolved |
| This fork | 8 | ~9fps | Fewer batches + all fixes applied |

LED output via HyperHDR smoothing runs at ~50fps regardless of input rate.

## Hardware notes

Tested on: **Samsung QE55QN90CATXXU** (4K, Tizen 9)

The `libvideoenhance.so` P/Invoke API behaviour:
- `ScreenCapturePoints = 2` — hardware processes 2 positions per batch
- `SleepMS = 20ms` — mandatory hardware wait between `MeasurePosition` and `MeasurePixel`
- Frame time = `ceil(N / ScreenCapturePoints) × SleepMS` + overhead

## Building & installing

```bash
# Build
cd HyperTizen
~/.dotnet/dotnet build -c Release

# Re-sign with your Samsung certificate (required — build always signs with default cert)
cd bin/Release/tizen90
~/tizen-studio/tools/ide/bin/tizen package -t tpk -s <your-cert-profile> -- io.gh.reisxd.HyperTizen-1.0.0.tpk

# Connect TV (must be in Developer Mode)
~/tizen-studio/tools/sdb connect <tv-ip>:26101

# Install
~/tizen-studio/tools/ide/bin/tizen install -n bin/Release/tizen90/io.gh.reisxd.HyperTizen-1.0.0.tpk -s <tv-ip>:26101

# Launch
~/tizen-studio/tools/ide/bin/tizen run -p io.gh.reisxd.HyperTizen -s <tv-ip>:26101
```

## Credits

Original project by [reisxd](https://github.com/reisxd/HyperTizen).
