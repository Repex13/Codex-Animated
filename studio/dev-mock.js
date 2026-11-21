// Design harness only: stands in for the WebView2 host so studio.js can run in a plain browser.
(function () {
  'use strict';
  if (window.chrome && window.chrome.webview) return;
  const empty = /(^|[#&])empty=1/.test(location.hash);
  const base = { opacity: 15, blur: 0, dim: 0, tint: 0, tintColor: '#8C66D1', focusX: 50, focusY: 50 };
  const make = (name, look, text, accents, hasAvatar) => ({
    name, look: Object.assign({}, base, look), hasAvatar: hasAvatar !== false, stamp: '1', broken: false,
    text: Object.assign({ title: name.toUpperCase(), subtitle: '', accent: accents[0], logs: [] }, text), accents
  });
  let themes = empty ? [] : [
    make('A Silent Voice', { opacity: 18 }, { subtitle: '聲の形', accent: '#F4A6C6', logs: ['KOE NO KATACHI · THE SHAPE OF VOICE', 'sign language loaded ......... ok'] }, ['#F4A6C6', '#7FD3E6', '#E9B44C']),
    make('Death Note', { opacity: 18, blur: 24, dim: 52 }, { subtitle: '計画通り', accent: '#EF5B4C' }, ['#EF5B4C', '#E9B44C']),
    make('Fullmetal Alchemist', {}, { subtitle: '等価交換', accent: '#E9B44C' }, ['#E9B44C', '#8FD694', '#7FD3E6']),
    make('Hunter x Hunter', { blur: 13, tint: 100 }, { title: 'HUNTER × HUNTER', subtitle: '天空闘技場・受付中', accent: '#B49CFF' }, []),
    make('Phantom Troupe', { blur: 13, tint: 100 }, { subtitle: '幻影旅団・活動開始', accent: '#E0667A' }, ['#E0667A'])
  ];
  const apps = [
    { id: 'devin', name: 'Devin', theme: 'A Silent Voice', splash: true, speed: 1, helperExists: true, helperRunning: true, autostart: true },
    { id: 'antigravity', name: 'Antigravity', theme: 'Hunter x Hunter', splash: true, speed: 1, helperExists: true, helperRunning: false, autostart: true },
    { id: 'codex', name: 'Codex', theme: 'Hunter x Hunter', splash: false, speed: 2, helperExists: false, helperRunning: false, autostart: false }
  ];
  const listeners = [];
  const state = () => JSON.parse(JSON.stringify({ themes, apps, screen: { w: 2560, h: 1440 } }));
  function handle(m) {
    const find = n => themes.find(t => t.name === n);
    switch (m.cmd) {
      case 'state': return state();
      case 'saveTheme': { const t = find(m.name); t.look = m.look; t.text = m.text; return { look: m.look, text: m.text }; }
      case 'applyTheme': apps.forEach(a => { if (m.app === 'all' || m.app === a.id) a.theme = m.name; }); return JSON.parse(JSON.stringify(apps));
      case 'createTheme': return { cancelled: true };
      case 'renameTheme': { const t = find(m.name); if (find(m.newName)) throw new Error(`A theme called "${m.newName}" already exists.`); apps.forEach(a => { if (a.theme === t.name) a.theme = m.newName; }); t.name = m.newName; const s = state(); s.renamed = m.newName; return s; }
      case 'deleteTheme': themes = themes.filter(t => t.name !== m.name); apps.forEach(a => { if (a.theme === m.name) a.theme = ''; }); return state();
      case 'setApp': { const a = apps.find(x => x.id === m.app); if ('splash' in m) a.splash = m.splash; if ('speed' in m) a.speed = m.speed; return JSON.parse(JSON.stringify(apps)); }
      case 'setAutostart': apps.find(x => x.id === m.app).autostart = m.on; return JSON.parse(JSON.stringify(apps));
      case 'startHelper': apps.find(x => x.id === m.app).helperRunning = true; return JSON.parse(JSON.stringify(apps));
      default: return { cancelled: true };
    }
  }
  window.chrome = {
    webview: {
      addEventListener(type, fn) { if (type === 'message') listeners.push(fn); },
      postMessage(message) {
        setTimeout(() => {
          let reply;
          try { reply = { id: message.id, ok: true, data: handle(message) }; } catch (e) { reply = { id: message.id, ok: false, error: e.message }; }
          listeners.forEach(fn => fn({ data: reply }));
        }, 15);
      }
    }
  };
  window.__imgUrl = (kind, p) => kind === 'avatar' ? `../themes/${p.theme}/avatar.jpg` : `../themes/${p.theme}/wallpaper.jpg`;
  window.__splashUrl = () => null;
})();
