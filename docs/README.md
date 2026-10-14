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
6. Turn on **Capture enabled**.

This saves the same `rpcServer` setting that selecting an SSDP device does, so it persists across restarts and the SSDP list can still be used at any time.

## Using HyperTizenUI with the remote

Settings are on the left, status on the right. UP / DOWN move between controls (the settings column scrolls to keep the selected one in view), OK presses buttons and opens the keyboard on text fields, and on settings shown as `< value >` or `A | B | C`, LEFT / RIGHT step through the choices while OK cycles them. Only the settings for the selected output are shown.

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

The modes do not change how many screen points are sampled: 14 points around the edge (4 top, 3 right, 4 bottom, 3 left) are mapped one-to-one onto the image HyperHDR receives, and the firmware decides how many are read per round. They also do not smooth colours; HyperHDR's own smoothing stays in charge of LED transitions.

### Target FPS

The **Target FPS** button under Capture Quality sets how fast the service captures: 10, 15, 20, 25, 30 or 40. Press [OK] to cycle through the values, or [LEFT] / [RIGHT] to step down / up. The change applies to the next frame and is remembered (needs native 1.0.2+).

Capture, image and send time count toward each frame, so the service only waits for whatever is left of the frame interval. If Capture FPS stays below the target, the TV's capture API is the limit: each frame reads the screen in batches and the firmware asks for a pause (`sleepMs` in Condition) after every batch, so the ceiling is roughly 1000 / ((16 / points) x sleepMs) frames per second.

## WLED Direct (native 1.0.4+)

HyperTizen can drive a [WLED](https://kno.wled.ge/) controller itself, with no HyperHDR and no PC:

```
TV capture -> 14 edge samples -> LED layout + interpolation -> DDP over UDP -> WLED -> strip
```

**Output** at the top of HyperTizenUI picks where frames go: **HyperHDR** (the default, unchanged) or **WLED Direct**. Only one is active; switching is safe while capturing and only that output's settings are shown.

### Setup

1. Set **Output** to **WLED Direct**.
2. Enter the WLED IP (for example `192.168.1.227`) and DDP port **4048**, then **Save WLED Target**.
3. Press **Test WLED > Red**. Every LED on the strip should turn red for 10 seconds. This works with capture off and uses the same DDP sender as live capture, so if it works, the network path is good.
4. Under **LED Layout**, enter the number of LEDs on each side of the TV, pick the **Start corner** (where the strip's first LED is) and **Direction** (which way it runs), as seen from the front of the TV, then **Save LED Layout**. Top + Right + Bottom + Left should equal the LED count in WLED's LED Preferences.
5. Turn on **Capture enabled**.

WLED needs **Receive UDP realtime** on (Config > Sync Interfaces, on by default). If HyperTizen stops sending, WLED returns to its own effect after its realtime timeout (2.5 s by default).

### LED mapping

The layout is one loop clockwise from the top-left corner: top left to right, right top to bottom, bottom right to left, left bottom to top. Each side's LEDs are coloured only from that side's capture points (4 top, 3 right, 4 bottom, 3 left), linearly interpolated by position; LEDs beyond the outermost point on a side take that point's colour. The strip is that loop entered at the start corner and walked in the chosen direction. Test Red and the other test colours fill the whole layout (2048 LEDs if no layout is saved yet; WLED ignores pixels past its strip).

### Picture settings (WLED Direct only)

| Setting | Effect |
|---|---|
| Brightness 0-100% | Scales every LED before sending. WLED's own brightness setting is never changed |
| Black threshold 0-10% | An LED whose red, green and blue are all below this share of full scale is sent as off. Judged before brightness |
| Smoothing On / Off | Time-based exponential smoothing; the time constant follows Capture Quality: Fast 50 ms, Balanced 90 ms, Smooth 150 ms. Off sends each frame as captured |

HyperHDR output is not affected by these; HyperHDR does its own processing.

### DDP details

Packets follow WLED's receiver (`wled00/e131.cpp`): 10-byte header, version 1, data type RGB 8-bit (`0x0B`), destination 1, a 32-bit byte offset and 16-bit length, PUSH on the last packet of a frame, sequence numbers 1-15. Each packet carries at most 1440 bytes (480 LEDs), the same split WLED's own DDP output uses: 1478 bytes with UDP and IP headers, inside a 1500-byte MTU, so packets are never IP-fragmented. 300 LEDs is one packet per frame, 600 is two, 4000 is nine.

### Status

Native Capture Status shows the output, its target and state (`ready`, `sending`, `testing`, `send error`, `no LED layout`, `not configured`; UDP has no connection, so there is no "connected"), LED count, packets per frame, last send time, DDP frames, packets and errors, and the last output error. A failed send never stops capture: it is counted and retried after 1 second. **Peak sample** is the largest raw value the capture API returned; above 255 means the TV returns more than 8 bits per channel, which both outputs currently clip at 255.

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