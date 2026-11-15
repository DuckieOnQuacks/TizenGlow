# Standalone TizenGlow App Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the settings page as its own TV app (`TizenGlow-<version>.wgt`) that starts the service and connects to it without TizenBrew.

**Architecture:** The same `TizenGlowUI/` files serve both hosts. The page decides at load which host it is in (`file:` = installed app) and picks its startup path; everything after the WebSocket opens is unchanged. The release workflow zips the folder into a `.wgt` next to the TPK.

**Tech Stack:** Plain JavaScript page, Tizen web app manifest (`config.xml`), Node (built-in `vm`, `assert`) for the check, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-11-15-standalone-app-design.md`

## Global Constraints

- The TizenBrew path must behave exactly as before (address from `http://127.0.0.1:8081`, reload on failure).
- The service, its app ID `io.gh.duckieonquacks.TizenGlow`, and port `8086` do not change.
- App ID stays `Sj1Em5JlZW.TizenGlowUI`.
- Release version `1.5.0` in `TizenGlow/tizen-manifest.xml`, `package.json`, `uiVersion` and `config.xml`; `minService` stays `1.4.0`.
- No new dependencies. Files keep their BOM and line endings.
- Commits end with the session's `Co-Authored-By` line.

## Review Focus

1. The service's TPK isn't installed: the app must say so, not sit on "Connecting...". (Task 1 test)
2. The service takes seconds to start: the app keeps retrying once a second without reloading. (Task 1 test)
3. `127.0.0.1` is refused but the TV's own address works. (Task 1 test)
4. Back while the TV keyboard is open must end typing, not close the app. (Task 1, checked on the TV)
5. TizenBrew Installer refuses the unsigned `.wgt`. (Not testable here; reported by the user)

---

### Task 1: Page startup for both hosts

**Files:**
- Modify: `TizenGlowUI/js/wsClient.js` (top of file; `showVersions`)
- Modify: `TizenGlowUI/index.html` (the `fetch('http://127.0.0.1:8081')` block and the `keydown` handler in the inline script)
- Create: `tools/check-page.js`

**Interfaces (all globals in `wsClient.js`):**
- `const standalone = location.protocol === 'file:';`
- `const serviceAppId = 'io.gh.duckieonquacks.TizenGlow';`
- `function startPage()`: standalone: `launchService()` then `tryHosts(['127.0.0.1', tvIp()])`; otherwise fetch `http://127.0.0.1:8081` and `tryHosts([ip])`.
- `function tryHosts(hosts)`: test-connects `ws://<host>:8086` in order, skipping empty hosts; on the first that opens, closes it, sets `deviceIP` and calls `open()`. When none opens: standalone `setTimeout(startPage, 1000)`, otherwise `location.reload()`.
- `function launchService()`: `tizen.application.launch(serviceAppId, ok, failed)`; `failed` and a thrown error set `#status` to `Service not found: install the TPK`.
- `function tvIp()`: `webapis.network.getIp()`, or `null` when unavailable.

- [ ] **Step 1: Write `tools/check-page.js`** (`node tools/check-page.js`, exits non-zero on failure). It loads `wsClient.js` in a `vm` context with stub elements for the IDs in `index.html` (a missing ID returns `null`), as the old scratch smoke test did, and asserts:
  - replies: version pill `v<uiVersion>`, WLED target, `on (checks every 10 min)`, the enabled toggle sends `{event:0,key:'enabled',value:'true'}`;
  - standalone start: launches `io.gh.duckieonquacks.TizenGlow`; first socket `ws://127.0.0.1:8086`; when it errors the next is `ws://192.168.1.20:8086` (stubbed `getIp`); when that opens, `deviceIP === '192.168.1.20'` and the page socket uses it;
  - standalone, nothing answers: one timer of `1000` ms whose callback launches the service again; `location.reload` never called;
  - standalone, launch fails: `#status` is `Service not found: install the TPK`;
  - TizenBrew start (`location.protocol = 'https:'`): fetches `http://127.0.0.1:8081`, never calls `tizen.application.launch`, connects to the returned address, and reloads when it fails;
  - wording: with a newer service, the pill ends `install the latest app` standalone and `update the TizenBrew module` otherwise.
- [ ] **Step 2: Run it.** Expected: fails (`startPage is not defined`).
- [ ] **Step 3: Implement the interfaces above** and change `showVersions`' "newer" text by host.
- [ ] **Step 4: `index.html`:** replace the fetch block with `startPage();`. In `keydown`, add: key code `10009` and `standalone`: if the focused element is a box being typed in, `stopEditing` it; otherwise `tizen.application.getCurrentApplication().exit()`.
- [ ] **Step 5: Run `node tools/check-page.js`.** Expected: `page check passed`.
- [ ] **Step 6: Commit.**

### Task 2: The app package and the release build

**Files:**
- Modify: `TizenGlowUI/config.xml`, `.github/workflows/release.yml`
- Modify: `TizenGlow/tizen-manifest.xml`, `package.json`, `TizenGlowUI/js/wsClient.js` (versions)

- [ ] **Step 1: `config.xml`:** `version="1.5.0"`, add `<access origin="*" subdomains="true"/>`; keep the ID, privileges and `prelaunch.support`.
- [ ] **Step 2: Versions to `1.5.0`** in the other three files.
- [ ] **Step 3: Workflow:**
  - the version check also reads `config.xml`'s widget `version` and fails when it differs;
  - a step runs `node tools/check-page.js`;
  - a step builds `TizenGlow-$VERSION.wgt` from inside `TizenGlowUI/`: `zip -r -X ../TizenGlow-$VERSION.wgt config.xml index.html main.css icon.png js -x js/service.js`, then writes `TizenGlow-$VERSION.wgt.sha256`;
  - the artifact upload and `gh release create` include the `.wgt` and its checksum;
  - the release notes gain: `**TV app:** <wgt>, SHA-256 ... Unsigned: sign it for your TV like the TPK. It opens the settings without TizenBrew.`
- [ ] **Step 4: Verify locally:** build the same zip with Python's `zipfile`, list it (5 top-level entries plus `js/calibration.js`, `js/wsClient.js`; no `service.js`, no `package.json`), parse `config.xml` as XML, run the check, and `dotnet build -c Release` still succeeds.
- [ ] **Step 5: Commit, push, and run the workflow by hand** (`gh workflow run Release`). Expected: success, artifact holds the TPK and the `.wgt`.

### Task 3: Docs and the 1.5.0 release

**Files:**
- Modify: `docs/README.md` (Installation, Updating), `README.md` (Getting started), `TizenGlowUI/index.html` (the "without opening TizenBrew" hint)

- [ ] **Step 1: `docs/README.md`:** the settings page is now "the TizenGlow app (`TizenGlow-<version>.wgt`), or the TizenBrew module". Install steps: TPK, then the app (signed and installed the same way), pin it to the home bar; the TizenBrew module steps move under "Or through TizenBrew". Updating: a page update means installing the new `.wgt` (the module still updates itself).
- [ ] **Step 2: `README.md`:** Getting started names both files and mentions the module as the alternative; drop "and TizenBrew" from the requirement line (now optional).
- [ ] **Step 3: Hint text:** "without opening TizenBrew" becomes "without opening anything".
- [ ] **Step 4: Run the check, commit, tag `v1.5.0` on that commit, push.** Expected: release with four assets.
- [ ] **Step 5: Purge the CDN paths** so the TizenBrew module serves 1.5.0, and confirm `uiVersion`.
