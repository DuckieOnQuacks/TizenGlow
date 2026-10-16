# TizenGlow

TizenGlow lights the LEDs behind a Samsung Tizen TV from what's on its screen. The TV itself captures the picture, so it works with apps, streaming services and HDMI inputs, with no camera and no PC needed.

- **WLED Direct:** drives a [WLED](https://kno.wled.ge/) controller over DDP, with an LED layout (counts per side, start corner, direction, LED shift), brightness, black threshold and smoothing.
- **HyperHDR:** sends the picture to a HyperHDR or Hyperion server instead, found over SSDP or entered by hand.
- **Calibration screen:** full-screen test patterns shown by the TV (solid colours, edges, corners, a moving marker, a rainbow) to check colours, orientation and alignment through the whole capture path.
- **Live status:** capture and send FPS, frame timing, the detected capture API and errors, on a settings page made for the remote.

Needs a Samsung TV with Tizen 6.5 or newer (2022+) and [TizenBrew](https://github.com/reisxd/TizenBrew).

## Getting started

Read the [guide](./docs/README.md): install the TPK from the [latest release](https://github.com/DuckieOnQuacks/TizenGlow/releases/latest), then add the TizenBrew module `duckieonquacks/tizenglow`.

Coming from HyperTizen: uninstall the HyperTizen app first. Both services use the same settings port, and both would capture at once. TizenGlow is a separate app, so enter your settings again.

## Credits and license

TizenGlow is a modified version of [HyperTizen](https://github.com/reisxd/HyperTizen) by Reis Can (reisxd), as of December 2024 (commit `9e71bf2`). The screen capture through Samsung's `libvideoenhance`, the TizenBrew service and module structure, and the settings server come from HyperTizen.

Changed by Joey Paschke from October 2026, renamed TizenGlow: WLED Direct output, the calibration screen, LED shift, runtime capture API detection, capture modes and statistics, the manual HyperHDR server, the redesigned settings page, the release workflow and many fixes. The git history records every change.

Copyright (C) 2024 Reis Can
Copyright (C) 2026 Joey Paschke

This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, version 3 of the License. It is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See [LICENSE](./LICENSE) for the full text.
