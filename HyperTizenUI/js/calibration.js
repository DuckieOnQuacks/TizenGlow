// Calibration screen: full-screen test patterns shown by the TV itself. The running capture sees them like
// any other picture, so they test the whole path: screen -> Samsung capture -> HyperTizen -> WLED Direct /
// HyperHDR -> LEDs. Nothing is sent from here except the LED shift (UP / DOWN on still patterns); Test WLED
// stays the network-only test.
//
// Every pattern is a few rectangles in screen fractions with exact CSS colours or gradients (no canvas,
// filters or opacity). The capture samples sit 5-7% in from each edge (Capturer.cs): edge bands of 12.5%
// cover them with room to spare, and the centre, where the info box goes, isn't sampled at all.
const calibration = (() => {
    const BAND = 0.125;                      // edge band depth
    const CORNER = 1 / 3;                    // corner blocks: each reaches samples on both of its edges
    const MARKER = 1 / 9;                    // marker length, share of the perimeter: wider than any gap between samples
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

    // White block, `phase` (0..1) of the way round; split over two sides at a corner.
    function marker(W, H, phase) {
        const P = 2 * (W + H), L = P * MARKER, a = phase * P, out = [];
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
        let p = (phase * P + P * MARKER / 2) % P, side = 0;
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
        const p = patterns[index];
        setLine(nameLine, `${index + 1} / ${patterns.length}   ${p.name}`);
        setLine(infoLine, !p.animated ? p.info
            : `${p.info}: ${paused ? 'paused' : 'now'} on the ${markerSide(W(), H(), phase())} edge, ${lap} s per lap`);
        const state = read('captureState');
        setLine(warnLine, state === 'running' ? '' : `Capture isn't running (${state.split(' (')[0]}), so the LEDs won't follow this screen.`);
        const count = read('statLedCount');
        const shift = typeof ledShift === 'undefined' || ledShift === 0 ? '' : `, LED shift ${ledShift > 0 ? '+' : ''}${ledShift}`;
        setLine(statsLine, `Capture ${read('detectedApi')}, ${read('statCaptureFps')} fps captured, ${read('statSendFps')} fps sent. `
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
        if (infoBox.style.display !== 'none') showText();
        const moving = patterns[index].animated && !paused;
        if (!moving && Date.now() - lastKey >= IDLE_MS) close('timeout');
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
        const status = document.getElementById('calibrationStatus');
        if (status) status.textContent = reason === 'timeout'
            ? 'Calibration closed after 10 minutes without a key press, to protect the screen.'
            : `Last pattern: ${patterns[index].name}. Opens there next time.`;
        const button = document.getElementById('calibrationOpen');
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
        open, close, patterns, hue, hueGradient, marker, markerSide, rainbow, BAND, CORNER, MARKER, LAPS, IDLE_MS,
        state: () => ({ isOpen, index, lap, paused, info: !!root && infoBox.style.display !== 'none' }),
    };
})();

window.openCalibration = calibration.open;
