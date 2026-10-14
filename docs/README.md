# HyperTizen

HyperTizen is a Hyperion / HyperHDR capturer for Tizen TVs.

# Installation

To install HyperTizen, you need to have a Samsung TV (Tizen) that has at least Tizen 6.5 (2022+).

You'll need Tizen Studio to install the app on your TV. You can download it from the [official website](https://developer.samsung.com/smarttv/develop/getting-started/setting-up-sdk/installing-tv-sdk.html).

1. Download the latest release from the [releases page](https://github.com/reisxd/HyperTizen/releases/latest).

2. Change the Host PC IP address to your PC's IP address by following [this](https://developer.samsung.com/smarttv/develop/getting-started/using-sdk/tv-device.html#Connecting-the-TV-and-SDK)

3. Install the package:
```bash
tizen install -n path/to/io.gh.reisxd.HyperTizen.tpk
```

Note that `tizen` is in `C:\tizen-studio\tools\ide\bin` on Windows and in `~/tizen-studio/tools/ide/bin` on Linux.

If you get `install failed[118, -12], reason: Check certificate error` error, you'll have to resign the package.

4. Install TizenBrew to your TV. Follow [this](https://github.com/reisxd/TizenBrew/blob/main/docs/README.md) guide.

5. Add `reisxd/HyperTizen/HyperTizenUI` as a GitHub module to the module manager. You can access the module manager by pressing the [GREEN] button on the remote.

## Manual HyperHDR server (when SSDP discovery fails)

If the "SSDP Devices" list stays empty (multicast blocked, VLANs, firewall, etc.), you can point HyperTizen at your HyperHDR / Hyperion server directly:

1. Open HyperTizenUI and wait for the status to say `Connected`.
2. Scroll down to **Manual HyperHDR Server** with the D-pad.
3. Enter the server's IP address or hostname (e.g. `192.168.1.50`). Press [OK] on the field to open the TV keyboard.
4. Enter the WebSocket port. This is the HyperHDR / Hyperion **web interface port** (default `8090`), not the FlatBuffers (`19400`) or JSON (`19444`) port.
5. Select **Use Manual Server**. The UI shows `Currently Connected to ws://HOST:PORT`.
6. Tick **Enabled**.

This saves the same `rpcServer` setting that selecting an SSDP device does, so it persists across restarts and the SSDP list can still be used at any time.

## Native Capture Status

Since 1.0.1 the service finds the capture API at runtime: it opens `/usr/lib/libvideoenhance.so` and uses a function family only if all three of its RGB-measure exports exist (`ppi_ve_*` on Tizen 9/10 firmware, `ve_*` on Tizen 7/8, `cs_ve_*` on older TVs). The Tizen version only decides which complete family is tried first: the one v1.0.0 used, so TVs that already worked make the same calls.

HyperTizenUI shows what the service found:

| Field | Meaning |
|---|---|
| State | `idle` (capture disabled), `starting`, `running` (frames are being captured), `stopped`, `no-server` (no HyperHDR address saved), `unsupported` (no complete API), `error` (see Last error) |
| API | `ppi_ve`, `ve`, `cs_ve` or `unsupported`, plus the platform version |
| Condition | What the panel reported: result code, capture points, resolution, pixel density, sleep |
| Last error | Timestamped error with exception, API, library path, platform version and which symbol families were complete, partial or missing |

`not reported` means the installed native service is older than 1.0.1.

### Performance statistics (native 1.0.3+)

Measured by the service over a rolling 2 second window and refreshed in the UI once a second. Nothing is shown until 1 second of data exists, and the Performance verdict waits for a full window. The window and counters reset when capture is enabled, when the Capture Quality mode changes and when Target FPS changes; reopening the UI does not reset them.

| Field | Meaning |
|---|---|
| Mode / Target FPS | Active Capture Quality mode and the frame rate the loop paces to |
| Capture FPS | Frames that came back from the TV's capture API per second |
| Send FPS | Frames HyperHDR actually received per second (0 while it is unreachable) |
| Avg capture time | Time in the capture API per frame (sampling the 16 screen points) |
| Avg frame time | Whole loop iteration per frame: capture + image + send + pacing wait. Frame breakdown shows each part |
| Failed frames | Frames whose capture or image step threw an error |
| Dropped frames | Frame slots the target asked for that passed without a capture because the previous frame was still running (see below) |
| Performance | `target met` (capture FPS at least 90% of target), `capture limited` (capture + image + send alone take at least 90% of a frame interval), `below target` (anything else), `measuring` |

Dropped frames: with a target of 30 FPS there is a frame slot every 33.3 ms. If a frame takes 92 ms, the slots it ran over are not made up later: frames are never queued, run in parallel or sent in a burst to catch up. Those passed slots are counted as dropped (fractionally per frame, shown rounded down), so dropped per second is about target FPS minus capture FPS. Waiting for the next slot when a frame finished early is normal pacing and never counts as dropped.

### Capture Quality

The **Capture Quality** selector under Enabled picks a preset (OK cycles, LEFT / RIGHT step). Balanced is the default. The choice is remembered, applies to the next frame without restarting capture, and also sets Target FPS, which you can then fine-tune.

| | Fast | Balanced | Smooth |
|---|---|---|---|
| Target FPS | 30 | 25 | 15 |
| A frame that overruns its slot | next frame starts at once | next frame starts at once | waits for the next slot of the 15 FPS grid, so frames are always whole intervals apart |
| Pause after a failed frame | 1 s | 5 s | 5 s |
| Condition read retry | 5 s | 10 s | 10 s |

The modes do not change how many screen points are sampled: the 16 points are mapped one-to-one onto the image HyperHDR receives, and the firmware decides how many are read per batch. They also do not smooth colours; HyperHDR's own smoothing stays in charge of LED transitions.

### Target FPS

The **Target FPS** button under Capture Quality sets how fast the service captures: 10, 15, 20, 25, 30 or 40. Press [OK] to cycle through the values, or [LEFT] / [RIGHT] to step down / up. The change applies to the next frame and is remembered (needs native 1.0.2+).

Capture, image and send time count toward each frame, so the service only waits for whatever is left of the frame interval. If Capture FPS stays below the target, the TV's capture API is the limit: each frame reads the screen in batches and the firmware asks for a pause (`sleepMs` in Condition) after every batch, so the ceiling is roughly 1000 / ((16 / points) x sleepMs) frames per second.

## Building the package

Requires the [.NET SDK](https://dotnet.microsoft.com/download) (tested with 8.0.426); the `Tizen.NET.Sdk` build SDK is restored from NuGet automatically, so Tizen Studio is not needed to build.

```bash
dotnet build HyperTizen/HyperTizen.csproj -c Release
```

The package is written to `HyperTizen/bin/Release/tizen90/io.gh.reisxd.HyperTizen-<version>.tpk`. It is signed with the Tizen SDK's default test certificates, which Samsung TVs reject, so re-sign it with your own Samsung certificate (see below, or use TizenBrew Installer) before installing.

## Resigning the package

1. Change the Host PC IP address to your PC's IP address by following [this](https://developer.samsung.com/smarttv/develop/getting-started/using-sdk/tv-device.html#Connecting-the-TV-and-SDK)

2. After following the guide for the Tizen Studio installation, you have to create a certificate profile. You can follow [this guide](https://developer.samsung.com/smarttv/develop/getting-started/setting-up-sdk/creating-certificates.html).

3. Sign the package:
```bash
tizen package -t tpk -s YourProfileName -o path/to/output/dir -- path/to/io.gh.reisxd.HyperTizen.tpk

# Example:
# tizen package -t tpk -s HyperTizen -o release -- io.gh.reisxd.HyperTizen.tpk
```

4. You should now be able to install the package.