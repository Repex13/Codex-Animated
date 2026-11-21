(() => {
  'use strict';

  // ---------- helpers ----------
  const $ = (selector, root = document) => root.querySelector(selector);
  const clamp = (n, lo, hi) => Math.max(lo, Math.min(hi, n));
  const enc = encodeURIComponent;
  const glyph = code => { const i = document.createElement('i'); i.className = 'icon'; i.setAttribute('aria-hidden', 'true'); i.textContent = String.fromCodePoint(code); return i; };

  function h(tag, props, ...kids) {
    const el = document.createElement(tag);
    for (const key in props || {}) {
      const value = props[key];
      if (value == null || value === false) continue;
      if (key === 'class') el.className = value;
      else if (key === 'text') el.textContent = value;
      else if (key.startsWith('on')) el.addEventListener(key.slice(2), value);
      else if (value === true) el.setAttribute(key, '');
      else el.setAttribute(key, value);
    }
    for (const kid of kids.flat()) {
      if (kid == null || kid === false) continue;
      el.append(kid.nodeType ? kid : document.createTextNode(kid));
    }
    return el;
  }

  const hexToRgb = hex => { const m = /^#?([0-9a-f]{6})$/i.exec(hex || ''); return m ? [0, 2, 4].map(i => parseInt(m[1].substr(i, 2), 16)) : null; };
  function hue(hex) {
    const rgb = hexToRgb(hex); if (!rgb) return 0;
    const [r, g, b] = rgb.map(v => v / 255), max = Math.max(r, g, b), min = Math.min(r, g, b), d = max - min;
    if (!d) return 0;
    let v = max === r ? ((g - b) / d) % 6 : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
    v *= 60; return v < 0 ? v + 360 : v;
  }
  const hueGap = (a, b) => { const d = Math.abs(hue(a) - hue(b)); return Math.min(d, 360 - d); };
  function luminance(hex) {
    const rgb = hexToRgb(hex) || [0, 0, 0];
    const [r, g, b] = rgb.map(v => { v /= 255; return v <= .03928 ? v / 12.92 : Math.pow((v + .055) / 1.055, 2.4); });
    return .2126 * r + .7152 * g + .0722 * b;
  }
  const contrast = (a, b) => { const x = luminance(a), y = luminance(b); return (Math.max(x, y) + .05) / (Math.min(x, y) + .05); };

  // ---------- bridge ----------
  const host = window.chrome && window.chrome.webview;
  let nextId = 1;
  const pending = new Map();
  if (host) host.addEventListener('message', event => {
    const m = event.data;
    const waiting = m && pending.get(m.id);
    if (!waiting) return;
    pending.delete(m.id);
    if (m.ok) waiting.resolve(m.data); else waiting.reject(new Error(m.error || 'Something went wrong.'));
  });
  const call = (cmd, args) => new Promise((resolve, reject) => {
    if (!host) { reject(new Error('Theme Studio is not connected to its host.')); return; }
    const id = nextId++;
    pending.set(id, { resolve, reject });
    host.postMessage(Object.assign({ id, cmd }, args || {}));
  });

  const query = params => Object.keys(params).map(k => `${k}=${enc(params[k])}`).join('&');
  const imgUrl = window.__imgUrl || ((kind, params) => `img/${kind}?${query(params)}`);
  const splashUrl = window.__splashUrl || ((name, stamp, still) =>
    `splash/${enc(name)}/${enc(stamp)}/index.html?${still ? `still=${still}&` : ''}app=devin&t=${Date.now()}`);

  // ---------- state ----------
  const DEFAULT_ACCENT = '#D980A7';
  const PRESETS = [['Original', 0, 0, 0], ['Soft', 18, 30, 0], ['Ghost', 24, 52, 0], ['Aura', 13, 0, 100]];
  const TINTS = ['#8C66D1', '#B8475E', '#4C78D6', '#3C9C8F', '#C48A32', '#A8A8A8'];
  const ACCENTS = ['#D980A7', '#B49CFF', '#EF5B4C', '#E9B44C', '#7FD3E6', '#8FD694'];
  const S = { themes: [], apps: [], screen: { w: 2560, h: 1440 }, sel: null, view: 'edit', stage: 'app', confirming: false, still: 3.4, playing: false };
  let dirty = false, saving = false, saveAgain = false, saveTimer = 0, statusTimer = 0, splashTimer = 0;
  let ui = {};

  const cur = () => S.themes.find(t => t.name === S.sel) || null;
  const usedBy = name => S.apps.filter(a => a.theme === name);

  function parseHash() {
    const out = {};
    for (const part of location.hash.replace(/^#/, '').split('&')) {
      if (!part) continue;
      const i = part.indexOf('=');
      const key = decodeURIComponent(i < 0 ? part : part.slice(0, i));
      out[key] = i < 0 ? '' : decodeURIComponent(part.slice(i + 1));
    }
    return out;
  }

  function mergeState(state) {
    const local = cur();
    const keep = dirty && local ? { name: local.name, look: local.look, text: local.text } : null;
    S.themes = state.themes;
    S.apps = state.apps;
    S.screen = state.screen || S.screen;
    if (keep) { const t = S.themes.find(x => x.name === keep.name); if (t) { t.look = keep.look; t.text = keep.text; } }
    if (!S.themes.find(t => t.name === S.sel)) S.sel = defaultSelection();
  }

  function defaultSelection() {
    if (!S.themes.length) return null;
    let best = null, count = -1;
    for (const t of S.themes) { const n = usedBy(t.name).length; if (n > count) { best = t; count = n; } }
    return best.name;
  }

  function setStatus(text, error) {
    clearTimeout(statusTimer);
    if (ui.status) { ui.status.textContent = text || ''; ui.status.classList.toggle('error', !!error); }
    if (text && !error && ui.status) statusTimer = setTimeout(() => { if (ui.status) ui.status.textContent = ''; }, 1600);
  }

  // ---------- accent ----------
  function applyAccent() {
    const t = cur();
    const accent = t && /^#[0-9a-f]{6}$/i.test(t.text.accent) ? t.text.accent : DEFAULT_ACCENT;
    const root = document.documentElement.style;
    root.setProperty('--accent', accent);
    root.setProperty('--on-accent', contrast(accent, '#121318') >= contrast(accent, '#ffffff') ? '#121318' : '#ffffff');
  }

  // ---------- controls ----------
  function button(label, kind, onClick, iconCode) {
    return h('button', { type: 'button', class: `btn ${kind}`, onclick: onClick }, iconCode ? glyph(iconCode) : null, label);
  }

  function segmented(items, selected, onPick, fill) {
    const root = h('div', { class: `seg${fill ? ' fill' : ''}`, role: 'group' });
    const buttons = items.map(([value, label]) => h('button', { type: 'button', 'aria-pressed': String(value === selected), 'data-value': value, text: label }));
    const paint = value => buttons.forEach(b => b.setAttribute('aria-pressed', String(b.dataset.value === String(value))));
    buttons.forEach((b, i) => {
      b.addEventListener('click', () => onPick(b.dataset.value));
      b.addEventListener('keydown', e => {
        if (e.key !== 'ArrowRight' && e.key !== 'ArrowLeft') return;
        const next = buttons[clamp(i + (e.key === 'ArrowRight' ? 1 : -1), 0, buttons.length - 1)];
        next.focus(); next.click(); e.preventDefault();
      });
      root.append(b);
    });
    root.paint = paint;
    return root;
  }

  function toggle(on, label, onChange) {
    const el = h('button', { type: 'button', class: 'switch', role: 'switch', 'aria-checked': String(!!on), 'aria-label': label });
    el.addEventListener('click', () => { const next = el.getAttribute('aria-checked') !== 'true'; el.setAttribute('aria-checked', String(next)); onChange(next); });
    return el;
  }

  function slider(label, min, max, value, format, onInput) {
    const valueEl = h('span', { class: 'value', text: format(value) });
    const input = h('input', { class: 'slider', type: 'range', min, max, step: 1, 'aria-label': label });
    const fill = () => input.style.setProperty('--fill', `${(input.value - min) / (max - min) * 100}%`);
    input.value = value; fill();
    input.addEventListener('input', () => { fill(); valueEl.textContent = format(+input.value); onInput(+input.value); });
    const row = h('div', { class: 'srow' }, h('div', { class: 'head' }, h('span', { class: 'label', text: label }), valueEl), input);
    row.set = v => { input.value = v; fill(); valueEl.textContent = format(v); };
    return row;
  }

  // ---------- rail ----------
  function renderRail() {
    const list = ui.list;
    const focused = document.activeElement && document.activeElement.closest && document.activeElement.closest('.row') ? document.activeElement.dataset.name : null;
    list.textContent = '';
    for (const t of S.themes) {
      const using = usedBy(t.name);
      const thumbImg = h('img', { alt: '', src: imgUrl('wallpaper', { theme: t.name, w: 160, v: t.stamp }) });
      const thumb = h('span', { class: `thumb${t.broken ? ' broken' : ''}` }, thumbImg);
      thumbImg.addEventListener('error', () => thumb.classList.add('broken'));
      const row = h('button', {
        type: 'button', role: 'option', class: `row${t.name === S.sel && S.view === 'edit' ? ' sel' : ''}`,
        'aria-selected': String(t.name === S.sel && S.view === 'edit'), 'data-name': t.name,
        tabindex: t.name === S.sel ? '0' : '-1', onclick: () => selectTheme(t.name)
      }, thumb, h('span', { class: 'row-text' }, h('div', { class: 'row-name', text: t.name }),
        using.length ? h('div', { class: 'row-use', text: `In use: ${using.map(a => a.name).join(', ')}` }) : null));
      row.addEventListener('keydown', e => {
        if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return;
        const i = S.themes.findIndex(x => x.name === t.name);
        const next = S.themes[clamp(i + (e.key === 'ArrowDown' ? 1 : -1), 0, S.themes.length - 1)];
        if (next && next.name !== t.name) selectTheme(next.name, true);
        e.preventDefault();
      });
      list.append(row);
    }
    if (focused) { const row = [...list.children].find(r => r.dataset.name === focused); if (row) row.focus(); }
  }

  function selectTheme(name, keepFocus) {
    if (S.sel === name && S.view === 'edit') return;
    flushSave();
    S.sel = name; S.view = 'edit'; S.confirming = false; S.playing = false;
    buildAll();
    if (keepFocus) { const row = [...ui.list.children].find(r => r.dataset.name === name); if (row) row.focus(); }
  }

  // ---------- canvas ----------
  function renderCanvasHead(t) {
    const error = h('div', { class: 'name-error' });
    const field = h('input', { class: 'name-field', value: t.name, maxlength: 60, 'aria-label': 'Theme name', spellcheck: 'false' });
    let reverting = false;
    const commit = () => {
      if (reverting) return;
      const next = field.value.trim();
      if (next === t.name) { error.textContent = ''; return; }
      call('renameTheme', { name: t.name, newName: next }).then(state => {
        mergeState(state); S.sel = state.renamed; buildAll(); setStatus('Renamed');
      }).catch(e => { error.textContent = `Couldn't rename. ${e.message}`; field.value = t.name; });
    };
    field.addEventListener('keydown', e => {
      if (e.key === 'Enter') { field.blur(); }
      else if (e.key === 'Escape') { reverting = true; field.value = t.name; error.textContent = ''; field.blur(); reverting = false; }
    });
    field.addEventListener('blur', commit);
    const chips = S.apps.map(app => {
      const on = app.theme === t.name;
      const chip = h('button', { type: 'button', class: `chip${on ? ' on' : ''}`, 'aria-pressed': String(on), onclick: () => applyTo(app.id, app.name) },
        on ? glyph(0xE73E) : null, app.name);
      return chip;
    });
    return h('div', { class: 'cv-head' }, h('div', { class: 'name-wrap' }, field, error),
      h('div', { class: 'use-on' }, h('span', { class: 'label', text: 'Use on' }), chips, button('All apps', 'quiet', () => applyTo('all', 'all apps'))));
  }

  function applyTo(appId, label) {
    const t = cur(); if (!t) return;
    flushSave().then(() => call('applyTheme', { name: t.name, app: appId })).then(apps => {
      S.apps = apps; renderRail(); refreshChips();
      setStatus(appId === 'all' ? 'Applied to all apps' : `Applied to ${label}`);
    }).catch(e => setStatus(`Couldn't apply. ${e.message}`, true));
  }

  function refreshChips() {
    const t = cur(); if (!t || !ui.head) return;
    const fresh = renderCanvasHead(t);
    const field = $('.name-field', ui.head);
    const editing = document.activeElement === field;
    if (editing) { const useOn = $('.use-on', fresh); $('.use-on', ui.head).replaceWith(useOn); }
    else ui.head.replaceWith(fresh);
    if (!editing) ui.head = fresh;
  }

  function buildCanvas(t) {
    const stageSwitch = segmented([['app', 'In the app'], ['splash', 'Startup animation']], S.stage, v => setStage(v, true));
    ui.stageSwitch = stageSwitch;
    ui.head = renderCanvasHead(t);
    ui.status = h('div', { class: 'status', role: 'status', 'aria-live': 'polite' });
    ui.help = h('p', { class: 'cv-help', text: 'Drag to choose what stays in view. Text is enlarged here; apps using this theme update as you edit.' });
    ui.foot = h('div', { class: 'cv-foot' });
    buildStage(t);
    renderFoot();
    return h('main', { class: 'canvas' }, ui.head, h('div', { class: 'cv-tools' }, stageSwitch), ui.stageWrap, ui.help, ui.foot);
  }

  function renderFoot() {
    const t = cur();
    ui.foot.textContent = '';
    ui.foot.append(ui.status);
    if (S.confirming) {
      ui.foot.append(h('div', { class: 'confirm' }, 'Delete this theme? It goes to the Recycle Bin.',
        button('Delete', 'secondary danger', () => deleteTheme(t.name)), button('Keep', 'quiet', () => { S.confirming = false; renderFoot(); })));
    } else ui.foot.append(button('Delete theme', 'quiet danger', () => { S.confirming = true; renderFoot(); }, 0xE74D));
  }

  function deleteTheme(name) {
    const index = S.themes.findIndex(t => t.name === name);
    S.confirming = false;
    call('deleteTheme', { name }).then(state => {
      dirty = false; mergeState(state);
      S.sel = state.themes.length ? state.themes[Math.min(index, state.themes.length - 1)].name : null;
      buildAll(); setStatus('Deleted');
    }).catch(e => { renderFoot(); setStatus(`Couldn't delete. ${e.message}`, true); });
  }

  // ---------- stage ----------
  const MOCK_W = 1600;
  const CODE = [
    '<k>using</k> System;', '<k>using</k> System.Drawing;', '', '<k>internal static class</k> <t>ArtworkLook</t>', '{',
    '    <c>// Resolution-independent blur, then tint and dim.</c>',
    '    <k>internal static</k> <t>Bitmap</t> <m>Render</m>(<t>Bitmap</t> src, <t>Look</t> look)', '    {',
    '        <k>double</k> sigma = <m>Sigma</m>(look.Blur, Math.<m>Max</m>(src.Width, src.Height));',
    '        <k>bool</k> blur = sigma >= <n>0.3</n>;', '        <k>bool</k> color = look.Tint > <n>0</n> || look.Dim > <n>0</n>;',
    '        <k>if</k> (!blur &amp;&amp; !color) <k>return</k> src;', '        <k>return</k> <m>Colorize</m>(blur ? <m>Blur</m>(src, sigma) : src, look);', '    }', '}'
  ];
  const FILES = ['AppTarget.cs', 'ArtworkLook.cs', 'ArtworkOverlay.cs', 'ImageCodec.cs', 'Launcher.cs', 'SplashPage.cs', 'ThemeSettings.cs', 'ThemeStudio.cs'];

  function mockEditor() {
    const mock = h('div', { class: 'mock', 'aria-hidden': 'true' });
    mock.innerHTML =
      '<div class="activity"><i></i><i></i><i></i><i></i></div>' +
      '<div class="side"><div class="t">Explorer</div>' + FILES.map(f => `<div class="f${f === 'ArtworkLook.cs' ? ' on' : ''}">${f}</div>`).join('') + '</div>' +
      '<div class="tabs"><span class="tab">ArtworkLook.cs</span></div>' +
      '<div class="code">' + CODE.map((line, i) => `<span class="ln">${i + 1}</span>${line.replace(/<(k|t|c|n|s|m)>(.*?)<\/\1>/g, '<span class="$1">$2</span>')}`).join('\n') + '</div>' +
      '<div class="status-bar">main    0 problems    Ln 8, Col 5    UTF-8    C#</div>';
    return mock;
  }

  function buildStage(t) {
    ui.mock = mockEditor();
    ui.art = h('img', { class: 'art', alt: '' });
    ui.frame = null;
    ui.stage = h('div', { class: 'stage', tabindex: '0', 'aria-label': 'Preview. Drag or use the arrow keys to choose what stays in view.' }, ui.mock, ui.art);
    ui.stageWrap = h('div', { class: 'stage-wrap' }, ui.stage);
    wireDrag();
    ui.shown = ''; ui.inflight = false;
    updateArt(t, true);
    applyStageMode();
    if (window.ResizeObserver) { if (ui.ro) ui.ro.disconnect(); ui.ro = new ResizeObserver(layoutStage); ui.ro.observe(ui.stageWrap); }
  }

  function lookParams(t) { return { theme: t.name, blur: t.look.blur, dim: t.look.dim, tint: t.look.tint, tintColor: t.look.tintColor, v: t.stamp }; }

  function updateArt(t, initial) {
    ui.art.style.opacity = String(t.look.opacity / 100);
    ui.art.style.objectPosition = `${t.look.focusX}% ${t.look.focusY}%`;
    const wanted = imgUrl('look', lookParams(t));
    ui.wanted = wanted;
    if (initial) { ui.shown = ''; }
    if (wanted === ui.shown) return;
    if (ui.inflight) return;
    loadLook();
  }

  function loadLook() {
    const url = ui.wanted;
    ui.inflight = true;
    const probe = new Image();
    const done = ok => {
      ui.inflight = false;
      ui.shown = url;
      if (ok) ui.art.src = url;
      ui.art.hidden = S.stage === 'splash' || !ok;
      layoutStage();
      if (ui.wanted !== ui.shown) loadLook(); else ui.artLoaded && ui.artLoaded();
    };
    probe.onload = () => done(true);
    probe.onerror = () => done(false);
    probe.src = url;
  }

  function layoutStage() {
    const wrap = ui.stageWrap; if (!wrap || !wrap.isConnected) return;
    const box = wrap.getBoundingClientRect();
    const aspect = S.stage === 'splash' ? 3 / 2 : S.screen.w / S.screen.h;
    const width = Math.max(100, Math.min(box.width, box.height * aspect));
    const height = width / aspect;
    ui.stage.style.width = `${Math.round(width)}px`;
    ui.stage.style.height = `${Math.round(height)}px`;
    const mockH = Math.round(MOCK_W * S.screen.h / S.screen.w);
    ui.mock.style.height = `${mockH}px`;
    ui.mock.style.transform = `scale(${width / MOCK_W})`;
    if (ui.frame) ui.frame.style.transform = `scale(${width / 1000})`;
    if (ui.placeholder) ui.placeholder.style.transform = `scale(${width / 1000})`;
    const draggable = S.stage === 'app' && overflowOf().x + overflowOf().y > 1;
    ui.stage.classList.toggle('draggable', draggable);
  }

  function overflowOf() {
    const w = ui.stage.clientWidth, hgt = ui.stage.clientHeight, nw = ui.art.naturalWidth, nh = ui.art.naturalHeight;
    if (!nw || !nh || !w) return { x: 0, y: 0 };
    const scale = Math.max(w / nw, hgt / nh);
    return { x: Math.max(0, nw * scale - w), y: Math.max(0, nh * scale - hgt) };
  }

  function wireDrag() {
    let drag = null;
    ui.stage.addEventListener('pointerdown', e => {
      if (S.stage !== 'app' || e.button !== 0) return;
      const o = overflowOf(); if (o.x + o.y <= 1) return;
      const t = cur();
      drag = { x: e.clientX, y: e.clientY, fx: t.look.focusX, fy: t.look.focusY, o };
      ui.stage.setPointerCapture(e.pointerId); ui.stage.classList.add('dragging');
    });
    ui.stage.addEventListener('pointermove', e => {
      if (!drag) return;
      const t = cur();
      if (drag.o.x > 1) t.look.focusX = clamp(Math.round(drag.fx - (e.clientX - drag.x) / drag.o.x * 100), 0, 100);
      if (drag.o.y > 1) t.look.focusY = clamp(Math.round(drag.fy - (e.clientY - drag.y) / drag.o.y * 100), 0, 100);
      onEdit('look');
    });
    const end = () => { if (drag) { drag = null; ui.stage.classList.remove('dragging'); } };
    ui.stage.addEventListener('pointerup', end);
    ui.stage.addEventListener('pointercancel', end);
    ui.stage.addEventListener('keydown', e => {
      const step = { ArrowLeft: [-2, 0], ArrowRight: [2, 0], ArrowUp: [0, -2], ArrowDown: [0, 2] }[e.key];
      if (!step || S.stage !== 'app') return;
      const o = overflowOf(), t = cur();
      if (o.x > 1) t.look.focusX = clamp(t.look.focusX + step[0], 0, 100);
      if (o.y > 1) t.look.focusY = clamp(t.look.focusY + step[1], 0, 100);
      onEdit('look'); e.preventDefault();
    });
  }

  function setStage(stage, user) {
    if (S.stage === stage && !S.playing) return;
    if (stage !== 'splash') S.playing = false;
    S.stage = stage;
    if (ui.stageSwitch) ui.stageSwitch.paint(stage);
    applyStageMode();
  }

  function setStill(seconds) {
    if (S.still === seconds) return;
    S.still = seconds;
    if (S.stage === 'splash' && !S.playing) reloadFrame();
  }

  function applyStageMode() {
    const splash = S.stage === 'splash';
    ui.mock.hidden = splash;
    ui.art.hidden = splash || !ui.art.getAttribute('src');
    ui.help.style.visibility = splash ? 'hidden' : 'visible';
    if (splash) {
      if (!ui.frame && !ui.placeholder) {
        const url = splashUrl(cur().name, cur().stamp, S.still);
        if (url) {
          ui.frame = h('iframe', { class: 'splash-frame still', title: 'Startup animation preview', tabindex: '-1' });
          ui.stage.append(ui.frame);
          ui.frame.src = url;
          ui.frame.addEventListener('load', () => ui.frameLoaded && ui.frameLoaded());
        } else {
          ui.placeholder = h('div', { class: 'splash-placeholder', text: `Startup animation preview: ${cur().name}` });
          ui.stage.append(ui.placeholder);
        }
      } else reloadFrame();
    }
    if (ui.frame) ui.frame.hidden = !splash;
    if (ui.placeholder) ui.placeholder.hidden = !splash;
    layoutStage();
  }

  function reloadFrame() {
    const t = cur(); if (!t || !ui.frame) return;
    ui.frame.classList.toggle('still', !S.playing);
    ui.frame.src = splashUrl(t.name, t.stamp, S.playing ? 0 : S.still);
  }

  function scheduleFrame() {
    clearTimeout(splashTimer);
    if (!ui.frame) return;
    splashTimer = setTimeout(() => { if (S.stage === 'splash' && !S.playing) reloadFrame(); }, 300);
  }

  function play() {
    const t = cur(); if (!t) return;
    setStage('splash'); S.playing = true;
    if (ui.frame) reloadFrame();
  }

  window.addEventListener('message', event => {
    if (event.origin !== location.origin || !event.data || !event.data.splash) return;
    if (!S.playing) return;
    S.playing = false;
    if (ui.frame) reloadFrame();
  });

  // ---------- editing and saving ----------
  function onEdit(kind) {
    const t = cur(); if (!t) return;
    dirty = true;
    updateArt(t);
    if (kind === 'look') refreshPresets();
    if (ui.centerBtn) ui.centerBtn.hidden = t.look.focusX === 50 && t.look.focusY === 50;
    if (ui.tintGroup) ui.tintGroup.hidden = t.look.tint <= 0;
    clearTimeout(saveTimer);
    saveTimer = setTimeout(doSave, 200);
  }

  function flushSave() {
    clearTimeout(saveTimer);
    if (dirty && !saving) return doSave();
    return Promise.resolve();
  }

  function doSave() {
    const t = cur(); if (!t) return Promise.resolve();
    if (saving) { saveAgain = true; return Promise.resolve(); }
    saving = true;
    const name = t.name, look = Object.assign({}, t.look), text = Object.assign({}, t.text);
    dirty = false;
    return call('saveTheme', { name, look, text }).then(result => {
      const live = S.themes.find(x => x.name === name);
      if (live) {
        if (result.text.accent && result.text.accent !== live.text.accent) { live.text.accent = result.text.accent; if (cur() === live) { applyAccent(); refreshAccent(); } }
      }
      setStatus('Saved');
      scheduleFrame();
    }).catch(e => { dirty = true; setStatus(`Couldn't save. ${e.message} It will retry with your next change.`, true); })
      .then(() => { saving = false; if (saveAgain) { saveAgain = false; return doSave(); } });
  }

  // ---------- inspector ----------
  function refreshPresets() {
    const t = cur();
    const match = PRESETS.find(p => p[1] === t.look.blur && p[2] === t.look.dim && p[3] === t.look.tint);
    if (ui.presets) ui.presets.paint(match ? match[0] : '');
  }

  function refreshAccent() {
    if (!ui.accentBox) return;
    const fresh = accentField(cur());
    ui.accentBox.replaceWith(fresh);
    ui.accentBox = fresh;
  }

  function accentField(t) {
    const shown = [];
    for (const hex of [...(t.accents || []), ...ACCENTS]) {
      if (!shown.some(s => s.toLowerCase() === hex.toLowerCase() || hueGap(s, hex) < 20)) shown.push(hex);
    }
    const current = (t.text.accent || '').toUpperCase();
    const inList = shown.some(s => s.toUpperCase() === current);
    const list = h('div', { class: 'swatches' });
    const pick = hex => { t.text.accent = hex; applyAccent(); onEdit('text'); refreshAccent(); };
    for (const hex of shown)
      list.append(h('button', { type: 'button', class: 'swatch', style: `background:${hex}`, 'aria-label': `Accent ${hex}`, 'aria-pressed': String(hex.toUpperCase() === current), onclick: () => pick(hex) }));
    const custom = h('input', { type: 'color', class: 'swatch', 'aria-label': 'Custom accent', 'aria-pressed': String(!inList && !!current), value: /^#[0-9a-f]{6}$/i.test(current) ? current.toLowerCase() : '#d980a7' });
    custom.addEventListener('input', () => { t.text.accent = custom.value.toUpperCase(); applyAccent(); onEdit('text'); });
    custom.addEventListener('change', () => refreshAccent());
    list.append(custom);
    return h('div', { class: 'field' }, h('span', { class: 'label', text: 'Accent' }), list);
  }

  function tintField(t) {
    const list = h('div', { class: 'swatches' });
    const current = (t.look.tintColor || '').toUpperCase();
    const inList = TINTS.some(c => c.toUpperCase() === current);
    const pick = hex => { t.look.tintColor = hex.toUpperCase(); onEdit('look'); refreshTints(); };
    for (const hex of TINTS)
      list.append(h('button', { type: 'button', class: 'swatch', style: `background:${hex}`, 'aria-label': `Tint color ${hex}`, 'aria-pressed': String(hex.toUpperCase() === current), onclick: () => pick(hex) }));
    const custom = h('input', { type: 'color', class: 'swatch', 'aria-label': 'Custom tint color', 'aria-pressed': String(!inList), value: current.toLowerCase() });
    custom.addEventListener('input', () => { t.look.tintColor = custom.value.toUpperCase(); onEdit('look'); });
    custom.addEventListener('change', () => refreshTints());
    list.append(custom);
    return h('div', { class: 'field' }, h('span', { class: 'label', text: 'Tint color' }), list);
  }

  function refreshTints() {
    const fresh = tintField(cur());
    ui.tintGroup.replaceWith(fresh); ui.tintGroup = fresh; fresh.hidden = cur().look.tint <= 0;
  }

  function buildInspector(t) {
    const look = t.look;
    ui.presets = segmented(PRESETS.map(p => [p[0], p[0]]), '', name => {
      const p = PRESETS.find(x => x[0] === name);
      look.blur = p[1]; look.dim = p[2]; look.tint = p[3];
      sBlur.set(look.blur); sDim.set(look.dim); sTint.set(look.tint);
      onEdit('look');
    }, true);
    const sOpacity = slider('Visibility', 4, 40, look.opacity, v => `${v}%`, v => { look.opacity = v; onEdit('look'); });
    const sBlur = slider('Blur', 0, 100, look.blur, v => String(v), v => { look.blur = v; onEdit('look'); });
    const sDim = slider('Dim', 0, 80, look.dim, v => String(v), v => { look.dim = v; onEdit('look'); });
    const sTint = slider('Tint', 0, 100, look.tint, v => String(v), v => { look.tint = v; onEdit('look'); });
    ui.tintGroup = tintField(t); ui.tintGroup.hidden = look.tint <= 0;
    ui.centerBtn = button('Center image', 'quiet', () => { look.focusX = 50; look.focusY = 50; onEdit('look'); }); ui.centerBtn.hidden = look.focusX === 50 && look.focusY === 50;
    const wallpaper = h('section', { class: 'group', id: 'g-wallpaper' }, h('h3', { text: 'Wallpaper' }),
      h('div', { class: 'field' }, ui.presets), sOpacity, sBlur, sDim, sTint, ui.tintGroup, ui.centerBtn,
      h('div', { class: 'field' }, button('Replace image…', 'secondary', () => replaceImage('replaceWallpaper'), 0xE8B9)));
    wallpaper.addEventListener('focusin', () => setStage('app'));

    const title = h('input', { class: 'input', maxlength: 40, placeholder: t.name.toUpperCase(), value: t.text.title, 'aria-label': 'Title' });
    title.addEventListener('input', () => { t.text.title = title.value; onEdit('text'); });
    const subtitle = h('input', { class: 'input', maxlength: 60, placeholder: 'Optional', value: t.text.subtitle, 'aria-label': 'Subtitle' });
    subtitle.addEventListener('input', () => { t.text.subtitle = subtitle.value; onEdit('text'); });
    subtitle.addEventListener('focus', () => setStill(9.5));
    subtitle.addEventListener('blur', () => setStill(3.4));
    ui.accentBox = accentField(t);
    const avatarThumb = t.hasAvatar ? h('img', { alt: '', src: imgUrl('avatar', { theme: t.name, v: t.stamp }) }) : null;
    const avatar = h('div', { class: 'field' }, h('span', { class: 'label', text: 'Avatar' }),
      h('div', { class: 'avatar-row' }, h('span', { class: 'avatar-slot' }, avatarThumb), button('Change…', 'secondary', () => replaceImage('setAvatar')),
        t.hasAvatar ? button('Remove', 'quiet', () => replaceImage('clearAvatar')) : null),
      h('p', { class: 'hint', text: 'Shown in the scanner when the animation starts.' }));
    const logs = h('textarea', { class: 'input', rows: 4, 'aria-label': 'Boot lines', spellcheck: 'false' });
    logs.value = (t.text.logs || []).join('\n');
    logs.addEventListener('input', () => { t.text.logs = logs.value.split('\n').map(x => x.trim()).filter(Boolean).slice(0, 4); onEdit('text'); });
    const boot = h('details', { class: 'field' }, h('summary', { text: 'Boot lines' }), logs,
      h('p', { class: 'hint', text: 'Up to four lines shown while the app starts. Leave empty for the standard ones.' }));
    const startup = h('section', { class: 'group', id: 'g-startup' }, h('h3', { text: 'Startup animation' }),
      h('div', { class: 'field' }, h('span', { class: 'label', text: 'Title' }), title),
      h('div', { class: 'field' }, h('span', { class: 'label', text: 'Subtitle' }), subtitle),
      ui.accentBox, avatar, boot,
      h('div', { class: 'play-row' }, button('Play', 'primary', play, 0xE768), button('Play full size', 'quiet', playFull, 0xE740)));
    startup.addEventListener('focusin', () => setStage('splash'));
    refreshPresets();
    return h('aside', { class: 'inspector' }, wallpaper, startup);
  }

  function replaceImage(cmd) {
    const t = cur(); if (!t) return;
    flushSave().then(() => call(cmd, { name: t.name })).then(state => {
      if (state.cancelled) return;
      mergeState(state); buildAll(); setStatus(cmd === 'replaceWallpaper' ? 'Image replaced' : 'Saved');
    }).catch(e => setStatus(`Couldn't change the image. ${e.message}`, true));
  }

  function playFull() {
    const t = cur(); if (!t) return;
    flushSave().then(() => call('playSplash', { name: t.name, app: 'devin' })).catch(e => setStatus(`Couldn't play the animation. ${e.message}`, true));
  }

  // ---------- apps view ----------
  function buildApps() {
    const wrap = h('main', { class: 'apps' }, h('h1', { text: 'Apps and startup' }),
      h('p', { class: 'lead', text: 'The helper draws the wallpaper inside the app and plays the animation when the app starts.' }));
    for (const app of S.apps) {
      const state = !app.helperExists ? h('span', { class: 'app-state', text: 'Helper not installed' })
        : app.helperRunning ? h('span', { class: 'app-state', text: 'Helper running' })
          : h('span', { class: 'app-state', text: 'Helper not running' });
      const line1 = h('div', { class: 'app-line1' }, h('span', { class: 'app-name', text: app.name }), state,
        app.helperExists && !app.helperRunning ? button('Start helper', 'secondary', () => startHelper(app.id)) : null);
      const select = h('select', { class: 'select', 'aria-label': `${app.name} theme` });
      select.append(h('option', { value: '', text: 'Custom' }));
      for (const t of S.themes) select.append(h('option', { value: t.name, text: t.name }));
      select.value = app.theme || '';
      select.addEventListener('change', () => {
        if (!select.value) { select.value = app.theme || ''; return; }
        call('applyTheme', { name: select.value, app: app.id }).then(apps => { S.apps = apps; setAppsStatus('Applied'); }).catch(e => setAppsStatus(`Couldn't apply. ${e.message}`, true));
      });
      const splash = toggle(app.splash, `Play animation on start for ${app.name}`, on =>
        call('setApp', { app: app.id, splash: on }).then(apps => { S.apps = apps; }).catch(e => setAppsStatus(`Couldn't save. ${e.message}`, true)));
      const speed = segmented([['1', 'Full'], ['2', 'Quick']], String(app.speed), v =>
        call('setApp', { app: app.id, speed: +v }).then(apps => { S.apps = apps; speed.paint(v); }).catch(e => setAppsStatus(`Couldn't save. ${e.message}`, true)));
      const auto = toggle(app.autostart, `Start helper with Windows for ${app.name}`, on =>
        call('setAutostart', { app: app.id, on }).then(apps => { S.apps = apps; }).catch(e => setAppsStatus(`Couldn't save. ${e.message}`, true)));
      const ctl = (label, el) => h('div', { class: 'ctl' }, h('span', { class: 'label', text: label }), el);
      wrap.append(h('section', { class: 'app-block', 'data-app': app.id }, line1,
        h('div', { class: 'app-controls' }, ctl('Theme', select), ctl('Play animation on start', splash), ctl('Speed', speed), ctl('Start helper with Windows', auto))));
    }
    ui.appsStatus = h('div', { class: 'status', role: 'status', 'aria-live': 'polite' });
    wrap.append(ui.appsStatus);
    return wrap;
  }

  function setAppsStatus(text, error) { if (ui.appsStatus) { ui.appsStatus.textContent = text; ui.appsStatus.classList.toggle('error', !!error); } }

  function startHelper(id) {
    call('startHelper', { app: id }).then(apps => { S.apps = apps; buildAll(); }).catch(e => setAppsStatus(`Couldn't start the helper. ${e.message}`, true));
  }

  // ---------- empty ----------
  function buildEmpty() {
    return h('main', { class: 'empty' }, h('h1', { text: 'Make your first theme' }),
      h('p', { text: 'Pick a wallpaper image. You can tune how it looks behind your editor and give it a title for the startup animation.' }),
      button('Choose image…', 'primary', createTheme, 0xE710));
  }

  function createTheme() {
    flushSave().then(() => call('createTheme', window.__studio && window.__studio.testPath ? { path: window.__studio.testPath } : undefined)).then(state => {
      if (state.cancelled) return;
      mergeState(state); S.sel = state.created; S.view = 'edit'; buildAll(); setStatus('Theme created');
    }).catch(e => setStatus(`Couldn't create the theme. ${e.message}`, true));
  }

  // ---------- build ----------
  function buildAll() {
    const app = $('#app');
    app.textContent = '';
    const t = cur();
    const empty = !S.themes.length;
    app.className = empty ? 'empty-view' : S.view === 'apps' ? 'apps-view' : '';
    applyAccent();
    ui = { stage: null };
    ui.list = h('div', { class: 'rail-list', role: 'listbox', 'aria-label': 'Themes' });
    const appsBtn = button('Apps and startup', 'quiet', () => { flushSave(); S.view = 'apps'; buildAll(); }, 0xE713);
    appsBtn.setAttribute('aria-current', String(S.view === 'apps'));
    app.append(h('nav', { class: 'rail' },
      h('div', { class: 'rail-head' }, h('h2', { text: 'Themes' }), button('New theme', 'secondary', createTheme, 0xE710)),
      ui.list, h('div', { class: 'rail-foot' }, appsBtn)));
    renderRail();
    if (empty) { app.append(buildEmpty()); markReady(); return; }
    if (S.view === 'apps') { app.append(buildApps()); markReady(); return; }
    app.append(buildCanvas(t), buildInspector(t));
    applyStageMode();
    layoutStage();
    markReady();
  }

  function markReady() {
    const root = document.documentElement;
    delete root.dataset.ready;
    const imgs = [...document.querySelectorAll('.thumb img, .stage .art, .avatar-slot img')].filter(i => i.getAttribute('src'));
    const loaded = imgs.map(img => img.complete ? Promise.resolve() : new Promise(r => { img.addEventListener('load', r, { once: true }); img.addEventListener('error', r, { once: true }); }));
    const waits = [Promise.all(loaded)];
    if (ui.stage && !ui.art.getAttribute('src') && !ui.art.hidden) waits.push(new Promise(r => { ui.artLoaded = r; }));
    if (S.stage === 'splash' && ui.frame) waits.push(new Promise(r => { ui.frameLoaded = () => setTimeout(r, 900); }));
    Promise.race([Promise.all(waits), new Promise(r => setTimeout(r, 12000))]).then(() => requestAnimationFrame(() => requestAnimationFrame(() => { root.dataset.ready = '1'; })));
  }

  // ---------- refresh on focus ----------
  window.addEventListener('focus', () => {
    call('state').then(state => {
      const editing = document.activeElement && document.activeElement.closest && document.activeElement.closest('.inspector, .name-field');
      mergeState(state);
      if (!ui.list) return;
      if (S.view === 'apps' && !editing) { buildAll(); return; }
      renderRail();
      if (S.view === 'edit') refreshChips();
    }).catch(() => { });
  });

  // ---------- start ----------
  function start() {
    call('state').then(state => {
      mergeState(state);
      const hash = parseHash();
      if (hash.empty === '1') { S.themes = []; S.sel = null; }
      S.sel = hash.theme && S.themes.find(t => t.name === hash.theme) ? hash.theme : defaultSelection();
      if (hash.view === 'apps') S.view = 'apps';
      if (hash.stage === 'splash') S.stage = 'splash';
      buildAll();
    }).catch(e => { $('#app').textContent = `Theme Studio couldn't load its themes. ${e.message}`; });
  }
  window.__studio = { call, state: S, select: selectTheme, edit: onEdit, flush: flushSave, play, setStage };
  start();
})();
