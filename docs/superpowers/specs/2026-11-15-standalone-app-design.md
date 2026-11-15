# Standalone TizenGlow app: design

2026-11-15. Approved by Joey.

## Why

Reaching the settings today means opening TizenBrew, then the module. The goal is fewer steps: one icon
on the TV that opens the settings.

## Goals

- A "TizenGlow" app on the TV that can be pinned to the home bar. Opening it starts the service if it
  isn't running and shows the settings page. Back closes it.
- The TizenBrew module keeps working exactly as it does today.
- Each release ships the app as `TizenGlow-<version>.wgt` beside the service's TPK.

## Not goals

- No redesign of the page.
- No change to the service, its app ID, its settings or Start with the TV.
- No self-updating page: a page update means reinstalling the app.

## Would count as failure

- The TizenBrew module path breaks.
- Installing the app resets the service's settings.

## Constraints

- The app can't be signed for a TV here or in CI (no Samsung certificate, no Tizen Studio). It ships
  unsigned and relies on TizenBrew Installer (or Tizen Studio) to sign it, as the TPK does.
- Nothing can be run on the TV from here, so the startup logic gets a Node check with stubbed TV APIs.

## Approaches

1. **A separate app with the page inside (picked).** A `.wgt` built from `TizenGlowUI/`. The service is
   untouched and keeps its settings. Cost: a second file to install, and page updates need a reinstall.
2. **One combined (hybrid) package.** One install per update, but the service gets a new app ID (settings
   reset once), the build needs Samsung's packaging tools, and it can't be tested here.
3. **An app that loads the page from the CDN.** Updates arrive by themselves, but it won't open without
   the CDN, and TV APIs are less dependable for remote pages.

Picked 1 because the aim is only fewer steps, and it is the one that can't break what works.

## Design

- **Package:** `TizenGlowUI/config.xml` (already in the repo from HyperTizen) becomes the app's manifest:
  app ID `Sj1Em5JlZW.TizenGlowUI` (kept), the release's version, network access to the service. The
  `.wgt` is a zip of `config.xml`, `index.html`, `main.css`, `icon.png` and `js/` (without `service.js`,
  which is TizenBrew's).
- **Telling the two hosts apart:** the installed app loads the page from `file:`; TizenBrew loads it over
  HTTP. `file:` means standalone.
- **Startup, standalone:** launch the service by its app ID (what `service.js` does under TizenBrew), then
  connect to `ws://127.0.0.1:8086`, falling back to the TV's own address from `webapis.network.getIp()`.
  Retry once a second until the service answers.
- **Startup, TizenBrew:** unchanged (address from TizenBrew's helper on port 8081).
- **Back:** standalone only. While typing in a box it ends typing; otherwise it exits the app. The
  calibration screen already takes Back for itself while open.
- **Wording:** the top bar's "update the TizenBrew module" says "install the latest app" when standalone.
- **Prelaunch:** the manifest's `prelaunch.support` stays on. The TV may then load the app in the
  background at power-on, which would also start the service. Unverified; not relied on.
- **Release:** version 1.5.0. The workflow checks `config.xml`'s version with the other three, zips the
  `.wgt`, and attaches it and its SHA-256 to the release.
- **Docs:** the install guide gains the app as the main route, with the TizenBrew module as the alternative.

## Left to the builder

- Where the startup code lives (it moves out of `index.html`'s inline script if that makes it checkable).
- The exact retry and wording details.

## Risk

TizenBrew Installer may refuse an unsigned `.wgt`. If it does, the fix depends on the error it shows.
