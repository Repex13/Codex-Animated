(() => {
  "use strict";

  const width = 1080;
  const height = 720;
  const duration = 12000;
  const background = "#08141d";
  const inks = ["#60aaab", "#9ae8ce", "#e0f2cf", "#e3b37d"];
  const art = document.getElementById("artwork");
  const ctx = document.getElementById("screen").getContext("2d", { alpha: false });
  const motion = window.matchMedia("(prefers-reduced-motion: reduce)");
  let finished = false;
  let frameId = 0;
  let timer = 0;

  function finish(message) {
    if (finished) return;
    finished = true;
    cancelAnimationFrame(frameId);
    clearTimeout(timer);
    motion.removeEventListener("change", motionChanged);
    if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(message);
  }
  function motionChanged(event) { if (event.matches) finish("complete"); }
  document.getElementById("skip").addEventListener("click", () => finish("complete"));
  document.addEventListener("keydown", event => {
    if (event.key === "Escape") { event.preventDefault(); finish("complete"); }
  });
  motion.addEventListener("change", motionChanged);
  if (motion.matches) { finish("complete"); return; }

  function smooth(value) {
    const x = Math.max(0, Math.min(1, value));
    return x * x * (3 - 2 * x);
  }
  function ramp(t, start, end) { return smooth((t - start) / (end - start)); }
  function surface(w, h) {
    const canvas = document.createElement("canvas");
    canvas.width = w;
    canvas.height = h;
    return canvas;
  }
  function photoPlate() {
    const canvas = surface(width, height);
    const scale = Math.max(width / art.naturalWidth, height / art.naturalHeight);
    const w = art.naturalWidth * scale;
    const h = art.naturalHeight * scale;
    canvas.getContext("2d").drawImage(art, (width - w) / 2, (height - h) / 2, w, h);
    return canvas;
  }

  // Marching squares. Each pair joins two sides of a tile at the chosen brightness.
  // Using the actual image ties the line drawing to whichever wallpaper was picked.
  const cases = [[], [3, 0], [0, 1], [3, 1], [1, 2], [3, 0, 1, 2], [0, 2], [3, 2],
    [2, 3], [2, 0], [0, 1, 2, 3], [2, 1], [1, 3], [1, 0], [0, 3], []];

  function makeDrawing(photo) {
    const cols = 136, rows = 91;
    const sampler = surface(cols, rows);
    const sample = sampler.getContext("2d", { willReadFrequently: true });
    sample.drawImage(photo, 0, 0, cols, rows);
    const rgba = sample.getImageData(0, 0, cols, rows).data;
    const values = new Uint8Array(cols * rows);
    const histogram = new Uint32Array(256);
    for (let i = 0; i < values.length; i++) {
      const p = 4 * i;
      const lum = Math.round(.24 * rgba[p] + .63 * rgba[p + 1] + .13 * rgba[p + 2]);
      values[i] = lum;
      histogram[lum]++;
    }
    const levels = [];
    let sum = 0, next = .08;
    for (let lum = 0; lum < histogram.length; lum++) {
      sum += histogram[lum] / values.length;
      if (sum >= next) {
        if (!levels.length || lum - levels[levels.length - 1] >= 5) levels.push(lum);
        while (next <= sum) next += .115;
      }
    }
    const groups = inks.map(() => []);
    const dw = width / (cols - 1), dh = height / (rows - 1);
    let sequence = 371;
    function random() {
      sequence = (Math.imul(sequence, 1664525) + 1013904223) >>> 0;
      return sequence / 4294967296;
    }
    function segment(group, x1, y1, x2, y2) {
      const x = (x1 + x2) / 2, y = (y1 + y2) / 2;
      groups[group].push({
        x, y, dx: (x2 - x1) / 2, dy: (y2 - y1) / 2,
        originX: width * (.31 + random() * .38),
        originY: height * (.38 + random() * .19),
        bow: (random() - .5) * height * .6,
        delay: .45 * x / width + .14 * random(), angle: (random() - .5) * 4
      });
    }
    const ex = [0, 0, 0, 0], ey = [0, 0, 0, 0];
    function fraction(a, b, level) { return a === b ? .5 : (level - a) / (b - a); }
    levels.forEach((level, index) => {
      const group = index < 3 ? 0 : index === levels.length - 1 ? 3 : 1 + (index % 2);
      for (let j = 0; j < rows - 1; j++) {
        for (let i = 0; i < cols - 1; i++) {
          const p = i + j * cols;
          const a = values[p], b = values[p + 1], c = values[p + cols + 1], d = values[p + cols];
          const sides = cases[(a > level) | ((b > level) << 1) | ((c > level) << 2) | ((d > level) << 3)];
          if (!sides.length) continue;
          ex[0] = (i + fraction(a, b, level)) * dw; ey[0] = j * dh;
          ex[1] = (i + 1) * dw; ey[1] = (j + fraction(b, c, level)) * dh;
          ex[2] = (i + fraction(d, c, level)) * dw; ey[2] = (j + 1) * dh;
          ex[3] = i * dw; ey[3] = (j + fraction(a, d, level)) * dh;
          for (let k = 0; k < sides.length; k += 2) {
            const m = sides[k], n = sides[k + 1];
            segment(group, ex[m], ey[m], ex[n], ey[n]);
          }
        }
      }
    });

    // Flat-color images still get a moving aperture before their color is revealed.
    if (groups.reduce((count, group) => count + group.length, 0) < 80) {
      for (let ring = 0; ring < 5; ring++) {
        for (let tick = 0; tick < 96; tick++) {
          const a = tick * Math.PI / 48, b = (tick + 1) * Math.PI / 48;
          const r = 65 + ring * 34;
          segment(ring % inks.length, width / 2 + Math.cos(a) * r * 1.7, height / 2 + Math.sin(a) * r,
            width / 2 + Math.cos(b) * r * 1.7, height / 2 + Math.sin(b) * r);
        }
      }
    }
    const plate = surface(width, height), ink = plate.getContext("2d");
    ink.lineWidth = 1.3;
    groups.forEach((group, i) => {
      ink.beginPath();
      for (const s of group) {
        ink.moveTo(s.x - s.dx, s.y - s.dy);
        ink.lineTo(s.x + s.dx, s.y + s.dy);
      }
      ink.strokeStyle = inks[i];
      ink.stroke();
    });
    // Hold a full-resolution tracing on the plate; bound the number of moving pieces.
    const stride = Math.max(1, Math.ceil(groups.reduce((n, g) => n + g.length, 0) / 2000));
    return { plate, moving: groups.map(group => group.filter((_, i) => i % stride === 0)) };
  }

  function makeBench() {
    const sheet = surface(width, height), g = sheet.getContext("2d");
    const glow = g.createRadialGradient(540, 325, 30, 540, 325, 630);
    glow.addColorStop(0, "#163842"); glow.addColorStop(.65, "#0e242f"); glow.addColorStop(1, background);
    g.fillStyle = glow; g.fillRect(0, 0, width, height);
    g.fillStyle = "#3b5b60";
    for (let x = 28; x < width; x += 26) for (let y = 20; y < height; y += 26) g.fillRect(x, y, 1.25, 1.25);
    g.strokeStyle = "#476368"; g.lineWidth = 1; g.beginPath();
    for (let i = 0; i < 45; i++) {
      const x = 56 + i * 22, length = i % 4 === 0 ? 8 : 3;
      g.moveTo(x, 36); g.lineTo(x, 36 + length);
    }
    g.stroke();
    return sheet;
  }

  function drawGate(t, opacity, photo) {
    const acquire = ramp(t, .08, 1.85);
    const cx = 540 + (1 - acquire) * 74, cy = 337 - (1 - acquire) * 34;
    const w = 16 + 451 * acquire, h = 60 + 198 * ramp(t, .36, 1.66);
    const x = cx - w / 2, y = cy - h / 2;
    ctx.save();
    ctx.globalAlpha = opacity;
    ctx.save(); ctx.beginPath(); ctx.rect(x, y, w, h); ctx.clip();
    ctx.fillStyle = "#06202a"; ctx.fillRect(x, y, w, h);
    const zoom = 1.42 - .34 * acquire;
    ctx.drawImage(photo, cx - width * zoom * .5, cy - height * zoom * .48, width * zoom, height * zoom);
    const sweep = x + w * ramp(t, .4, 2.15);
    ctx.fillStyle = "rgba(10,35,44,.65)"; ctx.fillRect(sweep, y, x + w - sweep, h);
    ctx.fillStyle = "#dcffd9"; ctx.fillRect(sweep, y, 2, h);
    ctx.restore();
    ctx.strokeStyle = "#a6d9ce"; ctx.lineWidth = 1;
    ctx.strokeRect(x - 5, y - 5, w + 10, h + 10);
    ctx.strokeStyle = "#ffd39c"; ctx.lineWidth = 2.7; ctx.beginPath();
    for (let i = 0; i < 4; i++) {
      const sx = i % 2 ? -1 : 1, sy = i < 2 ? 1 : -1;
      const px = (sx > 0 ? x : x + w) - sx * 10, py = (sy > 0 ? y : y + h) - sy * 10;
      ctx.moveTo(px + sx * 20, py); ctx.lineTo(px, py); ctx.lineTo(px, py + sy * 13);
    }
    ctx.stroke();
    ctx.strokeStyle = "#d9f6d7"; ctx.lineWidth = 1; ctx.beginPath();
    ctx.moveTo(cx - 11, cy); ctx.lineTo(cx - 4, cy); ctx.moveTo(cx + 4, cy); ctx.lineTo(cx + 11, cy);
    ctx.moveTo(cx, cy - 11); ctx.lineTo(cx, cy - 4); ctx.moveTo(cx, cy + 4); ctx.lineTo(cx, cy + 11);
    ctx.stroke(); ctx.restore();
  }

  function assemble(t, drawing) {
    const progress = (t - 2.1) / 3.7;
    ctx.globalAlpha = ramp(t, 2.05, 2.75);
    ctx.lineWidth = 1.65;
    for (let group = 0; group < drawing.moving.length; group++) {
      ctx.beginPath();
      ctx.strokeStyle = inks[group];
      const pieces = drawing.moving[group];
      for (let i = 0; i < pieces.length; i++) {
        const s = pieces[i], mix = smooth((progress - s.delay) * 2.5);
        const trail = (1 - mix) * Math.min(1, progress * 4);
        const x = s.originX + (s.x - s.originX) * mix;
        const y = s.originY + (s.y - s.originY) * mix + Math.sin(mix * Math.PI) * s.bow;
        const theta = s.angle * (1 - mix);
        const cos = Math.cos(theta), sin = Math.sin(theta);
        const dx = (s.dx * cos - s.dy * sin) * (.5 + .5 * mix) + 8 * trail;
        const dy = (s.dx * sin + s.dy * cos) * (.5 + .5 * mix);
        ctx.moveTo(x - dx, y - dy); ctx.lineTo(x + dx, y + dy);
      }
      ctx.stroke();
    }
    ctx.globalAlpha = 1;
  }

  function colorWipe(t, photo) {
    const p = ramp(t, 6.45, 8.75);
    const x = -210 + p * (width + 420);
    ctx.save(); ctx.beginPath();
    ctx.moveTo(0, 0); ctx.lineTo(x + 180, 0); ctx.lineTo(x - 180, height); ctx.lineTo(0, height);
    ctx.closePath(); ctx.clip(); ctx.drawImage(photo, 0, 0); ctx.restore();
    ctx.strokeStyle = "#ffe3b3"; ctx.lineWidth = 2; ctx.beginPath();
    ctx.moveTo(x + 180, 0); ctx.lineTo(x - 180, height); ctx.stroke();
  }

  function shutter(t) {
    const p = ramp(t, 10.65, 11.66);
    const a = (height / 2 + 84) * p - 84, b = a - 54;
    ctx.fillStyle = background;
    ctx.beginPath(); ctx.moveTo(0, 0); ctx.lineTo(width, 0); ctx.lineTo(width, b); ctx.lineTo(0, a + 54); ctx.fill();
    ctx.beginPath(); ctx.moveTo(0, height); ctx.lineTo(width, height);
    ctx.lineTo(width, height - a - 54); ctx.lineTo(0, height - b); ctx.fill();
    if (t < 11.66) return;
    const span = width * (1 - ramp(t, 11.66, 11.98));
    ctx.strokeStyle = "#def8d8"; ctx.lineWidth = 1.5; ctx.beginPath();
    ctx.moveTo((width - span) / 2, height / 2 + 54 * span / width);
    ctx.lineTo((width + span) / 2, height / 2 - 54 * span / width); ctx.stroke();
  }

  async function play() {
    await art.decode();
    if (finished) return;
    if (!ctx) { finish("assetError"); return; }
    const photo = photoPlate(), drawing = makeDrawing(photo), bench = makeBench();
    const shade = ctx.createLinearGradient(0, height * .6, 0, height);
    shade.addColorStop(0, "rgba(5,15,22,0)"); shade.addColorStop(1, "rgba(5,15,22,.72)");
    const begin = performance.now();
    function render(now) {
      if (finished) return;
      const t = Math.max(0, (now - begin) / 1000);
      if (t < 8.75) {
        ctx.drawImage(bench, 0, 0);
        if (t < 3.4) drawGate(t, 1 - ramp(t, 2.35, 3.4), photo);
        if (t > 2.1) {
          if (t < 5.95) assemble(t, drawing);
          ctx.globalAlpha = ramp(t, 4.9, 5.95); ctx.drawImage(drawing.plate, 0, 0); ctx.globalAlpha = 1;
        }
        if (t > 6.45) colorWipe(t, photo);
      } else {
        const zoom = 1 + .035 * ramp(t, 8.75, 11.35);
        ctx.drawImage(photo, width * (1 - zoom) / 2, height * (1 - zoom) / 2, width * zoom, height * zoom);
      }
      ctx.fillStyle = shade; ctx.fillRect(0, 0, width, height);
      if (t > 10.65) shutter(t);
      if (t >= duration / 1000) finish("complete");
      else frameId = requestAnimationFrame(render);
    }
    document.body.classList.add("playing");
    frameId = requestAnimationFrame(render);
    // Backgrounded WebViews can pause animation frames during a desktop switch.
    timer = setTimeout(() => finish("complete"), duration + 75);
  }
  play().catch(() => finish("assetError"));
})();
