// Calibration screen: full-screen test patterns shown by the TV itself. The running capture sees them like
// any other picture, so they test the whole path: screen -> Samsung capture -> TizenGlow -> WLED Direct /
// HyperHDR -> LEDs. Nothing is sent from here except the LED shift (UP / DOWN on still patterns); Test WLED
// stays the network-only test.
//
// Every pattern is a few rectangles in screen fractions with exact CSS colours or gradients (no canvas,
// filters or opacity). The capture samples sit 5-7% in from each edge (Capturer.cs): edge bands of 12.5%
// cover them with room to spare, and the centre, where the info box goes, isn't sampled at all.
const calibration = (() => {
    const BAND = 0.125;                      // edge band depth
    const CORNER = 1 / 3;                    // corner blocks: each reaches samples on both of its edges
    const MARKER = 1 / 9;                    // marker length, share of the perimeter, at least: it must cover every gap between samples
    const LAPS = [1.5, 2, 2.5, 3, 4, 6, 10]; // marker seconds per lap; UP = faster
    const IDLE_MS = 10 * 60 * 1000;          // still patterns close after 10 min without a key (OLED)
    const INFO_MS = { still: 8000, uniform: 2500 };

    const hex2 = v => (v < 16 ? '0' : '') + v.toString(16);
    const hex = (r, g, b) => ('#' + hex2(r) + hex2(g) + hex2(b)).toUpperCase();

    // Fully saturated, full brightness hue (degrees) as #RRGGBB.
    function hue(h) {
        h = ((h % 360) + 360) % 360;
        const f = n => { const k = (n + h / 60) % 6; return Math.round(255 * (1 - Math.max(0, Math.min(k, 4 - k, 1)))); };
        return hex(f(5), f(3), f(1));
    }

    // Hues h0..h1 (degrees, h0 < h1) along `direction`. Between multiples of 60 degrees a hue is a straight
    // line in RGB, so with stops there the browser's RGB interpolation gives the exact hue everywhere.
    function hueGradient(direction, h0, h1) {
        const stops = [h0];
        for (let h = Math.floor(h0 / 60) * 60 + 60; h < h1; h += 60) stops.push(h);
        stops.push(h1);
        return `linear-gradient(${direction}, ${stops.map(h => `${hue(h)} ${(100 * (h - h0) / (h1 - h0)).toFixed(3)}%`).join(', ')})`;
    }

    const rect = (x, y, w, h, bg) => ({ x, y, w, h, bg });
    const fill = bg => () => [rect(0, 0, 1, 1, bg)];

    // The perimeter runs clockwise from the top-left corner, as the LED layout does: top left to right,
    // right top to bottom, bottom right to left, left bottom to top. Positions are in screen pixels.
    const sideNames = ['top', 'right', 'bottom', 'left'];
    const sideLengths = (W, H) => [W, H, W, H];

    // Part of one side's band, `from`..`to` pixels along that side.
    function band(side, from, to, W, H, bg) {
        switch (side) {
            case 0: return rect(from / W, 0, (to - from) / W, BAND, bg);
            case 1: return rect(1 - BAND, from / H, BAND, (to - from) / H, bg);
            case 2: return rect(1 - to / W, 1 - BAND, (to - from) / W, BAND, bg);
            default: return rect(0, 1 - to / H, BAND, (to - from) / H, bg);
        }
    }

    // The TV's sample points, from the service's stats (x, y in screen fractions); the Balanced set until known.
    function samplePoints() {
        const fromStats = typeof lastStats !== 'undefined' && lastStats && lastStats.capturePoints;
        if (fromStats && fromStats.length) return fromStats;
        return [[0.25, 0.05], [0.75, 0.05], [0.95, 0.25], [0.95, 0.75], [0.75, 0.95], [0.25, 0.95], [0.05, 0.75], [0.05, 0.25]];
    }

    // Where a sample sits along the perimeter (pixels clockwise from the top-left corner).
    function perimeterPosition(p, W, H) {
        const [x, y] = p;
        if (x <= 0.06) return 2 * W + H + (1 - y) * H;     // left column (5% in), bottom to top
        if (x >= 0.94) return W + y * H;                   // right column, top to bottom
        return y < 0.5 ? x * W : W + H + (1 - x) * W;      // top or bottom row (moved in for black bars at most)
    }

    // Marker length as a share of the perimeter: MARKER, or 15% more than the widest gap between samples,
    // so the block always lights at least one sample wherever it is (Fast has one sample per side).
    function markerShare(W, H) {
        const P = 2 * (W + H);
        const pos = samplePoints().map(p => perimeterPosition(p, W, H)).sort((a, b) => a - b);
        let gap = 0;
        for (let i = 0; i < pos.length; i++) gap = Math.max(gap, (i + 1 < pos.length ? pos[i + 1] : pos[0] + P) - pos[i]);
        return Math.min(0.5, Math.max(MARKER, 1.15 * gap / P));
    }

    // White block, `phase` (0..1) of the way round; split over two sides at a corner.
    function marker(W, H, phase) {
        const P = 2 * (W + H), L = P * markerShare(W, H), a = phase * P, out = [];
        let start = 0;
        sideLengths(W, H).forEach((len, side) => {
            for (const shift of [0, P]) { // the block wraps past the top-left corner
                const from = Math.max(start, a - shift), to = Math.min(start + len, a + L - shift);
                if (to > from) out.push(band(side, from - start, to - start, W, H, '#FFFFFF'));
            }
            start += len;
        });
        return out;
    }

    // Side the middle of the marker is on.
    function markerSide(W, H, phase) {
        const P = 2 * (W + H);
        let p = (phase * P + P * markerShare(W, H) / 2) % P, side = 0;
        for (const len of sideLengths(W, H)) { if (p < len) break; p -= len; side++; }
        return sideNames[side];
    }

    // Hue follows the position along the perimeter, so it lands on the LEDs in the same order. The side
    // bands sit between the top and bottom bands; their gradients start where they do.
    function rainbow(W, H) {
        const P = 2 * (W + H), deg = p => 360 * p / P;
        return [
            rect(0, 0, 1, BAND, hueGradient('to right', 0, deg(W))),
            rect(1 - BAND, BAND, BAND, 1 - 2 * BAND, hueGradient('to bottom', deg(W + BAND * H), deg(W + (1 - BAND) * H))),
            rect(0, 1 - BAND, 1, BAND, hueGradient('to left', deg(W + H), deg(2 * W + H))),
            rect(0, BAND, BAND, 1 - 2 * BAND, hueGradient('to top', deg(2 * W + H + BAND * H), deg(2 * W + H + (1 - BAND) * H))),
        ];
    }

    const patterns = [
        { name: 'Full Red', info: '#FF0000 everywhere: every LED red', uniform: true, rects: fill('#FF0000') },
        { name: 'Full Green', info: '#00FF00 everywhere: every LED green', uniform: true, rects: fill('#00FF00') },
        { name: 'Full Blue', info: '#0000FF everywhere: every LED blue', uniform: true, rects: fill('#0000FF') },
        { name: 'Full White', info: '#FFFFFF everywhere: every LED white', uniform: true, rects: fill('#FFFFFF') },
        { name: 'Full Black', info: '#000000 everywhere: LEDs off (checks the black threshold)', uniform: true, rects: fill('#000000') },
        { name: 'Hue Gradient', info: 'Red, yellow, green, cyan, blue, magenta, red from left to right', uniform: true, rects: () => [rect(0, 0, 1, 1, hueGradient('to right', 0, 360))] },
        {
            name: 'Four Edges', info: 'Top red, right green, bottom blue, left yellow',
            rects: () => [rect(0, 0, 1, BAND, '#FF0000'), rect(1 - BAND, BAND, BAND, 1 - 2 * BAND, '#00FF00'),
                rect(0, 1 - BAND, 1, BAND, '#0000FF'), rect(0, BAND, BAND, 1 - 2 * BAND, '#FFFF00')],
        },
        {
            name: 'Four Corners', info: 'Top-left red, top-right green, bottom-right blue, bottom-left yellow',
            rects: () => [rect(0, 0, CORNER, CORNER, '#FF0000'), rect(1 - CORNER, 0, CORNER, CORNER, '#00FF00'),
                rect(1 - CORNER, 1 - CORNER, CORNER, CORNER, '#0000FF'), rect(0, 1 - CORNER, CORNER, CORNER, '#FFFF00')],
        },
        { name: 'Moving Edge Marker', info: 'A white block going clockwise round the edge', animated: true, rects: marker },
        {
            name: 'Sample Points', info: 'Where the TV samples the picture: the LEDs follow only these spots',
            rects: () => samplePoints().map(([x, y]) => rect(x - 0.015, y - 0.02665, 0.03, 0.0533, '#FFFFFF')), // 3% wide, the same on screen tall
        },
        { name: 'Edge Rainbow', info: 'Red, yellow, green, cyan, blue, magenta clockwise from the top-left corner', rects: rainbow },
    ];

    // ---- screen ----

    let root = null, stage, infoBox, nameLine, infoLine, warnLine, statsLine, keysLine;
    let isOpen = false, index = 0, lap = 2.5, paused = false, start = 0, pausedPhase = 0;
    let lastKey = 0, frameId = 0, tickTimer = null, infoTimer = null;
    const W = () => window.innerWidth || 1920, H = () => window.innerHeight || 1080;

    function line(id) {
        const p = document.createElement('p');
        p.id = id;
        infoBox.appendChild(p);
        return p;
    }

    function build() {
        root = document.createElement('div');
        root.id = 'calibration';
        stage = document.createElement('div');
        stage.id = 'calStage';
        infoBox = document.createElement('div');
        infoBox.id = 'calInfo';
        root.appendChild(stage);
        root.appendChild(infoBox);
        nameLine = line('calName');
        infoLine = line('calText');
        warnLine = line('calWarn');
        statsLine = line('calStats');
        keysLine = line('calKeys');
        document.body.appendChild(root);
    }

    const pct = v => (v * 100).toFixed(4) + '%';

    function draw(rects) {
        while (stage.children.length > rects.length) stage.removeChild(stage.lastChild);
        while (stage.children.length < rects.length) stage.appendChild(document.createElement('div'));
        rects.forEach((q, i) => {
            const el = stage.children[i];
            const css = `left: ${pct(q.x)}; top: ${pct(q.y)}; width: ${pct(q.w)}; height: ${pct(q.h)}; background: ${q.bg};`;
            if (el.calCss !== css) { el.style.cssText = css; el.calCss = css; }
        });
    }

    const phase = () => paused ? pausedPhase : ((Date.now() - start) / (lap * 1000)) % 1;

    function frame() {
        frameId = 0;
        if (!isOpen || !patterns[index].animated) return;
        draw(marker(W(), H(), phase()));
        if (!paused) frameId = requestAnimationFrame(frame);
    }

    function stopFrames() {
        if (frameId) cancelAnimationFrame(frameId);
        frameId = 0;
    }

    function show(i) {
        index = (i + patterns.length) % patterns.length;
        paused = false;
        start = Date.now();
        stopFrames();
        if (patterns[index].animated) frame(); else draw(patterns[index].rects(W(), H()));
    }

    const read = id => { const el = document.getElementById(id); return el ? el.textContent : '-'; };
    const wled = () => typeof outputSel !== 'undefined' && outputSel.value === 'wled';

    function showText() {
        if (wizard) return showWizardText();
        const p = patterns[index];
        setLine(nameLine, `${index + 1} / ${patterns.length}   ${p.name}`);
        setLine(infoLine, !p.animated ? p.info
            : `${p.info}: ${paused ? 'paused' : 'now'} on the ${markerSide(W(), H(), phase())} edge, ${lap} s per lap`);
        const state = read('captureState');
        setLine(warnLine, state === 'running' ? '' : `Capture isn't running (${state.split(' (')[0]}), so the LEDs won't follow this screen.`);
        const count = read('statLedCount');
        const shift = typeof ledShift === 'undefined' || ledShift === 0 ? '' : `, LED shift ${ledShift > 0 ? '+' : ''}${ledShift}`;
        const points = typeof lastStats !== 'undefined' && lastStats && lastStats.points ? `${lastStats.points} points, ` : '';
        setLine(statsLine, `Capture ${read('detectedApi')}, ${points}${read('statCaptureFps')} fps captured, ${read('statSendFps')} fps sent. `
            + `Output ${wled() ? `WLED Direct${/^\d+$/.test(count) ? `, ${count} LEDs` : count === '-' ? '' : `, ${count}`}${shift}` : 'HyperHDR'}`);
        setLine(keysLine, p.animated ? 'LEFT / RIGHT pattern    OK pause    UP / DOWN speed    BACK exit'
            : wled() ? 'LEFT / RIGHT pattern    UP / DOWN shift LEDs    OK hide info    BACK exit'
            : 'LEFT / RIGHT pattern    OK hide info    BACK exit');
    }

    function setLine(el, text) { if (el.textContent !== text) el.textContent = text; }

    // The info box shows on every key and hides itself, so nothing static stays on screen.
    function showInfo() {
        infoBox.style.display = '';
        showText();
        clearTimeout(infoTimer);
        infoTimer = setTimeout(hideInfo, patterns[index].uniform ? INFO_MS.uniform : INFO_MS.still);
    }

    function hideInfo() {
        clearTimeout(infoTimer);
        infoBox.style.display = 'none';
    }

    function tick() {
        if (wizard) wizardTick();
        if (infoBox.style.display !== 'none') showText();
        const moving = patterns[index].animated && !paused;
        if (!moving && Date.now() - lastKey >= IDLE_MS) close('timeout');
    }

    // ---- Levels wizard: Full White, measure; Full Black, measure; back to the settings with the result ----
    const WIZARD = { settleMs: 1500, timeoutMs: 20000 };
    let wizard = null, wizardTimer = null;

    function calibrateLevels() {
        const status = document.getElementById('levelsStatus');
        if (typeof connected !== 'function' || !connected()) {
            if (status) { status.className = 'hint warn'; status.textContent = 'Not connected to the TizenGlow service yet, try again in a moment.'; }
            return;
        }
        if (read('captureState') !== 'running') {
            if (status) { status.className = 'hint warn'; status.textContent = 'Enable capture first: levels are measured through the running capture.'; }
            return;
        }
        if (!isOpen) open();
        wizard = { step: 'white', results: [], sentAt: 0 };
        wizardStep('white');
    }

    function wizardStep(step) {
        wizard.step = step;
        wizard.sentAt = 0;
        show(step === 'white' ? 3 : 4); // Full White / Full Black
        clearTimeout(infoTimer);
        infoBox.style.display = '';
        showWizardText();
        clearTimeout(wizardTimer);
        wizardTimer = setTimeout(() => {
            if (!wizard || wizard.step !== step) return;
            // A stats reply already in flight may still carry the previous result: only a new one counts.
            const lv = typeof lastStats !== 'undefined' && lastStats && lastStats.levels;
            wizard.before = lv ? lv.result : null;
            wizard.sentAt = Date.now();
            send({ event: events.SetConfig, key: 'measureLevels', value: step });
            showWizardText();
        }, WIZARD.settleMs);
    }

    function showWizardText() {
        setLine(nameLine, `Calibrating levels: ${wizard.step}`);
        setLine(infoLine, wizard.sentAt ? `Measuring ${wizard.step}... keep the screen as it is` : `Showing full ${wizard.step}; measuring in a moment`);
        setLine(warnLine, '');
        setLine(statsLine, wizard.results.join('. '));
        setLine(keysLine, 'BACK cancels');
    }

    function wizardTick() {
        if (!wizard.sentAt) return;
        const lv = typeof lastStats !== 'undefined' && lastStats && lastStats.levels;
        const done = lv && !lv.measuring && lv.result && lv.result !== wizard.before && lv.result.indexOf(wizard.step + ' ') === 0;
        if (!done && Date.now() - wizard.sentAt < WIZARD.timeoutMs) return;
        wizard.results.push(done ? lv.result : `${wizard.step}: no answer from the service`);
        if (wizard.step === 'white') return wizardStep('black');
        const results = wizard.results;
        wizard = null;
        close('levels');
        const status = document.getElementById('levelsStatus');
        if (status) {
            status.className = results.some(r => /not measured|no answer/.test(r)) ? 'hint warn' : 'hint';
            status.textContent = results.map(r => r.charAt(0).toUpperCase() + r.slice(1) + (/[.?!]$/.test(r) ? '' : '.')).join(' ');
            status.dataset.result = results[results.length - 1];
        }
    }

    function cancelWizard() {
        if (!wizard) return;
        wizard = null;
        clearTimeout(wizardTimer);
        const status = document.getElementById('levelsStatus');
        if (status) { status.className = 'hint'; status.textContent = 'Calibration cancelled.'; }
    }

    function open() {
        if (isOpen) return;
        if (!root) build();
        isOpen = true;
        root.style.display = 'block';
        lastKey = Date.now();
        show(index);
        showInfo();
        tickTimer = setInterval(tick, 500);
    }

    function close(reason) {
        if (!isOpen) return;
        isOpen = false;
        root.style.display = 'none';
        stopFrames();
        clearInterval(tickTimer);
        clearTimeout(infoTimer);
        clearTimeout(wizardTimer);
        const status = document.getElementById('calibrationStatus');
        if (status && reason !== 'levels') status.textContent = reason === 'timeout'
            ? 'Calibration closed after 10 minutes without a key press, to protect the screen.'
            : `Last pattern: ${patterns[index].name}. Opens there next time.`;
        const button = document.getElementById(reason === 'levels' ? 'levelsCalibrate' : 'calibrationOpen');
        if (button) button.focus();
    }

    function speed(step) {
        const p = phase();
        lap = LAPS[Math.max(0, Math.min(LAPS.length - 1, LAPS.indexOf(lap) + step))];
        if (paused) pausedPhase = p; else start = Date.now() - p * lap * 1000;
    }

    function togglePause() {
        if (paused) { start = Date.now() - pausedPhase * lap * 1000; paused = false; frame(); }
        else {
            pausedPhase = phase();
            paused = true;
            stopFrames();
            draw(marker(W(), H(), pausedPhase));
        }
    }

    const keyNames = { ArrowLeft: 'left', ArrowRight: 'right', ArrowUp: 'up', ArrowDown: 'down', Enter: 'ok',
        Escape: 'back', Backspace: 'back', XF86Back: 'back', GoBack: 'back', BrowserBack: 'back' };
    const keyCodes = { 37: 'left', 39: 'right', 38: 'up', 40: 'down', 13: 'ok', 27: 'back', 8: 'back', 10009: 'back' };

    // Registered first, in the capture phase: while open, no key reaches the settings page or TizenBrew.
    window.addEventListener('keydown', e => {
        if (!isOpen) return;
        e.preventDefault();
        e.stopImmediatePropagation();
        lastKey = Date.now();
        const key = keyCodes[e.keyCode] === 'back' ? 'back' : keyNames[e.key] || keyCodes[e.keyCode];
        const p = patterns[index];
        if (wizard) { cancelWizard(); return close('levels'); } // any key cancels the measurement
        if (key === 'back') return close();
        if (key === 'left' || key === 'right') show(index + (key === 'right' ? 1 : -1));
        else if (key === 'ok' && !p.animated) return infoBox.style.display === 'none' ? showInfo() : hideInfo();
        else if (key === 'ok') togglePause();
        else if ((key === 'up' || key === 'down') && p.animated) speed(key === 'up' ? -1 : 1);
        // Still patterns, WLED Direct: move the LEDs one step clockwise (UP) or counter-clockwise (DOWN).
        else if ((key === 'up' || key === 'down') && wled() && window.stepLedShift) window.stepLedShift(key === 'up' ? 1 : -1);
        showInfo();
    }, true);

    return {
        open, close, calibrateLevels, patterns, hue, hueGradient, marker, markerSide, markerShare, samplePoints, rainbow, BAND, CORNER, MARKER, LAPS, IDLE_MS, WIZARD,
        state: () => ({ isOpen, index, lap, paused, info: !!root && infoBox.style.display !== 'none', wizard: wizard ? wizard.step : null }),
    };
})();

window.openCalibration = calibration.open;
window.calibrateLevels = calibration.calibrateLevels;
