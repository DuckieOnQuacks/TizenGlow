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
| Measured FPS | Frames actually captured per second over the last second (`0.0` when not capturing) |
| API | `ppi_ve`, `ve`, `cs_ve` or `unsupported`, plus the platform version |
| Condition | What the panel reported: result code, capture points, resolution, pixel density, sleep |
| Last error | Timestamped error with exception, API, library path, platform version and which symbol families were complete, partial or missing |

`not reported` means the installed native service is older than 1.0.1.

### Target FPS

The **Target FPS** button under Enabled sets how fast the service captures: 10, 15, 20, 25 (default), 30 or 40. Press [OK] to cycle through the values, or [LEFT] / [RIGHT] to step down / up. The change applies to the next frame and is remembered (needs native 1.0.2+).

Capture time counts toward each frame, so the service only waits for whatever is left of the frame interval. If Measured FPS stays below the target, the TV's capture API is the limit: each frame reads the screen in batches and the firmware asks for a pause (`sleepMs` in Condition) after every batch.

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