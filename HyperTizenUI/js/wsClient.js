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
        if (!canEnable) {
            alert('Please select a device or set a manual server first');
            return e.target.checked = false;
        }
        send({ event: events.SetConfig, key: 'enabled', value: e.target.checked.toString() });
    }
    send({ event: events.ReadConfig, key: 'rpcServer' });
    send({ event: events.ReadConfig, key: 'enabled' });
    send({ event: events.ScanSSDP });
    setInterval(() => {
        send({ event: events.ScanSSDP });
    }, 10000);
    send({ event: events.ReadConfig, key: 'targetFps' });
    readCaptureStatus();
    setInterval(readCaptureStatus, 3000);
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
    if (!client || client.readyState !== WebSocket.OPEN) return;
    const n = fpsPresets.length;
    const i = fpsPresets.indexOf(targetFps) + step;
    const next = fpsPresets[wrap ? (i + n) % n : Math.max(0, Math.min(n - 1, i))];
    if (next === targetFps) return;
    showTargetFps(next);
    send({ event: events.SetConfig, key: 'targetFps', value: String(next) });
}

// Written by the native service (Capturer.cs) as preferences, except captureFps which it
// serves live; all read back with ReadConfig.
const captureStatusKeys = ['captureState', 'captureFps', 'detectedApi', 'detectedPlatformVersion', 'captureCondition', 'lastCaptureError'];

function readCaptureStatus() {
    for (const key of captureStatusKeys) send({ event: events.ReadConfig, key });
}

function showCaptureStatus(msg) {
    const text = !msg.error ? String(msg.value)
        : msg.key === 'captureState' ? 'not reported (is native HyperTizen 1.0.1+ installed?)' : '-';
    const element = document.getElementById(msg.key);
    // Only touch the DOM on change: every mutation makes index.html rebuild its D-pad focus list.
    if (element.textContent !== text) element.textContent = text;
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
            } else if (captureStatusKeys.indexOf(msg.key) !== -1) {
                showCaptureStatus(msg);
            }
            break;
        case events.SSDPScanResult: {
            for (const device of msg.devices) {
                const url = device.UrlBase.indexOf('https') === 0 ? device.UrlBase.replace('https', 'wss') : device.UrlBase.replace('http', 'ws');

                if (ssdpDevices.some(d => d.url === url)) {
                    continue;
                }
                
                const friendlyName = device.FriendlyName;
                document.getElementById('ssdpItems').innerHTML += `
                <div class="ssdpItem" data-uri="${url}" data-friendlyName="${friendlyName}" tabindex="0" onclick="setRPC('${url}')">
                    <a>${friendlyName}</a>
                </div>
                `;
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
        result.error = 'Not connected to the HyperTizen service yet, try again in a moment';
    }
    status.className = result.error ? 'error' : '';
    if (result.error) return status.innerText = result.error;
    setRPC(result.url);
    status.innerText = `Manual server saved: ${result.url}`;
}