# TizenGlow

TizenGlow lights WLED or HyperHDR LEDs from what's on a Samsung Tizen TV's screen. It is based on [HyperTizen](https://github.com/reisxd/HyperTizen) by Reis Can; see [Credits and license](../README.md#credits-and-license).

# Installation

TizenGlow has two parts, and both are needed:

- **The TV service** (`io.gh.duckieonquacks.TizenGlow-<version>.tpk`): a background app with no screen of its own that captures the picture and sends it to HyperHDR or WLED.
- **The settings page** (TizenGlowUI): opened through [TizenBrew](https://github.com/reisxd/TizenBrew), which also starts the service.

You need a Samsung TV with Tizen 6.5 or newer (2022+).

1. Download the TPK from the [latest release](https://github.com/DuckieOnQuacks/TizenGlow/releases/latest). The release notes list its SHA-256.
2. The TPK is signed only with the Tizen SDK's test certificates, so sign it with your own Samsung certificate and install it. TizenBrew Installer does both; with Tizen Studio see [Resigning the package](#resigning-the-package) and then run `tizen install -n io.gh.duckieonquacks.TizenGlow-<version>.tpk`.
3. Install TizenBrew on the TV if you haven't ([guide](https://github.com/reisxd/TizenBrew/blob/main/docs/README.md)).
4. In TizenBrew's module manager ([GREEN] button) add the GitHub module `duckieonquacks/tizenglow`. Without a version it always loads the newest release; add `@v<version>` (for example `duckieonquacks/tizenglow@v1.0.0`) to stay on one release.
5. Open the module. The top bar shows `v<version>` when the service and the page match. If it says which one to update instead, the other half is out of date: install the TPK from the same release, or re-add the module. A page-only release (its notes say so) also works with an older service; the top bar then shows both versions without a warning.

Updating: install the new release's TPK. The page updates by itself (it can take a few hours to reach the TV because of CDN caching; pin `@v<version>` to get it immediately). Your settings are kept as long as the new TPK installs over the old one; if the TV makes you uninstall first (different signing certificate), enter them again.

## Manual HyperHDR server (when SSDP discovery fails)

If the "SSDP Devices" list stays empty (multicast blocked, VLANs, firewall, etc.), you can point TizenGlow at your HyperHDR / Hyperion server directly:

1. Open TizenGlowUI and wait for the status to say `Connected`.
2. Scroll down to **Manual HyperHDR Server** with the D-pad.
3. Enter the server's IP address or hostname (e.g. `192.168.1.50`). Press [OK] on the field to open the TV keyboard.
4. Enter the WebSocket port. This is the HyperHDR / Hyperion **web interface port** (default `8090`), not the FlatBuffers (`19400`) or JSON (`19444`) port.
5. Select **Use Manual Server**. The UI shows `Currently Connected to ws://HOST:PORT`.
6. Turn on **Capture enabled**.

This saves the same `rpcServer` setting that selecting an SSDP device does, so it persists across restarts and the SSDP list can still be used at any time.

## Using TizenGlowUI with the remote

Settings are on the left, status on the right. UP / DOWN move between controls (the settings column scrolls to keep the selected one in view), OK presses buttons and opens the keyboard on text fields, and on settings shown as `< value >` or `A | B | C`, LEFT / RIGHT step through the choices while OK cycles them. Only the settings for the selected output are shown.

## Native Capture Status

The service finds the capture API at runtime: it opens `/usr/lib/libvideoenhance.so` and uses a function family only if all three of its RGB-measure exports exist (`ppi_ve_*` on Tizen 9/10 firmware, `ve_*` on Tizen 7/8, `cs_ve_*` on older TVs). The Tizen version only decides which complete family is tried first: the one the original HyperTizen used on that version, so TVs that worked with it make the same calls.

TizenGlowUI shows what the service found:

| Field | Meaning |
|---|---|
| State | `idle` (capture disabled), `starting`, `running` (frames are being captured), `stopped`, `no-server` (no HyperHDR address saved), `unsupported` (no complete API), `error` (see Last error) |
| API | `ppi_ve`, `ve`, `cs_ve` or `unsupported`, plus the platform version |
| Condition | What the panel reported: result code, capture points, resolution, pixel density, sleep |
| Last error | Timestamped error with exception, API, library path, platform version and which symbol families were complete, partial or missing |

`not reported` means the TizenGlow service isn't installed or isn't running.

### Performance statistics

Measured by the service over a rolling 2 second window and refreshed in the UI once a second. Nothing is shown until 1 second of data exists, and the Performance verdict waits for a full window. The window and counters reset when capture is enabled, when the Capture Quality mode changes and when Target FPS changes; reopening the UI does not reset them.

| Field | Meaning |
|---|---|
| Mode / Target FPS | Active Capture Quality mode and the frame rate the loop paces to |
| Capture FPS | Frames that came back from the TV's capture API per second |
| Send FPS | Frames HyperHDR actually received per second (0 while it is unreachable) |
| Avg capture time | Time in the capture API per frame (sampling the 14 screen points) |
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

The **Target FPS** button under Capture Quality sets how fast the service captures: 10, 15, 20, 25, 30 or 40. Press [OK] to cycle through the values, or [LEFT] / [RIGHT] to step down / up. The change applies to the next frame and is remembered.

Capture, image and send time count toward each frame, so the service only waits for whatever is left of the frame interval. If Capture FPS stays below the target, the TV's capture API is the limit: each frame reads the screen in batches and the firmware asks for a pause (`sleepMs` in Condition) after every batch, so the ceiling is roughly 1000 / ((14 / points) x sleepMs) frames per second.

## WLED Direct

TizenGlow can drive a [WLED](https://kno.wled.ge/) controller itself, with no HyperHDR and no PC:

```
TV capture -> 14 edge samples -> LED layout + interpolation -> DDP over UDP -> WLED -> strip
```

**Output** at the top of TizenGlowUI picks where frames go: **HyperHDR** (the default) or **WLED Direct**. Only one is active; switching is safe while capturing and only that output's settings are shown.

### Setup

1. Set **Output** to **WLED Direct**.
2. Enter the WLED IP (for example `192.168.1.227`) and DDP port **4048**, then **Save WLED Target**.
3. Press **Test WLED > Red**. Every LED on the strip should turn red for 10 seconds. This works with capture off and uses the same DDP sender as live capture, so if it works, the network path is good.
4. Under **LED Layout**, enter the number of LEDs on each side of the TV, pick the **Start corner** (where the strip's first LED is) and **Direction** (which way it runs), as seen from the front of the TV, then **Save LED Layout**. Top + Right + Bottom + Left should equal WLED's own LED count: TizenGlow reads that count from WLED when you save the target (shown under the target and as **WLED LEDs** in status) and warns under **Total** while you type a layout that doesn't match.
5. Turn on **Capture enabled**.

WLED needs **Receive UDP realtime** on (Config > Sync Interfaces, on by default). If TizenGlow stops sending, WLED returns to its own effect after its realtime timeout (2.5 s by default).

### LED mapping

**LED shift** (under LED Layout) moves the colours round the strip one LED at a time and applies at once: + clockwise, - counter-clockwise, whichever way the strip runs. Use it when the counts are right but the corners are a few LEDs off, usually because the strip's first LED isn't exactly at the start corner. Easiest on the calibration screen: Four Edges, then UP / DOWN until each colour ends at its corner. It is kept when the layout is saved again.

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

## Calibration screen

**Open Calibration Screen** (under Output) fills the TV with test patterns. The TV shows them like any other picture, so they go through the whole path: screen, Samsung capture, TizenGlow, then WLED Direct or HyperHDR, then the LEDs. Nothing is sent to the LEDs directly; **Test WLED** is still the test for the network side only. Capture must be enabled and running (the info box warns when it isn't).

| Pattern | What the LEDs should do |
|---|---|
| Full Red, Green, Blue, White (`#FF0000`, `#00FF00`, `#0000FF`, `#FFFFFF`) | Every LED that colour |
| Full Black (`#000000`) | All off; checks the black threshold |
| Hue Gradient | Red, yellow, green, cyan, blue, magenta, red from left to right (top and bottom edges) |
| Four Edges | Top red, right green, bottom blue, left yellow |
| Four Corners | Top-left red, top-right green, bottom-right blue, bottom-left yellow |
| Moving Edge Marker | A white block going clockwise round the edge, 2.5 s per lap; shows orientation, latency and smoothing |
| Edge Rainbow | The rainbow clockwise from the top-left corner, in the same order as round the screen |

Remote: **LEFT / RIGHT** previous / next pattern, **OK** pauses the marker (on other patterns it shows or hides the info box), **UP / DOWN** make the marker faster / slower (1.5 to 10 s per lap) and, on the other patterns with WLED Direct, shift the LEDs one step clockwise / counter-clockwise (see LED shift), **BACK** returns to the settings on the last pattern used.

The info box sits in the middle of the screen, where nothing is sampled, and hides itself (after 2.5 s on the full-screen colours, 8 s otherwise); any key shows it again. To protect OLED panels, the screen closes after 10 minutes without a key press on any still pattern; only the moving marker keeps running.

Edge bands are 12.5% of the screen deep and corner blocks a third of it each way; the capture samples sit 5-7% in from the edges. Colours are exact CSS colours and gradients (no canvas, filters or opacity); the hue gradients have a stop at every 60 degrees, so they are exact hues throughout.

## Releasing a new version

1. Set the same version in `TizenGlow/tizen-manifest.xml`, `package.json` and `uiVersion` in `TizenGlowUI/js/wsClient.js`. Raise `minService` in the same file only when the page needs something new from the service; leave it for a page-only release, whose TPK is then optional.
2. Commit, then push a tag `v<version>` (for example `git tag v1.0.1 && git push origin v1.0.1`).

The [Release workflow](../.github/workflows/release.yml) checks that the three versions and the tag agree, builds the TPK on GitHub and publishes a release with the TPK and its SHA-256. The service's version is taken from `tizen-manifest.xml` at build time and reported to the page, which is how the top bar knows when the two halves differ. Running the workflow by hand (Actions > Release > Run workflow) only builds the TPK, as a downloadable artifact.

## Building the package

Requires the [.NET SDK](https://dotnet.microsoft.com/download) (tested with 8.0.426); the `Tizen.NET.Sdk` build SDK is restored from NuGet automatically, so Tizen Studio is not needed to build.

```bash
dotnet build TizenGlow/TizenGlow.csproj -c Release
```

The package is written to `TizenGlow/bin/Release/tizen90/io.gh.duckieonquacks.TizenGlow-<version>.tpk`. It is signed with the Tizen SDK's default test certificates, which Samsung TVs reject, so re-sign it with your own Samsung certificate (see below, or use TizenBrew Installer) before installing.

## Resigning the package

1. Change the Host PC IP address to your PC's IP address by following [this](https://developer.samsung.com/smarttv/develop/getting-started/using-sdk/tv-device.html#Connecting-the-TV-and-SDK)

2. After following the guide for the Tizen Studio installation, you have to create a certificate profile. You can follow [this guide](https://developer.samsung.com/smarttv/develop/getting-started/setting-up-sdk/creating-certificates.html).

3. Sign the package:
```bash
tizen package -t tpk -s YourProfileName -o path/to/output/dir -- path/to/io.gh.duckieonquacks.TizenGlow.tpk

# Example:
# tizen package -t tpk -s TizenGlow -o release -- io.gh.duckieonquacks.TizenGlow.tpk
```

4. You should now be able to install the package.