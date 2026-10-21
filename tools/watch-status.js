// Prints what the TizenGlow service on the TV reports, once a second, from a PC on the same network, so
// you can see what capture is doing while the TV shows something else (a console, an HDMI input, an app).
// Read-only: it only asks for status, it never changes a setting.
//
// usage: node tools/watch-status.js <TV IP>        e.g. node tools/watch-status.js 192.168.1.20
// Needs Node.js 22 or newer (built-in WebSocket). Stop it with Ctrl+C.

const tv = process.argv[2];
if (!tv) {
    console.log('usage: node tools/watch-status.js <TV IP>');
    process.exit(1);
}

const time = () => new Date().toTimeString().slice(0, 8);
const fmt = v => v === null || v === undefined ? '-' : typeof v === 'number' ? v.toFixed(1) : String(v);
let ws = null, status = {}, lastCondition = null, lastError = null, lastState = null, connected = false, unreachable = false;

function connect() {
    const sock = new WebSocket(`ws://${tv}:8086`);
    ws = sock;
    let done = false;
    // Node reports a refused or unreachable host with an error and no close event; either one ends this try.
    const lost = () => {
        if (done) return;
        done = true;
        clearTimeout(giveUp);
        if (connected) console.log(`${time()} connection lost: the TV went to sleep, or the TizenGlow service stopped`);
        else if (!unreachable) console.log(`${time()} cannot reach ${tv}:8086: TV off, or the TizenGlow service is not running (open it in TizenBrew once). Retrying...`);
        unreachable = !connected;
        connected = false;
        try { sock.close(); } catch (e) { }
        setTimeout(connect, 3000);
    };
    const giveUp = setTimeout(lost, 3000);
    sock.onopen = () => {
        clearTimeout(giveUp);
        connected = true;
        unreachable = false;
        console.log(`${time()} connected to TizenGlow on ${tv}`);
        sock.send(JSON.stringify({ event: 1, key: 'nativeVersion' }));
    };
    sock.onmessage = m => {
        let msg;
        try { msg = JSON.parse(m.data); } catch (e) { return; }
        if (msg.Event !== 2) return;
        if (msg.key === 'nativeVersion') return console.log(`${time()} service ${msg.error ? 'older than 1.0.6' : msg.value}`);
        status[msg.key] = msg.error ? null : msg.value;
    };
    sock.onclose = lost;
    sock.onerror = lost;
}

function tick() {
    if (!connected) return;
    // Print the previous second's answers, then ask again.
    const s = status.captureStats;
    if (s || status.captureState) {
        const state = status.captureState || '-';
        if (state !== lastState) { console.log(`${time()} state: ${state}`); lastState = state; }
        if (s) {
            const levels = s.levels && s.levels.enabled && s.levels.white !== '255,255,255' ? ` levels ${s.levels.white}/${s.levels.black}` : '';
            console.log(`${time()} ${fmt(state).padEnd(8)} capture ${fmt(s.captureFps).padStart(5)} fps  send ${fmt(s.sendFps).padStart(5)} fps  `
                + `frame ${fmt(s.averageCaptureMs)} ms  peak ${s.peakSample === null || s.peakSample === undefined ? '-' : s.peakSample}  failed ${s.failedFrames}  ${s.mode} ${s.points || ''} pts  `
                + `${s.output} ${s.outputState || ''}  bars ${s.letterbox || '-'}${levels}`);
        }
        if (status.captureCondition && status.captureCondition !== lastCondition) {
            console.log(`${time()} condition: ${status.captureCondition}`);
            lastCondition = status.captureCondition;
        }
        if (status.lastCaptureError && status.lastCaptureError !== lastError) {
            console.log(`${time()} last error: ${status.lastCaptureError}`);
            lastError = status.lastCaptureError;
        }
    }
    status = {};
    for (const key of ['captureState', 'captureCondition', 'lastCaptureError', 'captureStats']) ws.send(JSON.stringify({ event: 1, key }));
}

connect();
setInterval(tick, 1000);
