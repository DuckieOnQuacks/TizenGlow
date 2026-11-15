// Checks the settings page's logic (TizenGlowUI/js/wsClient.js) without a TV. The script runs against stub
// elements for exactly the IDs in index.html (an ID that no longer exists returns null and throws), with
// stubbed sockets, timers and TV APIs, once as the installed app (file:) and once as the TizenBrew module.
//
// usage: node tools/check-page.js        (exits non-zero when a check fails)

const fs = require('fs'), vm = require('vm'), path = require('path'), assert = require('assert');
const ui = path.join(__dirname, '..', 'TizenGlowUI');
const html = fs.readFileSync(path.join(ui, 'index.html'), 'utf8');
const source = fs.readFileSync(path.join(ui, 'js', 'wsClient.js'), 'utf8');
const ids = new Set([...html.matchAll(/id="([^"]+)"/g)].map(m => m[1]));
const serviceAppId = 'io.gh.duckieonquacks.TizenGlow';
const tvAddress = '192.168.1.20';

/** A freshly loaded page. protocol 'file:' is the installed app, anything else the TizenBrew module. */
function page(protocol, options = {}) {
    const elements = {};
    const element = id => elements[id] || (elements[id] = {
        id, value: '', textContent: '', innerHTML: '', innerText: '', className: '', checked: false, dataset: {},
        style: { setProperty() {} }, classList: { contains: () => false }, clientHeight: 0,
        appendChild() {}, getContext: null,
    });
    const log = { sockets: [], timers: [], launches: [], fetches: [], reloads: 0 };
    function Socket(url) { this.url = url; this.readyState = 1; this.sent = []; log.sockets.push(this); }
    Socket.OPEN = 1;
    Socket.prototype.send = function (m) { this.sent.push(JSON.parse(m)); };
    Socket.prototype.close = function () { this.closed = true; };
    const ctx = {
        document: {
            getElementById: id => ids.has(id) ? element(id) : null,
            documentElement: { style: { setProperty() {} } },
            activeElement: null,
            createElement: () => element('created'),
        },
        location: { protocol, reload() { log.reloads++; } },
        WebSocket: Socket,
        fetch: url => { log.fetches.push(url); return Promise.resolve({ text: () => Promise.resolve(tvAddress) }); },
        tizen: {
            application: {
                launch(id, ok, failed) {
                    log.launches.push(id);
                    if (options.launchThrows) throw new Error('no such app');
                    if (options.launchFails) failed({ message: 'not found' });
                },
            },
        },
        webapis: { network: { getIp: () => tvAddress } },
        alert: m => { throw new Error('alert: ' + m); },
        setTimeout: (fn, ms) => { log.timers.push({ fn, ms }); return 0; },
        setInterval: () => 0, clearInterval() {}, clearTimeout() {},
        atob: s => Buffer.from(s, 'base64').toString('binary'),
        console,
    };
    ctx.window = ctx;
    vm.createContext(ctx);
    vm.runInContext(source, ctx);
    return { ctx, element, log, read: name => vm.runInContext(name, ctx) };
}

const tick = () => new Promise(resolve => setImmediate(resolve));

(async () => {
    // The page talks to a service: what it asks for, and what it shows for the replies.
    {
        const p = page('https:');
        const client = new p.ctx.WebSocket('ws://test');
        vm.runInContext('client = testClient', Object.assign(p.ctx, { testClient: client }));
        p.ctx.onOpen();
        const keys = client.sent.map(m => m.key);
        for (const gone of ['rpcServer', 'outputMode']) assert(!keys.includes(gone), 'still asks for ' + gone);
        assert(keys.includes('ledPreview'), 'asks for the LED preview');

        const version = p.read('uiVersion');
        const reply = (key, value, error) => p.ctx.onMessage({ data: JSON.stringify({ Event: 2, key, value, error: !!error }) });
        reply('enabled', 'true');
        reply('nativeVersion', version);
        reply('wledTarget', '192.168.1.225:4048');
        reply('ledLayout', '85,50,85,50,bottomright,ccw');
        reply('captureState', 'running');
        reply('captureStats', {
            mode: 'Detailed', resolution: '3840x2160', performance: 'capture limited', targetFps: 10, captureFps: 7, sendFps: 7,
            averageCaptureMs: 141.9, averageFrameMs: 142.1, averageImageMs: 0, averageSendMs: 0.1, averageWaitMs: 0,
            failedFrames: 0, droppedFrames: 45, outputState: 'sending', outputTarget: '192.168.1.225:4048', peakSample: 511,
            ledCount: 270, packetsPerFrame: 1, lastSendMs: 0.08, ddpSendErrors: 0, ddpFramesSent: 129, ddpPacketsSent: 129,
            wledLedCount: 270, points: 14, pointsPerEdge: '4/3/4/3', pointsPerRound: 2, sleepMs: 20, ceilingFps: 7.1,
            letterbox: 'watching', autostart: 'on', levels: { enabled: true, white: '255,255,255', black: '0,0,0' },
        });
        reply('ledPreview', 'No LED frame yet.', true);
        for (const key of ['wledBrightness', 'wledGamma', 'letterbox', 'autostart', 'uiScale']) reply(key, 'x', true);

        assert.strictEqual(p.element('versions').textContent, 'v' + version);
        assert.strictEqual(p.element('statOutputTarget').textContent, '192.168.1.225:4048');
        assert.strictEqual(p.element('statAutostart').textContent, 'on (checks every 10 min)');
        assert.strictEqual(p.element('wledHost').value, '192.168.1.225');

        client.sent.length = 0;
        p.element('enabled').checked = true;
        p.element('enabled').onchange({ target: p.element('enabled') });
        assert.deepStrictEqual(client.sent, [{ event: 0, key: 'enabled', value: 'true' }]);
    }

    // Installed app: starts the service, tries this TV's loopback address, then its own address.
    {
        const p = page('file:');
        p.ctx.startPage();
        assert.deepStrictEqual(p.log.launches, [serviceAppId], 'the app starts the service');
        assert.strictEqual(p.log.sockets[0].url, 'ws://127.0.0.1:8086');
        p.log.sockets[0].onerror();
        assert.strictEqual(p.log.sockets[1].url, `ws://${tvAddress}:8086`, 'falls back to the TV address');
        p.log.sockets[1].onopen();
        assert(p.log.sockets[1].closed, 'the test connection is closed');
        assert.strictEqual(p.read('deviceIP'), tvAddress);
        assert.strictEqual(p.log.sockets[2].url, `ws://${tvAddress}:8086`, 'the page connects where the test worked');
        assert.strictEqual(typeof p.log.sockets[2].onmessage, 'function');
        assert.strictEqual(p.log.fetches.length, 0, 'the app does not ask TizenBrew for the address');
    }

    // Installed app, service still starting: retries after a second, launching again, without reloading.
    {
        const p = page('file:');
        p.ctx.startPage();
        p.log.sockets[0].onerror();
        p.log.sockets[1].onerror();
        assert.strictEqual(p.log.reloads, 0, 'no reload while the service starts');
        assert.deepStrictEqual(p.log.timers.map(t => t.ms), [1000]);
        p.log.timers[0].fn();
        assert.deepStrictEqual(p.log.launches, [serviceAppId, serviceAppId]);
        assert.strictEqual(p.log.sockets[2].url, 'ws://127.0.0.1:8086');
    }

    // Installed app, the service's TPK isn't installed: it says so.
    for (const how of ['launchFails', 'launchThrows']) {
        const p = page('file:', { [how]: true });
        p.ctx.startPage();
        assert.strictEqual(p.element('status').textContent, 'Service not found: install the TPK', how);
    }

    // TizenBrew module: as before. The address comes from TizenBrew, which also starts the service.
    {
        const p = page('https:');
        p.ctx.startPage();
        await tick();
        assert.deepStrictEqual(p.log.fetches, ['http://127.0.0.1:8081']);
        assert.strictEqual(p.log.launches.length, 0, 'the module leaves starting the service to TizenBrew');
        assert.strictEqual(p.log.sockets[0].url, `ws://${tvAddress}:8086`);
        p.log.sockets[0].onerror();
        assert.strictEqual(p.log.reloads, 1, 'the module reloads when it cannot connect');
        assert.strictEqual(p.log.timers.length, 0);

        const q = page('https:');
        q.ctx.startPage();
        await tick();
        q.log.sockets[0].onopen();
        assert.strictEqual(q.read('deviceIP'), tvAddress);
        assert.strictEqual(q.log.sockets[1].url, `ws://${tvAddress}:8086`);
    }

    // A service newer than the page: each host is told how to update its own page.
    {
        const app = page('file:'), brew = page('https:');
        app.ctx.showVersions('99.0.0');
        brew.ctx.showVersions('99.0.0');
        assert(app.element('versions').textContent.endsWith('install the latest app'), app.element('versions').textContent);
        assert(brew.element('versions').textContent.endsWith('update the TizenBrew module'), brew.element('versions').textContent);
    }

    console.log('page check passed');
})().catch(e => { console.error(e); process.exit(1); });
