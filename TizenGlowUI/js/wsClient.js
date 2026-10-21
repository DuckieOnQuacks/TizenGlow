// This page's version. Must equal tizen-manifest.xml and package.json; the release workflow checks.
const uiVersion = '1.3.1';
// Oldest service (TPK) this page works with. Raise it only when the page needs something new from the
// service; a page-only release leaves it, and that release's TPK is then optional.
const minService = '1.3.0'; // strip off with the TV

let client;
let deviceIP;
const ssdpDevices = [];
let canEnable = false;

function open() {
    client = new WebSocket(`ws://${deviceIP}:8086`);
    client.onopen = onOpen;
    client.onmessage = onMessage;
    client.onerror = () => {
        location.reload();
    }
}

const events = {
    SetConfig: 0,
    ReadConfig: 1,
    ReadConfigResult: 2,
    ScanSSDP: 3,
    SSDPScanResult: 4
}

function send(json) {
    client.send(JSON.stringify(json));
}

function onOpen() {
    document.getElementById('status').innerHTML = 'Connected';
    document.getElementById('enabled').onchange = (e) => {
        if (outputSel.value === 'wled' ? !wledReady : !canEnable) {
            alert(outputSel.value === 'wled' ? 'Please save a WLED target first' : 'Please select a device or set a manual server first');
            return e.target.checked = false;
        }
        send({ event: events.SetConfig, key: 'enabled', value: e.target.checked.toString() });
    }
    send({ event: events.ReadConfig, key: 'rpcServer' });
    send({ event: events.ReadConfig, key: 'enabled' });
    send({ event: events.ScanSSDP });
    send({ event: events.ReadConfig, key: 'captureMode' });
    send({ event: events.ReadConfig, key: 'targetFps' });
    send({ event: events.ReadConfig, key: 'nativeVersion' });
    send({ event: events.ReadConfig, key: 'uiScale' });
    for (const key of wledKeys) send({ event: events.ReadConfig, key });
    for (const key of captureKeys) send({ event: events.ReadConfig, key });
    readCaptureStatus();
    // One timer each, however often this runs.
    clearInterval(ssdpTimer);
    clearInterval(statusTimer);
    ssdpTimer = setInterval(() => send({ event: events.ScanSSDP }), 10000);
    statusTimer = setInterval(readCaptureStatus, 1000);
}

let ssdpTimer = null;
let statusTimer = null;

function stepIn(list, current, step, wrap) {
    const n = list.length;
    const i = list.indexOf(current) + step;
    return list[wrap ? (i + n) % n : Math.max(0, Math.min(n - 1, i))];
}

function connected() {
    return client && client.readyState === WebSocket.OPEN;
}

// Must match HyperionClient.FpsPresets; the service ignores anything else.
const fpsPresets = [10, 15, 20, 25, 30, 40];
let targetFps = 25;

function showTargetFps(fps) {
    targetFps = fpsPresets.indexOf(fps) !== -1 ? fps : 25;
    document.getElementById('targetFps').textContent = `Target FPS: < ${targetFps} >`;
}

// OK cycles through the presets (wrapping), Left/Right step down/up.
window.stepTargetFps = (step, wrap) => {
    if (!connected()) return;
    const next = stepIn(fpsPresets, targetFps, step, wrap);
    if (next === targetFps) return;
    showTargetFps(next);
    send({ event: events.SetConfig, key: 'targetFps', value: String(next) });
}

// Must match CaptureMode.cs ("smooth" was the old name of the 14-point mode).
const captureModes = ['fast', 'balanced', 'detailed'];
const captureModeLabels = { fast: 'Fast', balanced: 'Balanced', detailed: 'Detailed' };
let captureMode = 'balanced';

function showCaptureMode(mode) {
    if (mode === 'smooth') mode = 'detailed';
    captureMode = captureModes.indexOf(mode) !== -1 ? mode : 'balanced';
    document.getElementById('captureMode').innerHTML = 'Capture: ' + captureModes
        .map(m => m === captureMode ? `<span class="active">${captureModeLabels[m]}</span>` : captureModeLabels[m]).join(' | ');
}

// Same keys as Target FPS. A mode also sets its own target FPS, so read that back.
window.stepCaptureMode = (step, wrap) => {
    if (!connected()) return;
    const next = stepIn(captureModes, captureMode, step, wrap);
    if (next === captureMode) return;
    showCaptureMode(next);
    send({ event: events.SetConfig, key: 'captureMode', value: next });
    send({ event: events.ReadConfig, key: 'targetFps' });
}

// One-button selector: OK cycles (wrapping), Left/Right step. With a key the choice is sent at once;
// without one it stays local until saved (LED layout). Labels come from fixed lists only.
function makeSelector(id, values, initial, render, key, after) {
    const sel = { value: initial };
    sel.show = v => {
        sel.value = values.indexOf(v) !== -1 ? v : initial;
        document.getElementById(id).innerHTML = render(sel.value);
        if (after) after(sel.value);
    };
    sel.step = (step, wrap) => {
        if (key && !connected()) return;
        const next = stepIn(values, sel.value, step, wrap);
        if (next === sel.value) return;
        sel.show(next);
        if (key) send({ event: events.SetConfig, key, value: String(next) });
    };
    return sel;
}

const options = (labels, value) => Object.keys(labels).map(k => k === value ? `<span class="active">${labels[k]}</span>` : labels[k]).join(' | ');

// Output: HyperHDR (default, as before) or WLED Direct. Only the active one's settings are shown.
const outputLabels = { hyperhdr: 'HyperHDR', wled: 'WLED Direct' };
const outputSel = makeSelector('outputMode', Object.keys(outputLabels), 'hyperhdr', v => 'Output: ' + options(outputLabels, v), 'outputMode',
    v => { document.body.className = 'output-' + v; });
window.stepOutputMode = outputSel.step;
function showOutputMode(mode) { outputSel.show(mode); }

// WLED Direct picture settings, sent as soon as they change.
const brightnessSel = makeSelector('wledBrightness', [0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100], 100, v => `Brightness: &lt; ${v}% &gt;`, 'wledBrightness');
const thresholdSel = makeSelector('wledBlackThreshold', [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 0, v => `Black threshold: &lt; ${v}% &gt;`, 'wledBlackThreshold');
const smoothingSel = makeSelector('wledSmoothing', ['on', 'off'], 'on', v => `Smoothing: &lt; ${v === 'on' ? 'On' : 'Off'} &gt;`, 'wledSmoothing');
const tvOffLabels = { off: 'Switch the strip off', leave: "Leave it to WLED" };
const tvOffSel = makeSelector('wledTvOff', Object.keys(tvOffLabels), 'off', v => `When the TV turns off: &lt; ${tvOffLabels[v]} &gt;`, 'wledTvOff');
window.stepTvOff = tvOffSel.step;
const saturationSel = makeSelector('wledSaturation', [0, 25, 50, 75, 100, 125, 150, 175, 200], 100, v => `Saturation: &lt; ${v}% &gt;`, 'wledSaturation');
const gammaValues = ['0.6', '0.7', '0.8', '0.9', '1.0', '1.2', '1.4', '1.6', '1.8', '2.0', '2.2', '2.4'];
const gammaSel = makeSelector('wledGamma', gammaValues, '1.0', v => `Gamma: &lt; ${v} &gt;`, 'wledGamma');
window.stepBrightness = brightnessSel.step;
window.stepBlackThreshold = thresholdSel.step;
window.stepSmoothing = smoothingSel.step;
window.stepSaturation = saturationSel.step;
window.stepGamma = gammaSel.step;

// Capture-side settings (both outputs): black bars and levels.
const letterboxLabels = { off: 'Off', auto: 'Auto', on: 'Always' };
const letterboxSel = makeSelector('letterbox', Object.keys(letterboxLabels), 'auto', v => `Black bars: &lt; ${letterboxLabels[v]} &gt;`, 'letterbox');
const levelsSel = makeSelector('levels', ['on', 'off'], 'on', v => `Levels: &lt; ${v === 'on' ? 'On' : 'Off'} &gt;`, 'levels');
const autostartSel = makeSelector('autostart', ['on', 'off'], 'on', v => `Start with the TV: &lt; ${v === 'on' ? 'On' : 'Off'} &gt;`, 'autostart');
window.stepLetterbox = letterboxSel.step;
window.stepLevels = levelsSel.step;
window.stepAutostart = autostartSel.step;

// Page size: scales every size in main.css (--px). Saved by the service like any other setting.
const uiScaleSel = makeSelector('uiScale', [50, 60, 70, 80, 90, 100], 100, v => `Page size: &lt; ${v}%${v === 50 ? ' (4K)' : ''} &gt;`, 'uiScale', applyUiScale);
window.stepUiScale = uiScaleSel.step;

function applyUiScale(percent) {
    document.documentElement.style.setProperty('--px', `${percent / 100}px`);
    fitStatus();
}

// Shrinks the status column's content when it would not fit (a long error, a large page size) instead of
// cutting it off: nothing can scroll it with the remote.
function fitStatus() {
    const column = document.getElementById('captureStatus'), body = document.getElementById('statusBody');
    if (!column || !body || !column.clientHeight) return;
    // While a box is being typed in, the TV keyboard takes the bottom of the screen and the page shrinks to
    // the space above it: keep the column's size rather than squeezing it into that gap.
    const active = document.activeElement;
    if (active && active.tagName === 'INPUT' && active.type !== 'checkbox' && !active.readOnly) return;
    body.style.zoom = '';
    const padding = column.scrollHeight - body.offsetHeight; // the column's own, which zoom doesn't shrink
    const ratio = (column.clientHeight - padding) / body.offsetHeight;
    if (ratio < 1) body.style.zoom = String(Math.floor(ratio * 100) / 100);
}
const captureKeys = ['letterbox', 'levels', 'autostart'];

// LED layout: start corner and direction are saved together with the counts.
const cornerLabels = { topleft: 'Top Left', topright: 'Top Right', bottomright: 'Bottom Right', bottomleft: 'Bottom Left' };
const directionLabels = { cw: 'Clockwise', ccw: 'Counter-clockwise' };
const cornerSel = makeSelector('ledStart', Object.keys(cornerLabels), 'topleft', v => `Start corner: &lt; ${cornerLabels[v]} &gt;`);
const directionSel = makeSelector('ledDirection', Object.keys(directionLabels), 'cw', v => `Direction: &lt; ${directionLabels[v]} &gt;`);
window.stepLedStart = cornerSel.step;
window.stepLedDirection = directionSel.step;

const wledKeys = ['outputMode', 'wledTarget', 'ledLayout', 'wledBrightness', 'wledBlackThreshold', 'wledSmoothing', 'ledShift', 'wledSaturation', 'wledGamma', 'wledTvOff'];
const ledSides = ['ledTop', 'ledRight', 'ledBottom', 'ledLeft'];
let wledReady = false;

function showStatus(id, text, isError) {
    const element = document.getElementById(id);
    element.className = isError ? 'error' : '';
    element.textContent = text;
}

// Returns { host, port } or { error }. WLED is IPv4 / hostname only.
function checkWledTarget(host, port) {
    host = String(host).trim();
    port = String(port).trim();
    if (!host) return { error: 'Invalid WLED host: enter its IP address or hostname' };
    if (!/^[a-z0-9.-]+$/i.test(host)) return { error: `Invalid WLED host: "${host}" (IP or hostname only)` };
    if (!/^\d+$/.test(port) || +port < 1 || +port > 65535) return { error: 'Invalid DDP port: enter a number from 1 to 65535 (WLED uses 4048)' };
    return { host, port: +port };
}

window.saveWledTarget = () => {
    const result = checkWledTarget(document.getElementById('wledHost').value, document.getElementById('wledPort').value);
    if (!result.error && !connected()) result.error = 'Not connected to the TizenGlow service yet, try again in a moment';
    if (result.error) return showStatus('wledStatus', result.error, true);
    send({ event: events.SetConfig, key: 'wledTarget', value: `${result.host}:${result.port}` });
    wledReady = true;
    showStatus('wledStatus', `WLED target saved: ${result.host}:${result.port}`);
}

function showWledTarget(value) {
    const colon = value.lastIndexOf(':');
    if (colon <= 0) return;
    document.getElementById('wledHost').value = value.slice(0, colon);
    document.getElementById('wledPort').value = value.slice(colon + 1);
    wledReady = true;
}

// Counts as typed: empty is 0, anything else must be a whole number 0-2000. Returns { counts } or { error }.
function readLedCounts() {
    const counts = [];
    for (const id of ledSides) {
        const text = String(document.getElementById(id).value).trim();
        if (text !== '' && !/^\d+$/.test(text)) return { error: 'LED counts must be whole numbers' };
        const n = text === '' ? 0 : +text;
        if (n > 2000) return { error: 'At most 2000 LEDs per side' };
        counts.push(n);
    }
    if (counts[0] + counts[1] + counts[2] + counts[3] === 0) return { error: 'Enter at least one LED (top + right + bottom + left must be more than 0)' };
    return { counts };
}

function showLedTotal() {
    let total = 0;
    for (const id of ledSides) total += Math.max(0, parseInt(document.getElementById(id).value, 10) || 0);
    setText('ledTotal', String(total));
    showLedCheck(total);
}

// LED count WLED itself reports (the service asks it once per target); null until known.
let wledLedCount = null;

const leds = n => n === 1 ? '1 LED' : `${n} LEDs`;

// Compares the layout being typed with WLED's own LED count.
function showLedCheck(total) {
    const element = document.getElementById('ledCountCheck');
    let text = '', cls = 'hint';
    if (wledLedCount !== null) {
        if (total === 0) text = `WLED reports ${wledLedCount} LEDs.`;
        else if (total === wledLedCount) { text = `Matches WLED's ${wledLedCount} LEDs.`; cls = 'hint ok'; }
        else if (total < wledLedCount) { text = `WLED has ${wledLedCount} LEDs: the last ${leds(wledLedCount - total)} would stay dark. The four sides should add up to ${wledLedCount}.`; cls = 'hint warn'; }
        else { text = `WLED has only ${wledLedCount} LEDs: the last ${leds(total - wledLedCount)} in this layout would never light. The four sides should add up to ${wledLedCount}.`; cls = 'hint warn'; }
    }
    if (element.className !== cls) element.className = cls;
    setText('ledCountCheck', text);
}

function versionParts(v) { return String(v).split('.').map(n => parseInt(n, 10) || 0); }

function compareVersions(a, b) {
    const x = versionParts(a), y = versionParts(b);
    for (let i = 0; i < Math.max(x.length, y.length); i++) {
        if ((x[i] || 0) !== (y[i] || 0)) return (x[i] || 0) < (y[i] || 0) ? -1 : 1;
    }
    return 0;
}

// The service (TPK) and this page (TizenBrew module) are released together; say which one is behind.
// A service from minService up to this page's version works, so it is only shown, not flagged.
function showVersions(native) {
    const element = document.getElementById('versions');
    const tooOld = native === null || compareVersions(native, minService) < 0;
    const newer = !tooOld && compareVersions(native, uiVersion) > 0;
    element.className = tooOld || newer ? 'pill warn' : 'pill';
    element.textContent = native === uiVersion ? `v${uiVersion}`
        : native === null ? `Service older than ${minService}: install the latest TPK`
        : tooOld ? `Service ${native}, page ${uiVersion}: install the latest TPK`
        : newer ? `Service ${native}, page ${uiVersion}: update the TizenBrew module`
        : `Page ${uiVersion}, service ${native}`;
}

window.saveLedLayout = () => {
    const result = readLedCounts();
    if (!result.error && !connected()) result.error = 'Not connected to the TizenGlow service yet, try again in a moment';
    if (result.error) return showStatus('layoutStatus', result.error, true);
    const c = result.counts;
    send({ event: events.SetConfig, key: 'ledLayout', value: `${c.join(',')},${cornerSel.value},${directionSel.value}` });
    showStatus('layoutStatus', `LED layout saved: ${c[0] + c[1] + c[2] + c[3]} LEDs from ${cornerLabels[cornerSel.value]}, ${directionLabels[directionSel.value].toLowerCase()}`);
}

// LED shift: moves the colours round the strip one LED at a time (+ clockwise), for a strip whose first
// LED isn't exactly at the corner. Sent at once; the calibration screen steps it with UP / DOWN.
let ledShift = 0;

function showLedShift(value) {
    ledShift = Number.isInteger(value) ? value : 0;
    const text = ledShift > 0 ? `+${ledShift} clockwise` : ledShift < 0 ? `${ledShift} counter-clockwise` : '0';
    document.getElementById('ledShift').textContent = `LED shift: < ${text} >`;
}

// LEFT / RIGHT step one LED, OK steps clockwise and wraps; less than one whole loop of the strip either way.
window.stepLedShift = (step, wrap) => {
    if (!connected()) return;
    let total = 0;
    for (const id of ledSides) total += Math.max(0, parseInt(document.getElementById(id).value, 10) || 0);
    const limit = Math.max(1, total - 1);
    let next = ledShift + step;
    if (next > limit) next = wrap ? -limit : limit;
    if (next < -limit) next = wrap ? limit : -limit;
    if (next === ledShift) return;
    showLedShift(next);
    send({ event: events.SetConfig, key: 'ledShift', value: String(next) });
}

function showLedLayout(value) {
    const parts = value.split(',');
    if (parts.length !== 6) return;
    ledSides.forEach((id, i) => { document.getElementById(id).value = parts[i]; });
    cornerSel.show(parts[4]);
    directionSel.show(parts[5]);
    showLedTotal();
}

const testLabels = { red: 'Red', green: 'Green', blue: 'Blue', white: 'White', off: 'Off' };

// Whole-strip DDP test pattern; works with capture off.
window.testWled = color => {
    if (!connected()) return showStatus('testStatus', 'Not connected to the TizenGlow service yet', true);
    if (!wledReady) return showStatus('testStatus', 'Save a WLED target first', true);
    send({ event: events.SetConfig, key: 'wledTest', value: color });
    showStatus('testStatus', `${testLabels[color]} sent to WLED for 10 s`);
}

// Written by the native service (Capturer.cs) as preferences; read back with ReadConfig.
const captureStatusKeys = ['captureState', 'detectedApi', 'detectedPlatformVersion', 'lastCaptureError'];

function readCaptureStatus() {
    for (const key of captureStatusKeys) send({ event: events.ReadConfig, key });
    send({ event: events.ReadConfig, key: 'captureStats' });
    if (outputSel.value === 'wled') send({ event: events.ReadConfig, key: 'ledPreview' });
}

// The latest captureStats, for the calibration screen (sample points, levels results).
let lastStats = null;

// Only touch the DOM on change; status is refreshed every second.
function setText(id, text) {
    const element = document.getElementById(id);
    if (element.textContent !== text) element.textContent = text;
}

function showCaptureStatus(msg) {
    setText(msg.key, !msg.error ? String(msg.value)
        : msg.key === 'captureState' ? 'not reported (is the TizenGlow service installed?)' : '-');
    // The stylesheet colours the state badge from this (running green, error red, ...).
    if (msg.key === 'captureState') document.getElementById('captureState').dataset.state = msg.error ? '' : String(msg.value);
}

// Live numbers from the service (CaptureStats.cs), over its rolling 2 s window.
function showCaptureStats(msg) {
    const s = msg.error ? null : msg.value; // error: no TizenGlow service
    lastStats = s;
    const num = (v, unit) => !s ? '-' : v === null || v === undefined ? 'measuring' : v.toFixed(1) + (unit || '');
    const running = document.getElementById('captureState').textContent === 'running';
    setText('statMode', s ? s.mode : '-');
    setText('statResolution', s && s.resolution ? s.resolution : '-');
    setText('statPerformance', s && running ? s.performance : '-');
    setText('statTargetFps', s ? String(s.targetFps) : '-');
    setText('statCaptureFps', num(s && s.captureFps));
    setText('statSendFps', num(s && s.sendFps));
    setText('statCaptureMs', num(s && s.averageCaptureMs, ' ms'));
    setText('statFrameMs', num(s && s.averageFrameMs, ' ms'));
    setText('statFailed', s ? String(s.failedFrames) : '-');
    setText('statDropped', s ? String(s.droppedFrames) : '-');
    setText('statBreakdown', !s || s.averageFrameMs === null ? '-'
        : `capture ${num(s.averageCaptureMs)} + prepare ${num(s.averageImageMs)} + send ${num(s.averageSendMs)} + wait ${num(s.averageWaitMs)} ms`);

    // Output (native 1.0.4+). The WLED rows are only shown in WLED Direct mode.
    const has = v => s && v !== null && v !== undefined;
    setText('statOutput', has(s && s.output) ? s.output : '-');
    setText('statOutputState', has(s && s.outputState) ? s.outputState : '-');
    setText('statOutputTarget', has(s && s.outputTarget) ? s.outputTarget : '-');
    setText('statPeak', !has(s && s.peakSample) ? '-' : s.peakSample > 255 ? `${s.peakSample} (above 8-bit, clipped at 255)` : String(s.peakSample));
    setText('statLedCount', !s ? '-' : has(s.ledCount) ? String(s.ledCount) : 'no layout saved');
    setText('statPackets', has(s && s.packetsPerFrame) ? String(s.packetsPerFrame) : '-');
    setText('statLastSend', has(s && s.lastSendMs) ? `${s.lastSendMs.toFixed(2)} ms` : '-');
    setText('statDdpErrors', has(s && s.ddpSendErrors) ? String(s.ddpSendErrors) : '-');
    setText('statDdpFrames', has(s && s.ddpFramesSent) ? String(s.ddpFramesSent) : '-');
    setText('statDdpPackets', has(s && s.ddpPacketsSent) ? String(s.ddpPacketsSent) : '-');
    setText('statOutputError', !s ? '-' : s.outputError || 'none');

    // WLED's own LED count (native 1.0.6+).
    wledLedCount = has(s && s.wledLedCount) ? s.wledLedCount : null;
    setText('statWledLeds', wledLedCount !== null ? String(wledLedCount) : '-');
    setText('wledInfo', !has(s && s.wledInfo) ? '' : wledLedCount !== null ? `${s.wledInfo}: ${wledLedCount} LEDs` : s.wledInfo);
    showLedTotal();

    // Points, black bars and levels (service 1.1.0+).
    setText('statPoints', !has(s && s.points) ? '-'
        : `${s.points} (${s.pointsPerEdge})` + (s.pointsPerRound > 0 ? `, ${s.pointsPerRound} per round` : '')
        + (s.sleepMs > 0 ? `, ${s.sleepMs} ms wait: max ${s.ceilingFps} fps` : ''));
    setText('statLetterbox', has(s && s.letterbox) ? s.letterbox : '-');
    setText('statAutostart', !has(s && s.autostart) ? '-' : s.autostart === 'on' ? 'on (checks every 10 min)' : s.autostart);
    const lv = s && s.levels;
    setText('statLevels', !lv ? '-' : !lv.enabled ? 'off' : lv.white === '255,255,255' && lv.black === '0,0,0' ? 'not measured' : `white ${lv.white}, black ${lv.black}`);
    const status = document.getElementById('levelsStatus');
    if (lv && lv.result && status.dataset.result !== lv.result) {
        status.dataset.result = lv.result;
        status.className = /not measured/.test(lv.result) ? 'hint warn' : 'hint';
        status.textContent = sentence(lv.result) + (lv.measuring ? '' : ` Now: white ${lv.white}, black ${lv.black}.`);
    }
    fitStatus();
}

// "white measured ..." -> "White measured ...." (a result that already ends in ? or . keeps it).
const sentence = r => r.charAt(0).toUpperCase() + r.slice(1) + (/[.?!]$/.test(r) ? '' : '.');

// ---- LED preview: the last strip WLED got, drawn as a ring in canonical order (top-left clockwise) ----
function showLedPreview(msg) {
    const canvas = document.getElementById('ledPreview');
    if (!canvas || !canvas.getContext) return;
    // Drawn at the screen's real pixel density, so it stays sharp whatever the page size.
    const dpr = window.devicePixelRatio || 1;
    const W = canvas.clientWidth || canvas.width, H = canvas.clientHeight || canvas.height;
    if (canvas.width !== Math.round(W * dpr) || canvas.height !== Math.round(H * dpr)) {
        canvas.width = Math.round(W * dpr);
        canvas.height = Math.round(H * dpr);
    }
    const ctx = canvas.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    const band = Math.max(4, Math.round(H * 0.08));
    ctx.fillStyle = '#0d1117';
    ctx.fillRect(0, 0, W, H);
    const v = msg.error ? null : msg.value;
    if (!v || !v.rgb) {
        ctx.strokeStyle = '#2b3445';
        ctx.lineWidth = 2;
        ctx.strokeRect(band / 2, band / 2, W - band, H - band);
        return;
    }
    const bytes = atob(v.rgb);
    const color = i => `rgb(${bytes.charCodeAt(i * 3)},${bytes.charCodeAt(i * 3 + 1)},${bytes.charCodeAt(i * 3 + 2)})`;
    let i = 0;
    const run = (n, place) => { for (let k = 0; k < n; k++, i++) { ctx.fillStyle = color(i); place(k / n, (k + 1) / n); } };
    run(v.top, (a, b) => ctx.fillRect(a * W, 0, (b - a) * W + 0.5, band));                                  // left to right
    run(v.right, (a, b) => ctx.fillRect(W - band, a * H, band, (b - a) * H + 0.5));                          // top to bottom
    run(v.bottom, (a, b) => ctx.fillRect((1 - b) * W, H - band, (b - a) * W + 0.5, band));                   // right to left
    run(v.left, (a, b) => ctx.fillRect(0, (1 - b) * H, band, (b - a) * H + 0.5));                            // bottom to top
}

function onMessage(data) {
    const msg = JSON.parse(data.data);
    switch(msg.Event) {
        case events.ReadConfigResult:
            if(msg.key === 'rpcServer' && !msg.error) {
                canEnable = true;
                showRPC(msg.value);
            } else if(msg.key === 'enabled' && !msg.error) {
                document.getElementById('enabled').checked = msg.value === 'true';
            } else if (msg.key === 'targetFps') {
                showTargetFps(msg.error ? 25 : Number(msg.value));
            } else if (msg.key === 'captureMode') {
                showCaptureMode(msg.error ? 'balanced' : msg.value);
            } else if (msg.key === 'nativeVersion') {
                showVersions(msg.error ? null : String(msg.value));
            } else if (msg.key === 'outputMode') {
                showOutputMode(msg.error ? 'hyperhdr' : msg.value);
            } else if (msg.key === 'wledTarget') {
                if (!msg.error) showWledTarget(msg.value);
            } else if (msg.key === 'ledLayout') {
                if (!msg.error) showLedLayout(msg.value);
            } else if (msg.key === 'wledBrightness') {
                brightnessSel.show(msg.error ? 100 : Number(msg.value));
            } else if (msg.key === 'wledBlackThreshold') {
                thresholdSel.show(msg.error ? 0 : Number(msg.value));
            } else if (msg.key === 'wledSmoothing') {
                smoothingSel.show(msg.error ? 'on' : msg.value);
            } else if (msg.key === 'ledShift') {
                showLedShift(msg.error ? 0 : Number(msg.value));
            } else if (msg.key === 'wledTvOff') {
                tvOffSel.show(msg.error ? 'off' : msg.value);
            } else if (msg.key === 'wledSaturation') {
                saturationSel.show(msg.error ? 100 : Number(msg.value));
            } else if (msg.key === 'wledGamma') {
                gammaSel.show(msg.error ? '1.0' : Number(msg.value).toFixed(1));
            } else if (msg.key === 'letterbox') {
                letterboxSel.show(msg.error ? 'auto' : msg.value);
            } else if (msg.key === 'levels') {
                levelsSel.show(msg.error ? 'on' : msg.value);
            } else if (msg.key === 'autostart') {
                autostartSel.show(msg.error ? 'on' : msg.value);
            } else if (msg.key === 'uiScale') {
                uiScaleSel.show(msg.error ? 100 : Number(msg.value));
            } else if (msg.key === 'ledPreview') {
                showLedPreview(msg);
            } else if (msg.key === 'captureStats') {
                showCaptureStats(msg);
            } else if (captureStatusKeys.indexOf(msg.key) !== -1) {
                showCaptureStatus(msg);
            }
            break;
        case events.SSDPScanResult: {
            for (const device of msg.devices || []) {
                // Any device on the network can answer the search, so its name and address are untrusted:
                // the address must be a plain ws(s)://host[:port][/], and both go in as text, never as HTML.
                const base = String(device.UrlBase || '');
                const url = /^https:/i.test(base) ? 'wss' + base.slice(5) : /^http:/i.test(base) ? 'ws' + base.slice(4) : '';
                if (!/^wss?:\/\/([a-z0-9.-]+|\[[0-9a-f:.]+\])(:\d{1,5})?\/?$/i.test(url) || ssdpDevices.some(d => d.url === url)) continue;

                const friendlyName = String(device.FriendlyName || url).slice(0, 60);
                const item = document.createElement('div');
                item.className = 'ssdpItem';
                item.tabIndex = 0;
                item.setAttribute('data-uri', url);
                item.onclick = () => setRPC(url);
                const label = document.createElement('a');
                label.textContent = friendlyName;
                item.appendChild(label);
                document.getElementById('ssdpItems').appendChild(item);
                ssdpDevices.push({ url, friendlyName });
            }
        }
    }
}

window.setRPC = (url) =>  {
    canEnable = true;
    send({ event: events.SetConfig, key: 'rpcServer', value: url });
    showRPC(url);
}

// Shows the saved server and mirrors it into the manual host/port fields.
function showRPC(url) {
    document.getElementById('currentServer').innerText = `Currently Connected to ${url}`;
    const parsed = parseRPC(url);
    if (parsed) {
        document.getElementById('manualHost').value = parsed.host;
        document.getElementById('manualPort').value = parsed.port;
    }
}

// 'ws://host:port' / 'wss://host' -> { host, port }, or null if it doesn't look like one.
function parseRPC(url) {
    const match = /^(wss?):\/\/(\[[0-9a-f:.]+\]|[^\/:?#\s]+)(?::(\d+))?/i.exec(url || '');
    if (!match) return null;
    return { host: match[2], port: match[3] || (match[1].toLowerCase() === 'wss' ? '443' : '80') };
}

// Returns { url } or { error }.
function buildRPC(host, port) {
    host = String(host).trim();
    port = String(port).trim();
    if (!host) return { error: 'Invalid host: enter the HyperHDR IP address or hostname' };
    if (!/^([a-z0-9.-]+|\[[0-9a-f:.]+\])$/i.test(host)) return { error: `Invalid host: "${host}" (enter only an IP or hostname, no ws:// or port)` };
    if (!/^\d+$/.test(port) || +port < 1 || +port > 65535) return { error: 'Invalid port: enter a number from 1 to 65535' };
    return { url: `ws://${host}:${+port}` };
}

window.useManualServer = () => {
    const status = document.getElementById('manualStatus');
    const result = buildRPC(document.getElementById('manualHost').value, document.getElementById('manualPort').value);
    if (!result.error && (!client || client.readyState !== WebSocket.OPEN)) {
        result.error = 'Not connected to the TizenGlow service yet, try again in a moment';
    }
    status.className = result.error ? 'error' : '';
    if (result.error) return status.innerText = result.error;
    setRPC(result.url);
    status.innerText = `Manual server saved: ${result.url}`;
}