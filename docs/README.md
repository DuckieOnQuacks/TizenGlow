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

**Starting with the TV:** TizenGlow's service is started by TizenBrew, so after a full restart of the TV (a power cut, a firmware update, or Samsung's occasional full reboot) nothing runs until something starts it. **Start with the TV** (under Capture, on by default) keeps a repeating system alarm pointed at the service: while the service runs it does nothing, and after a restart the next alarm starts it again, within 10 minutes (the shortest repeat Tizen allows), without opening TizenBrew. If the TV is in standby at that moment, capture waits until the screen comes on. Tizen's own start-at-boot flag has been ignored since Tizen 2.4, so this is the workaround. **Autostart** in the status says `on`, `off`, or why the TV refused the alarm. For an immediate start whenever you open TizenBrew, also set TizenGlow under TizenBrew's **Settings > Autolaunch Settings for services**.

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
| Mode / Target FPS | Active Capture mode and the frame rate the loop paces to |
| Capture FPS | Frames that came back from the TV's capture API per second |
| Send FPS | Frames HyperHDR actually received per second (0 while it is unreachable) |
| Avg capture time | Time in the capture API per frame (sampling the points) |
| Avg frame time | Whole loop iteration per frame: capture + image + send + pacing wait. Frame breakdown shows each part |
| Failed frames | Frames whose capture or image step threw an error |
| Dropped frames | Frame slots the target asked for that passed without a capture because the previous frame was still running (see below) |
| Performance | `target met` (capture FPS at least 90% of target), `capture limited` (capture + image + send alone take at least 90% of a frame interval), `below target` (anything else), `measuring` |

Dropped frames: with a target of 30 FPS there is a frame slot every 33.3 ms. If a frame takes 92 ms, the slots it ran over are not made up later: frames are never queued, run in parallel or sent in a burst to catch up. Those passed slots are counted as dropped (fractionally per frame, shown rounded down), so dropped per second is about target FPS minus capture FPS. Waiting for the next slot when a frame finished early is normal pacing and never counts as dropped.

### Capture

The **Capture** selector under Enabled picks how many points a frame samples (OK cycles, LEFT / RIGHT step). Balanced is the default. The choice is remembered, applies to the next frame without restarting capture, and also sets Target FPS, which you can then fine-tune.

| | Fast | Balanced | Detailed |
|---|---|---|---|
| Points (top / right / bottom / left) | 6 (2 / 1 / 2 / 1) | 8 (2 / 2 / 2 / 2) | 14 (4 / 3 / 4 / 3) |
| Frame time on an S90D (2 points per round, 20 ms wait) | 60 ms, 16.7 fps | 80 ms, 12.5 fps | 140 ms, 7 fps |
| Target FPS | 20 | 15 | 10 |
| Pause after a failed frame | 1 s | 5 s | 5 s |
| Condition read retry | 5 s | 10 s | 10 s |

The firmware reads `ScreenCapturePoints` points per round and asks for a pause (`sleepMs` in Condition) after every round, so a frame takes rounds x wait: the point count is the only lever on frame rate and latency. Fewer points means faster, lower-latency frames and coarser colour along each edge (Fast gives each side one colour); more points means finer colour. Points are evenly spaced along each edge, 5% in from it, and both outputs map by position, so any count works. **Points** in the status shows the count, the firmware's numbers and the resulting ceiling. A saved "smooth" mode from older versions becomes Detailed.

### Black bars

Letterboxed films (2.39:1 on a 16:9 screen) leave the top and bottom sample rows in the black bars, so those LEDs stay dark for the whole film. **Black bars** under Capture: **Auto** (default) moves the top and bottom rows in by 12.5% of the screen once every top and bottom sample has been black for 3 seconds while the sides show a picture, and one frame every 5 seconds samples the edge again to move back when the bars are gone (that frame shows the previous rows, so the LEDs never blink). **Always** keeps the rows moved in; **Off** never moves them. The status shows `watching`, `detected`, `always` or `off`. The sides are never moved.

### Target FPS

The **Target FPS** button under Capture Quality sets how fast the service captures: 10, 15, 20, 25, 30 or 40. Press [OK] to cycle through the values, or [LEFT] / [RIGHT] to step down / up. The change applies to the next frame and is remembered.

Capture, image and send time count toward each frame, so the service only waits for whatever is left of the frame interval. If Capture FPS stays below the target, the TV's capture API is the limit (see Capture above); pick a mode with fewer points.

### Levels

The TV's capture doesn't necessarily return 0 for black and 255 for white (one S90D returns about 170 for white), which leaves the LEDs dim and dull on both outputs. **Calibrate levels** under Levels shows Full White, measures what the capture returns, then Full Black, measures again, and returns to the settings with the result (about 10 seconds; capture must be running). From then on every frame is stretched so the measured white is 255,255,255 and the measured black 0,0,0, per channel, before either output sees it. **Levels: Off** keeps the measurement but stops applying it. A measurement is refused when the samples are dark while measuring white, bright while measuring black, differ too much (the screen wasn't one colour), or leave less than 32 counts between black and white; the status says why. The values can also be typed in over the settings port as `levelsWhite` / `levelsBlack` ("r,g,b").

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
| Smoothing On / Off | Time-based exponential smoothing; the time constant follows Capture: Fast 60 ms, Balanced 100 ms, Detailed 150 ms. While it is on, the strip is re-sent at 30 fps between captured frames, so colours glide towards each new frame instead of stepping once per capture (Send FPS shows the DDP rate). Off sends each frame as captured |
| Saturation 0-200% | 100 as captured, 0 greyscale, 200 doubled (around each LED's brightness) |
| Gamma 0.6-2.4 | Applied per LED after saturation: above 1 deepens dark colours and makes the strip less washed out, below 1 lifts them. 1.0 is as captured |

HyperHDR output is not affected by these; HyperHDR does its own processing. Levels (above) apply to both outputs.

### DDP details

Packets follow WLED's receiver (`wled00/e131.cpp`): 10-byte header, version 1, data type RGB 8-bit (`0x0B`), destination 1, a 32-bit byte offset and 16-bit length, PUSH on the last packet of a frame, sequence numbers 1-15. Each packet carries at most 1440 bytes (480 LEDs), the same split WLED's own DDP output uses: 1478 bytes with UDP and IP headers, inside a 1500-byte MTU, so packets are never IP-fragmented. 300 LEDs is one packet per frame, 600 is two, 4000 is nine.

### Status

Native Capture Status shows the output, its target and state (`ready`, `sending`, `testing`, `send error`, `no LED layout`, `not configured`; UDP has no connection, so there is no "connected"), LED count, packets per frame, last send time, DDP frames, packets and errors, and the last output error. Below it, the **LED preview** ring shows the last strip sent to WLED laid out round the screen (top-left clockwise, whatever the strip's start corner and direction), refreshed once a second, so the mapping can be checked without looking behind the TV. A failed send never stops capture: it is counted and retried after 1 second. **Peak sample** is the largest raw value the capture API returned; above 255 means the TV returns more than 8 bits per channel, which both outputs currently clip at 255.

## Calibration screen

**Open Calibration Screen** (under Output) fills the TV with test patterns. The TV shows them like any other picture, so they go through the whole path: screen, Samsung capture, TizenGlow, then WLED Direct or HyperHDR, then the LEDs. Nothing is sent to the LEDs directly; **Test WLED** is still the test for the network side only. Capture must be enabled and running (the info box warns when it isn't).

| Pattern | What the LEDs should do |
|---|---|
| Full Red, Green, Blue, White (`#FF0000`, `#00FF00`, `#0000FF`, `#FFFFFF`) | Every LED that colour |
| Full Black (`#000000`) | All off; checks the black threshold |
| Hue Gradient | Red, yellow, green, cyan, blue, magenta, red from left to right (top and bottom edges) |
| Four Edges | Top red, right green, bottom blue, left yellow |
| Four Corners | Top-left red, top-right green, bottom-right blue, bottom-left yellow |
| Moving Edge Marker | A white block going clockwise round the edge, 2.5 s per lap; shows orientation, latency and smoothing. It is long enough to always light at least one sample point, so it grows with fewer points |
| Sample Points | A white spot at each point the TV samples (from the service's stats), moved in when black bars are detected: the LEDs follow only these spots |
| Edge Rainbow | The rainbow clockwise from the top-left corner, in the same order as round the screen |

Remote: **LEFT / RIGHT** previous / next pattern, **OK** pauses the marker (on other patterns it shows or hides the info box), **UP / DOWN** make the marker faster / slower (1.5 to 10 s per lap) and, on the other patterns with WLED Direct, shift the LEDs one step clockwise / counter-clockwise (see LED shift), **BACK** returns to the settings on the last pattern used.

The info box sits in the middle of the screen, where nothing is sampled, and hides itself (after 2.5 s on the full-screen colours, 8 s otherwise); any key shows it again. To protect OLED panels, the screen closes after 10 minutes without a key press on any still pattern; only the moving marker keeps running.

Edge bands are 12.5% of the screen deep and corner blocks a third of it each way; the capture samples sit 5% in from the edges (with Fast's single side point in the middle of the side, Four Corners leaves the sides dark). **Calibrate levels** (under Levels) uses the same screen on its own: Full White and Full Black with the measurement in between, and any key cancels it. Colours are exact CSS colours and gradients (no canvas, filters or opacity); the hue gradients have a stop at every 60 degrees, so they are exact hues throughout.

## Watching the status from a PC

The settings page covers the screen, so it can't show what capture does while you watch something else (a game console, an HDMI input, an app). From a PC on the same network, with [Node.js](https://nodejs.org/) 22 or newer:

```bash
node tools/watch-status.js 192.168.1.20
```

It prints the capture state, capture and send FPS, frame time, peak sample, points and black-bar state once a second, plus the capture condition and last error whenever they change. It only reads status and never changes a setting. If it can't connect, the TV is off or the TizenGlow service isn't running.

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