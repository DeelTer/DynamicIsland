'use strict';
/* Island UI. One black shape morphs between three views (compact pill, toast, panel).
   Geometry is driven by interruptible springs; content only ever fades/blurs (GPU-composited). */

const $ = s => document.querySelector(s);
const WIN = { w: 760, h: 520 };
const COMPACT_H = 34;
const native = window.chrome && window.chrome.webview;
const post = (m, files) => {
  if (!native) return;
  if (files && files.length && native.postMessageWithAdditionalObjects) native.postMessageWithAdditionalObjects(m, files);
  else native.postMessage(m);
};
// Page errors go to the native errors.log — a silent JS exception is otherwise invisible in the app.
window.addEventListener('error', e => post({ t: 'jsError', text: `${e.message} @ ${e.filename?.split('/').pop()}:${e.lineno}:${e.colno}\n${e.error?.stack || ''}` }));
window.addEventListener('unhandledrejection', e => post({ t: 'jsError', text: 'promise: ' + (e.reason?.stack || e.reason) }));
const ic = (name, size = 20, cls = '') =>
  `<svg class="ico ${cls}" width="${size}" height="${size}" viewBox="0 0 256 256" fill="currentColor" aria-hidden="true">${PH[name] || ''}</svg>`;
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const pad2 = n => String(n).padStart(2, '0');
const clock = sec => {
  sec = Math.max(0, Math.round(sec));
  const h = Math.floor(sec / 3600), m = Math.floor(sec / 60) % 60, s = sec % 60;
  return h ? `${h}:${pad2(m)}:${pad2(s)}` : `${m}:${pad2(s)}`;
};

const wrap = $('#wrap'), shape = $('#shape'), stage = $('#stage');
const ears = [...document.querySelectorAll('.ear')];
const views = { compact: $('#compact'), toast: $('#toast'), panel: $('#panel') };

// ------------------------------------------------------------------ state

const S = {
  hoverOpen: true, note: '', colors: [], apps: [], shelf: [],
  timer: { endsAt: null, pausedMs: 0, totalMs: 0 }, rec: { state: 'off' },
  battery: { has: false }, media: { has: false }, art: '', artColor: '#ff5d8f',
  clips: { items: [], history: false },
  geo: { visLeft: 0, visRight: WIN.w }
};

let mode = 'compact', tab = 'home';
let hovering = false, pinned = false, dragging = false, lifted = false, hidden = false;
let fileDrag = false, suppressClick = false;
let dwellT = 0, leaveT = 0;

// ------------------------------------------------------------------ springs

class Spring {
  constructor(x, k = 420, c = 38) { this.x = this.t = x; this.v = 0; this.k = k; this.c = c; }
  to(t, k, c) { this.t = t; if (k) this.k = k; if (c) this.c = c; }
  set(x) { this.x = this.t = x; this.v = 0; }
  step(dt) {
    if (this.idle) return;
    const n = Math.max(1, Math.ceil(dt * 480)), h = dt / n;
    for (let i = 0; i < n; i++) {
      this.v += (-this.k * (this.x - this.t) - this.c * this.v) * h;
      this.x += this.v * h;
    }
    if (Math.abs(this.x - this.t) < .02 && Math.abs(this.v) < .3) { this.x = this.t; this.v = 0; }
  }
  get idle() { return this.x === this.t && this.v === 0; }
}

const sp = {
  cx: new Spring(WIN.w / 2), w: new Spring(196), h: new Spring(COMPACT_H),
  rb: new Spring(17), rt: new Spring(0), ear: new Spring(1), lift: new Spring(0)
};

function targetBox() {
  if (mode === 'panel') return { w: 700, h: 448, r: 32 };
  if (mode === 'toast') return { w: 440, h: views.toast.offsetHeight, r: 28 };
  return { w: Math.max(196, views.compact.offsetWidth), h: COMPACT_H, r: 17 };
}

function centreFor(w) {
  const min = S.geo.visLeft + w / 2 + 8, max = S.geo.visRight - w / 2 - 8;
  return min > max ? WIN.w / 2 : Math.min(max, Math.max(min, WIN.w / 2));
}

// Feel presets: [stiffness, damping]. ζ≈0.75 gives one soft overshoot, like iOS.
const OPEN = [290, 26], CLOSE = [420, 38], HOVER = [560, 30], SNAP = [330, 25];

function layout(kc = mode === 'panel' ? OPEN : CLOSE) {
  const b = targetBox();
  let w = b.w, h = b.h;
  if (hovering && !dragging && !fileDrag && mode === 'compact') { w += 16; h += 4; }
  if (hovering && mode === 'toast') { w += 6; h += 2; }
  if (hidden) { w = Math.max(90, w * .45); h = 0; }
  const [k, c] = kc;
  sp.w.to(w, k, c); sp.h.to(h, k, c); sp.cx.to(centreFor(b.w), k, c);
  const round = lifted ? Math.min(h / 2, b.r + 4) : b.r;
  sp.rb.to(round, k, c);
  sp.rt.to(lifted ? round : 0, 420, 34);
  sp.ear.to(lifted || hidden ? 0 : 1, 420, 34);
  sp.lift.to(lifted ? 1 : 0, 380, 30);
  sendRegion(true);
  kick();
}

function apply() {
  const w = Math.max(0, sp.w.x), h = Math.max(0, sp.h.x), lift = sp.lift.x;
  wrap.style.width = w + 'px';
  wrap.style.height = h + 'px';
  wrap.style.transform = `translate3d(${sp.cx.x - w / 2}px, ${lift * 8}px, 0) scale(${1 + lift * .035})`;
  const rb = Math.max(0, Math.min(sp.rb.x, h / 2, w / 2)), rt = Math.max(0, Math.min(sp.rt.x, h / 2, w / 2));
  shape.style.borderRadius = `${rt}px ${rt}px ${rb}px ${rb}px`;
  const e = Math.max(0, Math.min(1, sp.ear.x));
  for (const ear of ears) { ear.style.opacity = e; ear.style.transform = `scale(${Math.max(.001, e)})`; }
  if (lift > .001 || shape._lift) {
    shape._lift = lift > .001;
    shape.style.boxShadow = `0 ${10 + lift * 18}px ${30 + lift * 26}px -8px rgba(0,0,0,${.55 + lift * .25}), 0 0 0 .5px rgba(255,255,255,${.06 + lift * .08})`;
  }
}

let raf = 0, last = 0;
function kick() { if (!raf) { last = performance.now(); raf = requestAnimationFrame(frame); } }
function frame(now) {
  const dt = Math.min(.05, Math.max(.001, (now - last) / 1000));
  last = now;
  let busy = false;
  for (const s of Object.values(sp)) { s.step(dt); if (!s.idle) busy = true; }
  apply();
  const edgeBusy = drawEdge(dt);
  if (busy || edgeBusy) raf = requestAnimationFrame(frame);
  else raf = 0;
  if (!busy && regionDirty) sendRegion(false);
}

// The native window is large and transparent; only this rectangle receives clicks.
let lastRegion = '', regionDirty = false;
function sendRegion(union) {
  const t = { l: sp.cx.t - sp.w.t / 2, r: sp.cx.t + sp.w.t / 2, b: sp.h.t };
  let l = t.l - 22, r = t.r + 22, b = t.b + 30;
  if (union) {
    const w = sp.w.x, cx = sp.cx.x;
    l = Math.min(l, cx - w / 2 - 34); r = Math.max(r, cx + w / 2 + 34); b = Math.max(b, sp.h.x + 44);
    if (lifted || sp.lift.x > .01) { l -= 56; r += 56; b += 70; }
    regionDirty = true;
  } else regionDirty = false;
  const x = Math.max(0, Math.floor(l)), w = Math.min(WIN.w, Math.ceil(r)) - x, h = Math.min(WIN.h, Math.ceil(b));
  const key = `${x},${w},${h}`;
  if (key === lastRegion) return;
  lastRegion = key;
  post({ t: 'region', x, y: 0, w, h });
}

// ------------------------------------------------------------------ living edge

const edge = $('#edge'), ctx = edge.getContext('2d');
const edgeAlpha = new Spring(0, 160, 24);
let lastBase = 0, phase = 0, level = .5, levelGoal = .5, levelT = 0, edgeDrawn = false;
function sizeEdge() {
  const dpr = window.devicePixelRatio || 1;
  edge.width = WIN.w * dpr; edge.height = WIN.h * dpr;
  edge.style.width = WIN.w + 'px'; edge.style.height = WIN.h + 'px';
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
}
sizeEdge();
matchMedia(`(resolution: ${window.devicePixelRatio}dppx)`).addEventListener?.('change', () => { sizeEdge(); kick(); });

function edgeColor() {
  if (S.timer.doneAt) return '#ffb340';
  if (S.rec.state === 'rec') return '#ff453a';
  if (S.media.has && S.media.playing) return S.artColor;
  return null;
}
function drawEdge(dt) {
  const color = edgeColor();
  edgeAlpha.to(color && !hidden && sp.lift.x < .5 && mode !== 'toast' ? 1 : 0);
  edgeAlpha.step(dt);
  if (edgeAlpha.x < .01 && edgeAlpha.t === 0) {
    if (edgeDrawn) { ctx.clearRect(0, 0, WIN.w, WIN.h); edgeDrawn = false; }
    return false;
  }
  if (color) edge._color = color;
  phase += dt * (S.rec.state === 'rec' ? 7 : 4.2);
  levelT -= dt;
  if (levelT <= 0) { levelGoal = .25 + Math.random() * .75; levelT = .1 + Math.random() * .22; }
  level += (levelGoal - level) * Math.min(1, dt * 9);
  const w = sp.w.x, h = sp.h.x, rb = Math.min(sp.rb.x, h / 2);
  const left = sp.cx.x - w / 2 + rb * .55, right = sp.cx.x + w / 2 - rb * .55, base = h + 4.5;
  const amp = (1.6 + level * 3.4) * edgeAlpha.x, wave = mode === 'panel' ? 34 : 18;
  ctx.clearRect(0, Math.max(0, base - 24), WIN.w, 48 + Math.abs(lastBase - base));
  lastBase = base;
  ctx.beginPath();
  for (let x = left; x <= right; x += 2) {
    const fade = Math.min(1, (x - left) / 26, (right - x) / 26);
    const y = base + Math.sin((x - left) / wave * Math.PI * 2 - phase) * amp * fade;
    x === left ? ctx.moveTo(x, y) : ctx.lineTo(x, y);
  }
  ctx.lineCap = 'round'; ctx.lineJoin = 'round';
  ctx.globalAlpha = .28 * edgeAlpha.x; ctx.strokeStyle = edge._color; ctx.lineWidth = 6; ctx.stroke();
  ctx.globalAlpha = .95 * edgeAlpha.x; ctx.lineWidth = 2; ctx.stroke();
  ctx.globalAlpha = 1;
  edgeDrawn = true;
  return true;
}

// ------------------------------------------------------------------ view switching

function switchView(instant) {
  for (const [name, el] of Object.entries(views)) {
    const on = name === mode;
    if (on === el.classList.contains('on')) continue;
    el.classList.toggle('on', on);
    for (const a of el.getAnimations()) a.cancel();
    if (instant) { el.style.opacity = on ? 1 : 0; continue; }
    el.style.opacity = '';
    if (on) {
      const big = name === 'panel'; // blur on a 700×448 layer costs frames; fade + scale only
      el.animate(big ? [
        { opacity: 0, transform: 'translateX(-50%) translateY(-12px) scale(.95)' },
        { opacity: 1, transform: 'translateX(-50%)' }
      ] : [
        { opacity: 0, transform: 'translateX(-50%) translateY(-10px) scale(.94)', filter: 'blur(8px)' },
        { opacity: 1, transform: 'translateX(-50%)', filter: 'blur(0)' }
      ], { duration: name === 'panel' ? 360 : 280, delay: name === 'panel' ? 70 : 50, easing: 'cubic-bezier(.22,1,.36,1)', fill: 'both' });
    } else {
      el.animate([
        { opacity: 1, transform: 'translateX(-50%)', filter: 'blur(0)' },
        { opacity: 0, transform: 'translateX(-50%) scale(.96)', filter: name === 'panel' ? 'none' : 'blur(5px)' }
      ], { duration: 130, easing: 'cubic-bezier(.4,0,1,1)', fill: 'both' });
    }
  }
}

function open(t) {
  clearTimeout(dwellT); clearTimeout(leaveT);
  if (S.timer.doneAt) { t = 'timer'; ackTimer(); }
  if (t) tab = t;
  if (mode === 'panel') { renderPage(true); moveIndicator(true); return; }
  if (mode === 'toast') {
    clearTimeout(toastT);
    // Opened by hovering/hotkey while a result was showing: bring it back after the panel closes.
    if (current && KEEP.has(current.kind) && current.kind !== 'timer') toasts.unshift(current);
    current = null;
  }
  if (!t && !S.timer.doneAt && Date.now() - lastClipAt < 10000) tab = 'clip';
  mode = 'panel';
  post({ t: 'panel', open: true });
  // The panel DOM is kept between openings; only rebuild what changed while it was closed.
  if (!views.panel.firstChild) renderPanel();
  else {
    if (panelDirty || pageTab !== tab) renderPage(false);
    refreshTabs();
    const pin = views.panel.querySelector('[data-act="pin"]');
    if (pin) { pin.classList.toggle('on', pinned); pin.innerHTML = ic(pinned ? 'push-pin:fill' : 'push-pin', 19); }
  }
  panelDirty = false;
  switchView();
  layout(OPEN);
}

function ackTimer() {
  if (!S.timer.doneAt) return;
  S.timer = { ...S.timer, doneAt: null };
  post({ t: 'timerAck' });
  renderCompact();
  if (current?.kind === 'timer') { clearTimeout(toastT); current = null; }
}

function close() {
  clearTimeout(leaveT);
  if (mode !== 'panel') return;
  if (document.activeElement && document.activeElement !== document.body) document.activeElement.blur();
  mode = 'compact';
  post({ t: 'panel', open: false });
  renderCompact();
  switchView();
  layout(CLOSE);
  if (toasts.length) setTimeout(nextToast, 260);
}

function scheduleClose(delay = 340) {
  clearTimeout(leaveT);
  leaveT = setTimeout(() => {
    if (!hovering && !pinned && !typing() && !dragging && !fileDrag) close();
  }, delay);
}
const typing = () => { const a = document.activeElement; return !!a && (a.tagName === 'TEXTAREA' || a.tagName === 'INPUT') && document.hasFocus(); };

// ------------------------------------------------------------------ compact

const dateFmt = new Intl.DateTimeFormat('ru-RU', { weekday: 'short', day: 'numeric' });
function timerLeft() {
  const t = S.timer;
  if (t.endsAt) return (t.endsAt - Date.now()) / 1000;
  return t.pausedMs > 0 ? t.pausedMs / 1000 : 0;
}
function activity() {
  if (S.timer.doneAt) return 'done';
  if (S.rec.state === 'rec') return 'rec';
  if (S.rec.state === 'busy') return 'busy';
  if (S.timer.endsAt || S.timer.pausedMs > 0) return 'timer';
  if (S.dl) return 'dl';
  if (S.media.has && S.media.playing) return 'media';
  return 'idle';
}
function batteryHtml() {
  const b = S.battery;
  if (!b || !b.has) return '';
  const p = b.pct;
  const name = b.charging ? 'battery-charging' : p >= 90 ? 'battery-full' : p >= 60 ? 'battery-high' : p >= 35 ? 'battery-medium' : p >= 12 ? 'battery-low' : 'battery-empty';
  const color = b.charging ? 'var(--green)' : p < 12 ? 'var(--red)' : 'var(--ink)';
  return `<span style="color:${color};display:flex">${ic(name, 20)}</span><span class="num">${p}%</span>`;
}
// Live microphone waveform: native sends a 0..1 loudness ~20×/s; bars scroll right-to-left.
const vlevels = new Array(40).fill(0);
const vwave = n => `<span class="vwave" data-n="${n}">${'<i></i>'.repeat(n)}</span>`;
function pushLevel(v) {
  vlevels.push(v); vlevels.shift();
  for (const w of document.querySelectorAll('.vwave')) {
    const bars = w.children, n = bars.length;
    for (let i = 0; i < n; i++) {
      const l = vlevels[vlevels.length - n + i];
      // a little shimmer so silence still reads as "listening"
      bars[i].style.transform = `scaleY(${(0.14 + l * 0.86 + Math.random() * 0.05).toFixed(3)})`;
    }
  }
}

const bars = (color, paused) => `<span class="bars ${paused ? 'paused' : ''}" style="color:${color}"><i></i><i></i><i></i><i></i><i></i></span>`;

function renderCompact() {
  const a = activity();
  const shelf = S.shelf.length ? `<span class="badge">${ic('tray', 14)}${S.shelf.length}</span>` : '';
  let html;
  if (a === 'done') html =
    `<div class="side" style="color:var(--orange)"><span class="ring-pulse">${ic('bell:fill', 17)}</span></div>
     <span class="time" style="color:var(--orange)">Время вышло</span>
     <div class="side"><button class="stop ok" data-act="ack" title="Готово">${ic('check', 13)}</button></div>`;
  else if (a === 'rec') html =
    `<div class="side" style="color:var(--red)">${ic('microphone:fill', 17)}${vwave(7)}</div>
     <span class="time num" style="color:#ff6961" data-k="rec"></span>
     <div class="side"><button class="stop" data-act="voice" title="Остановить">${ic('stop:fill', 12)}</button></div>`;
  else if (a === 'busy') html =
    `<div class="side" style="color:var(--green)">${ic('waveform', 18)}</div>
     <span class="muted">Распознаю речь…</span>
     <div class="side"><span class="spin" style="display:flex;color:var(--muted)">${ic('arrow-counter-clockwise', 15)}</span></div>`;
  else if (a === 'timer') html =
    `<div class="side" style="color:var(--orange)">${ic('timer:fill', 18)}</div>
     <span class="time num" style="color:var(--orange)" data-k="timer"></span>
     <div class="side"><span class="muted num" data-k="clock"></span></div>`;
  else if (a === 'dl') html =
    `<div class="side dl" style="color:#4aa3ff"><span class="dl-ico">${ic('download-simple', 17)}</span>${S.dl.count > 1 ? `<span class="badge">${S.dl.count}</span>` : ''}</div>
     <span class="time num fixed" style="color:#8cc8ff">${S.dl.pct != null ? Math.floor(S.dl.pct) + '%' + (S.dl.part ? ' · ' + S.dl.part : '') : S.dl.bytes ? size(S.dl.bytes) : '…'}</span>
     <div class="side"><span class="muted num">${S.dl.speed > 0 ? size(S.dl.speed) + '/с' : ''}</span></div>
     ${S.dl.pct != null ? `<i class="dlbar" style="transform:scaleX(${(S.dl.pct / 100).toFixed(3)})"></i>` : ''}`;
  else if (a === 'media') html =
    `<div class="side">${S.art ? `<img class="art" src="${S.art}">` : `<span style="color:${S.artColor};display:flex">${ic('music-notes', 18)}</span>`}${shelf}</div>
     <span class="time num" data-k="clock"></span>
     <div class="side">${bars(S.artColor)}</div>`;
  else html =
    `<div class="side">${shelf}<span class="muted" data-k="date"></span></div>
     <span class="time num" data-k="clock"></span>
     <div class="side">${batteryHtml()}</div>`;
  if (views.compact._html !== html) {
    views.compact.innerHTML = html; views.compact._html = html;
    // A different activity (music → download → timer…) swaps in softly; ticking numbers do not.
    if (views.compact._act !== a && views.compact._act !== undefined && mode === 'compact')
      views.compact.animate([{ opacity: 0, filter: 'blur(6px)', transform: 'translateX(-50%) scale(.94)' }, { opacity: 1, filter: 'blur(0)', transform: 'translateX(-50%)' }],
        { duration: 300, easing: 'cubic-bezier(.22,1,.36,1)' });
    views.compact._act = a;
  }
  wrap.classList.toggle('alarm', a === 'done');
  tickTexts();
}

function tickTexts() {
  const now = new Date();
  for (const el of document.querySelectorAll('[data-k]')) {
    const k = el.dataset.k;
    const v = k === 'clock' ? `${pad2(now.getHours())}:${pad2(now.getMinutes())}`
      : k === 'date' ? dateFmt.format(now).replace(',', '')
      : k === 'rec' ? clock((Date.now() - S.rec.startedAt) / 1000)
      : k === 'timer' ? clock(timerLeft()) : '';
    if (el.textContent !== v) el.textContent = v;
  }
}

// ------------------------------------------------------------------ toasts

const toasts = [];
let current = null, toastT = 0, toastLeft = 0, toastStart = 0;

// Results the user acts on (a finished download, a QR code, a screenshot, the timer) are never lost:
// over an open panel they wait in the queue and show once it closes; while the island is hidden
// (games, fullscreen) their countdown does not run.
const KEEP = new Set(['dl', 'qr', 'shot', 'timer', 'voice']);
function showToast(t) {
  // A new message replaces a pending one instead of stacking up.
  if (t.kind === 'msg') for (let i = toasts.length - 1; i >= 0; i--) if (toasts[i].kind === 'msg') toasts.splice(i, 1);
  if (mode === 'panel') {
    if (t.kind === 'msg') return; // messages never interrupt an open panel
    flash(t.title.length > 40 ? t.title.slice(0, 40) + '…' : t.title);
    if (!KEEP.has(t.kind)) return;
  }
  toasts.push(t);
  while (toasts.length > 6) { const i = toasts.findIndex(x => !KEEP.has(x.kind)); toasts.splice(i < 0 ? 0 : i, 1); }
  if (!current && mode !== 'panel') nextToast();
}

let swapping = false;
function nextToast() {
  clearTimeout(toastT);
  if (mode === 'panel' || swapping) return;
  const next = toasts.shift() || null;
  if (!next) {
    current = null;
    if (mode === 'toast') { mode = 'compact'; renderCompact(); switchView(); layout(CLOSE); }
    return;
  }
  if (mode === 'toast' && current) {
    // Toast → toast: fade the old content out, then morph the shape to the new one.
    swapping = true;
    current = next;
    const out = views.toast.animate([{ opacity: 1 }, { opacity: 0, filter: 'blur(4px)', transform: 'translateX(-50%) scale(.97)' }],
      { duration: 120, easing: 'ease-in', fill: 'forwards' });
    out.onfinish = () => { swapping = false; out.cancel(); presentToast(next, true); };
    return;
  }
  presentToast(next, false);
}
function presentToast(t, swap) {
  current = t;
  renderToast(t);
  mode = 'toast';
  if (swap) views.toast.classList.remove('on');
  switchView();
  layout(OPEN);
  toastLeft = t.ms || 4200;
  if (!hovering && !hidden) resumeToast();
}
function resumeToast() { clearTimeout(toastT); toastStart = performance.now(); toastT = setTimeout(nextToast, toastLeft); }
function pauseToast() { clearTimeout(toastT); toastLeft = Math.max(1500, toastLeft - (performance.now() - toastStart)); }

function actionButtons(actions) {
  return actions.map(a => `<button class="btn ${a.label ? '' : 'round'} ${a.primary ? 'primary' : ''}" data-act="toast:${esc(a.id)}">${a.icon ? ic(a.icon, 20) : ''}${a.label ? esc(a.label) : ''}</button>`).join('');
}

function renderToast(t) {
  const acts = `<div class="acts">${actionButtons(t.actions || [])}</div>`;
  let html;
  if (t.kind === 'color') {
    const recent = (t.extra?.recent || []).map(c => `<b style="background:${esc(c)}" data-act="color:${esc(c)}" title="${esc(c)}"></b>`).join('');
    html = `<div class="swatch" style="background:${esc(t.color)}"></div>
      <div class="txt"><div class="hex">${esc(t.title)}<small>в буфере</small></div><div class="mono">${esc(t.sub)}</div>${recent ? `<div class="recent">${recent}</div>` : ''}</div>${acts}`;
  } else if (t.kind === 'shot') {
    html = `<img class="shot" src="${t.image}"><div class="txt"><div class="title">${esc(t.title)}</div><div class="sub">${esc(t.sub)}</div></div>
      <div class="acts" style="flex-direction:column">${actionButtons(t.actions || [])}</div>`;
  } else if (t.kind === 'msg') {
    // Messenger peek: avatar + sender + one line; the whole card opens the app.
    const open = (t.actions || [])[0];
    html = `<div class="msg" ${open ? `data-act="toast:${esc(open.id)}"` : ''}>
        <div class="ava" style="--c:${esc(t.color)}">${t.image ? `<img src="${t.image}">` : ic('chat-circle-dots', 20)}<b>${esc((t.extra?.app || '?')[0])}</b></div>
        <div class="txt"><div class="title"><span class="mapp" style="color:${esc(t.color)}">${esc(t.extra?.app || '')}</span>${esc(t.title)}</div>${t.sub ? `<div class="sub">${esc(t.sub)}</div>` : ''}</div>
      </div>`;
  } else if (t.kind === 'dl' && t.image) {
    html = `<img class="dlthumb" src="${t.image}"><div class="txt"><div class="title">${esc(t.title)}</div><div class="sub">${esc(t.sub)}</div></div>${acts}`;
  } else {
    const color = t.color || '#8e8e93';
    html = `<div class="badge-ico" style="background:${color}22;color:${color}">${ic(t.icon || 'bell', 22)}</div>
      <div class="txt"><div class="title">${esc(t.title)}</div>${t.sub ? `<div class="sub">${esc(t.sub)}</div>` : ''}</div>${acts}`;
  }
  views.toast.innerHTML = html;
}

function flash(text) {
  const el = $('#flash');
  if (!el || mode !== 'panel') return;
  el.innerHTML = ic('check-circle:fill', 15) + esc(text);
  el.classList.add('show');
  clearTimeout(el._t);
  el._t = setTimeout(() => el.classList.remove('show'), 1500);
}

// ------------------------------------------------------------------ panel

const TABS = [['home', 'house', 'Главная'], ['clip', 'clipboard-text', 'Буфер'], ['note', 'note-pencil', 'Заметка'], ['timer', 'timer', 'Таймер'], ['fx', 'currency-circle-dollar', 'Валюты']];
const tabCount = id => id === 'clip' && S.clips.items.length ? `<span class="count">${S.clips.items.length}</span>` : '';

function renderPanel() {
  views.panel.innerHTML = `
    <div class="tabs">
      <div class="group" id="tabs"><div class="indicator"></div>${TABS.map(([id, icon, label]) => `
        <button class="tab ${id === tab ? 'active' : ''}" data-act="tab:${id}" data-tab="${id}">${ic(id === tab ? icon + ':fill' : icon, 20)}<span class="label">${label}</span>${tabCount(id)}</button>`).join('')}
      </div>
      <span id="dlchip" class="dlchip"></span>
      <div class="spacer"></div>
      <button class="tab ${pinned ? 'on' : ''}" data-act="pin" title="${pinned ? 'Открепить' : 'Не скрывать'}">${ic(pinned ? 'push-pin:fill' : 'push-pin', 19)}</button>
      <button class="tab" data-act="menu" title="Настройки">${ic('gear-six', 20)}</button>
    </div>
    <div class="body" id="body"><div class="page" id="page"></div></div>
    <div id="flash"></div>`;
  renderPage(false);
  dlChip();
  requestAnimationFrame(() => moveIndicator(false));
}

function refreshTabs() {
  if (mode !== 'panel') return;
  for (const btn of document.querySelectorAll('.tab[data-tab]')) {
    const id = btn.dataset.tab, def = TABS.find(t => t[0] === id);
    btn.classList.toggle('active', id === tab);
    btn.innerHTML = `${ic(id === tab ? def[1] + ':fill' : def[1], 20)}<span class="label">${def[2]}</span>${tabCount(id)}`;
  }
  moveIndicator(true);
}

function moveIndicator(animate) {
  const ind = document.querySelector('.indicator'), btn = document.querySelector(`.tab[data-tab="${tab}"]`);
  if (!ind || !btn) return;
  if (!animate) ind.style.transition = 'none';
  ind.style.width = btn.offsetWidth + 'px';
  ind.style.transform = `translateX(${btn.offsetLeft}px)`;
  if (!animate) { ind.offsetWidth; ind.style.transition = ''; }
}

let pageTab = null, panelDirty = false, lastClipAt = 0;
function renderPage(animate) {
  const body = $('#body');
  if (!body) return;
  const old = $('#page');
  const dir = TABS.findIndex(t => t[0] === tab) - TABS.findIndex(t => t[0] === pageTab);
  const page = document.createElement('div');
  page.className = 'page ' + tab;
  page.id = 'page';
  page.innerHTML = pageHtml();
  if (animate && old && pageTab !== tab) {
    old.id = '';
    old.style.pointerEvents = 'none';
    old.animate([{ opacity: 1 }, { opacity: 0, transform: `translateX(${-Math.sign(dir) * 14}px)`, filter: 'blur(3px)' }],
      { duration: 140, easing: 'ease-in', fill: 'both' }).onfinish = () => old.remove();
    body.appendChild(page);
    page.animate([{ opacity: 0, transform: `translateX(${Math.sign(dir) * 18}px)`, filter: 'blur(3px)' }, { opacity: 1, transform: 'none', filter: 'blur(0)' }],
      { duration: 300, delay: 50, easing: 'cubic-bezier(.22,1,.36,1)', fill: 'both' });
  } else {
    if (old) old.replaceWith(page); else body.prepend(page);
  }
  pageTab = tab;
  wirePage(page);
  tickPage();
}

function pageHtml() {
  if (tab === 'home') return homeHtml();
  if (tab === 'clip') return clipHtml();
  if (tab === 'note') return noteHtml();
  if (tab === 'timer') return timerHtml();
  if (tab === 'fx') return fxHtml();
  return '';
}

const tool = (icon, label, act, live) => `<button class="tool ${live ? 'live' : ''}" data-act="${act}">${ic(icon, 22)}<span>${label}</span></button>`;

function mediaHtml() {
  const m = S.media;
  if (!m.has) return `<div class="tile media empty"><span class="big">${ic('music-notes', 40)}</span><b>Ничего не играет</b><span>Запустите музыку — плеер появится здесь</span></div>`;
  const cover = S.art ? `<img class="cover" src="${S.art}">` :
    `<div class="cover" style="display:grid;place-items:center;background:linear-gradient(135deg,${S.artColor},#222)">${ic('music-notes', 44)}</div>`;
  return `<div class="tile media ${m.playing ? '' : 'paused'}" data-key="${esc(m.key)}">
      ${S.art ? `<div class="backdrop" style="background-image:url('${S.art}')"></div>` : ''}
      <button class="aout" data-act="audioList" title="Куда выводить звук">${ic('speaker-high', 17)}</button>
      ${cover}
      <div class="meta">
        <div class="src">${esc(m.source)}</div>
        <div class="ttl" title="${esc(m.title)}">${esc(m.title)}</div>
        <div class="art">${esc(m.artist)}</div>
        ${m.dur > 0 ? `<div class="progress" data-act="seek"><div class="track"><div class="fill" id="mfill" style="background:${S.artColor}"></div></div></div>
        <div class="times num"><span id="mpos"></span><span id="mrem"></span></div>` : '<div style="height:12px"></div>'}
        <div class="controls">
          <button class="c" data-act="media:prev" title="Назад">${ic('skip-back:fill', 22)}</button>
          <button class="c play" data-act="media:toggle" title="Пауза / продолжить">${ic(m.playing ? 'pause:fill' : 'play:fill', 24)}</button>
          <button class="c" data-act="media:next" title="Вперёд">${ic('skip-forward:fill', 22)}</button>
        </div>
      </div>
    </div>`;
}

function homeHtml() {
  const rec = S.rec.state === 'rec', busy = S.rec.state === 'busy';
  const apps = S.apps.map(a => `<button class="app" data-act="launch" data-id="${esc(a.id)}" title="${esc(a.name)}">
      ${a.icon ? `<img src="${a.icon}" draggable="false">` : ic('app-window', 30)}
      <span class="label">${esc(a.name)}</span>
      <span class="x" data-act="removeApp" data-id="${esc(a.id)}" title="Убрать">${ic('x', 11)}</span></button>`).join('');
  return `${mediaHtml()}
    <div class="tools">
      ${tool('selection', 'Область', 'snip')}
      ${tool('monitor', 'Весь экран', 'fullshot')}
      ${tool('eyedropper', 'Пипетка', 'pick')}
      ${rec ? `<button class="tool live rec" data-act="voice" title="Остановить и распознать">${ic('stop:fill', 22)}<span class="num" data-k="rec">0:00</span>${vwave(16)}</button>`
        : tool(busy ? 'waveform' : 'microphone', busy ? 'Распознаю…' : 'Диктовка', 'voice', false)}
      ${tool('currency-circle-dollar', 'Валюты', 'tab:fx')}
      ${tool('clipboard-text', 'Буфер', 'tab:clip')}
    </div>
    ${stripHtml()}
    <div class="tile dock" id="dock">${apps}
      <button class="app add" data-act="addApp" title="Добавить приложение">${ic('plus', 22)}</button>
      ${S.apps.length ? '' : '<span class="hint">Перетащите сюда приложение или ярлык</span>'}
    </div>`;
}

const DOC_COLORS = {
  pdf: '#e5484d', png: '#3b82f6', jpg: '#3b82f6', jpeg: '#3b82f6', gif: '#3b82f6', webp: '#3b82f6', bmp: '#3b82f6', svg: '#3b82f6',
  txt: '#8e8e93', md: '#8e8e93', log: '#8e8e93', json: '#6e6e73', zip: '#f5a524', rar: '#f5a524', '7z': '#f5a524',
  doc: '#2b6cdf', docx: '#2b6cdf', xls: '#1f9d55', xlsx: '#1f9d55', csv: '#1f9d55', ppt: '#e2683c', pptx: '#e2683c',
  exe: '#6e56cf', msi: '#6e56cf', mp3: '#ec4899', wav: '#ec4899', flac: '#ec4899', mp4: '#8b5cf6', mov: '#8b5cf6', mkv: '#8b5cf6'
};
const size = n => n < 1024 ? n + ' Б' : n < 1048576 ? (n / 1024).toFixed(n < 10240 ? 1 : 0) + ' КБ' : n < 1073741824 ? (n / 1048576).toFixed(1) + ' МБ' : (n / 1073741824).toFixed(1) + ' ГБ';

function docIcon(f, big) {
  if (f.thumb) return `<img src="${f.thumb}" draggable="false" class="${f.ext === 'folder' || f.ext === 'exe' || f.ext === 'lnk' ? 'icon' : ''}">`;
  if (f.ext === 'folder') return `<span style="color:#f5b84a;display:flex">${ic('folder-open', 34)}</span>`;
  return `<div class="doc ${big ? '' : 'small'}" style="background:${DOC_COLORS[f.ext] || '#636366'}">${esc((f.ext || 'file').slice(0, 4).toUpperCase())}</div>`;
}

function stripHtml() {
  const n = S.shelf.length;
  if (!n) return `<div class="tile strip empty" id="strip">${ic('tray', 22)}<span><b>Полка</b> — временное место для файлов: положите сюда, потом перетащите куда нужно</span>
      <button class="btn ghost" data-act="shelfPaste">${ic('clipboard-text', 16)}Вставить</button></div>`;
  const old = S.shelf.filter(f => f.age > 24).length;
  const items = S.shelf.map(f => `<div class="sitem ${f.age > 24 ? 'old' : ''}" data-file="${esc(f.id)}" title="${esc(f.name)}${f.ext === 'folder' ? ' · двойной клик — открыть' : ''}">
      <div class="th">${docIcon(f)}</div>
      <div class="meta"><div class="name">${esc(f.name)}</div><div class="size num">${f.ext === 'folder' ? 'Папка' : size(f.size)}</div></div>
      <span class="x" data-act="shelfRemove" data-id="${esc(f.id)}" title="Убрать с полки">${ic('x', 11)}</span></div>`).join('');
  return `<div class="tile strip" id="strip">
      <button class="grab" data-drag="all" title="Перетащить всё (${n})">${ic('dots-six-vertical', 18)}<span class="num">${n}</span></button>
      <div class="items" id="sitems">${items}</div>
      ${old ? `<button class="btn ghost" data-act="shelfClearOld" title="Убрать файлы старше суток">${ic('trash', 16)}Старые (${old})</button>` : ''}
      <button class="btn ghost round" data-act="shelfClear" title="Очистить полку">${ic('trash', 18)}<span></span></button>
    </div>`;
}

// ------------------------------------------------------------------ clipboard tab

let clipFilter = 'Все', clipSel = -1;
const CLIP_FILTERS = ['Все', 'Картинки', 'Текст', 'Ссылки'];
function clipVisible() {
  const q = ($('#cq')?.value || '').trim().toLowerCase();
  // Images match by the text recognised on them (OCR), so screenshots are searchable.
  return S.clips.items.filter(c => (clipFilter === 'Все' || c.kind === clipFilter) &&
    (!q || (c.text && c.text.toLowerCase().includes(q)) || (c.ocr && c.ocr.toLowerCase().includes(q))));
}
function clipHtml() {
  return `<div class="cbar">
      <div class="search">${ic('magnifying-glass', 17)}<input id="cq" placeholder="Поиск по буферу" autocomplete="off" spellcheck="false"></div>
      ${CLIP_FILTERS.map(f => `<button class="chip ${f === clipFilter ? 'on' : ''}" data-act="cfilter:${f}">${f}</button>`).join('')}
      <div class="spacer"></div>
      <button class="toggle" data-act="chistory" title="Запоминать скопированное">История<span class="switch ${S.clips.history ? 'on' : ''}"></span></button>
      <button class="btn ghost round" data-act="cclear" title="Очистить незакреплённые">${ic('trash', 17)}<span></span></button>
    </div>
    <div class="clist" id="clist">${clipListHtml()}</div>`;
}
function clipListHtml() {
  const list = clipVisible();
  if (!S.clips.history && !S.clips.items.length)
    return `<div class="cempty">${ic('clipboard-text', 32)}<b>История выключена</b><span>Включите переключатель «История» — Island начнёт запоминать скопированное. Пароли из менеджеров не сохраняются.</span></div>`;
  if (!list.length) return `<div class="cempty">${ic('magnifying-glass', 28)}<b>${$('#cq')?.value ? 'Ничего не найдено' : 'Пусто'}</b><span>Скопируйте что-нибудь — оно появится здесь</span></div>`;
  const pin = c => `<span class="cact ${c.pinned ? 'pinned' : ''}" data-act="cpin" title="${c.pinned ? 'Открепить' : 'Закрепить'}">${ic(c.pinned ? 'star:fill' : 'star', 14)}</span>
      <span class="cact" data-act="cremove" title="Удалить">${ic('x', 13)}</span>`;
  const images = list.filter(c => c.image), texts = list.filter(c => !c.image);
  let html = '';
  const ocrBtn = c => c.ocr ? `<span class="cact" data-act="cocr" title="Копировать текст с картинки">${ic('text-t', 13)}</span>` : '';
  if (images.length) html += `<div class="cgrid">${images.map(c => `<div class="citem cimg ${c.i === clipSel ? 'sel' : ''}" data-i="${c.i}" title="Клик — копировать, двойной — вставить, тяните — как файл">
      <img src="${c.image}" draggable="false"><div class="cacts">${ocrBtn(c)}${pin(c)}</div></div>`).join('')}</div>`;
  if (texts.length) html += `<div class="crows">${texts.map(c => `<div class="citem crow ${c.i === clipSel ? 'sel' : ''}" data-i="${c.i}" title="Клик — копировать, двойной — вставить">
      <span class="kind">${ic(c.kind === 'Ссылки' ? 'link' : 'text-t', 15)}</span>
      <div class="val ${c.kind === 'Ссылки' ? 'link' : ''}">${esc(c.text)}</div><div class="cacts">${pin(c)}</div></div>`).join('')}</div>`;
  return html;
}
function renderClipList() { const l = $('#clist'); if (l) l.innerHTML = clipListHtml(); }

function noteHtml() {
  return `<textarea id="note" placeholder="Быстрая заметка…" spellcheck="false" maxlength="16000"></textarea>
    <div class="foot"><span class="num" id="ncount"></span><div class="spacer"></div>
      <button class="btn ghost" data-act="copyNote">${ic('copy', 16)}Скопировать</button></div>`;
}

function timerHtml() {
  const t = S.timer, active = !!t.endsAt || t.pausedMs > 0;
  const presets = [1, 3, 5, 10, 15, 25, 45, 60].map(m => `<button class="preset" data-act="timer:${m}">${m}<small>мин</small></button>`).join('');
  return `<div class="tile ring">
      <svg width="210" height="210" viewBox="0 0 210 210">
        <circle cx="105" cy="105" r="92" fill="none" stroke="rgba(255,255,255,.08)" stroke-width="10"/>
        <circle id="tarc" cx="105" cy="105" r="92" fill="none" stroke="var(--orange)" stroke-width="10" stroke-linecap="round"
          transform="rotate(-90 105 105)" stroke-dasharray="578.05" stroke-dashoffset="578.05"/>
      </svg>
      <div><div class="val num" id="tval">0:00</div><div class="lbl" id="tlbl">${active ? (t.endsAt ? 'идёт' : 'пауза') : 'выберите время'}</div></div>
    </div>
    <div class="presets">${presets}
      <form class="custom" id="tform"><input id="tmin" autocomplete="off" spellcheck="false" placeholder="25, 1ч30м, 90s, 18:30…"><button class="btn primary" type="submit">Старт</button></form>
      ${active ? `<div class="runctl">
        <button class="btn" data-act="timerToggle">${ic(t.endsAt ? 'pause:fill' : 'play:fill', 16)}${t.endsAt ? 'Пауза' : 'Продолжить'}</button>
        <button class="btn ghost" data-act="timerReset">${ic('arrow-counter-clockwise', 16)}Сбросить</button></div>` : ''}
    </div>`;
}

// Duration from free text, RU/EN: "25" (minutes), "1ч30м", "2h 15m 10s", "90 сек", "1д",
// a clock time "18:30" (next occurrence) or an absolute "20.02.2026 15:00" / "2026-02-20 15:00".
const UNITS = { с: 1e3, сек: 1e3, секунд: 1e3, секунды: 1e3, секунда: 1e3, s: 1e3, sec: 1e3, second: 1e3, seconds: 1e3,
  м: 6e4, мин: 6e4, минут: 6e4, минуты: 6e4, минута: 6e4, m: 6e4, min: 6e4, minute: 6e4, minutes: 6e4,
  ч: 36e5, час: 36e5, часа: 36e5, часов: 36e5, h: 36e5, hr: 36e5, hour: 36e5, hours: 36e5,
  д: 864e5, дн: 864e5, день: 864e5, дня: 864e5, дней: 864e5, d: 864e5, day: 864e5, days: 864e5 };
function parseDuration(text) {
  const t = text.trim().toLowerCase().replace(',', '.');
  if (!t) return 0;
  if (/^\d+(\.\d+)?$/.test(t)) return parseFloat(t) * 6e4;
  let m = t.match(/^(?:(\d{1,2})\.(\d{1,2})\.(\d{4})|(\d{4})-(\d{1,2})-(\d{1,2}))(?:\s+(\d{1,2}):(\d{2})(?::(\d{2}))?)?$/);
  if (m) {
    const [d, mo, y] = m[1] ? [m[1], m[2], m[3]] : [m[6], m[5], m[4]];
    return new Date(+y, mo - 1, +d, +(m[7] || 0), +(m[8] || 0), +(m[9] || 0)) - Date.now();
  }
  m = t.match(/^(?:в\s*)?(\d{1,2}):(\d{2})$/);
  if (m) {
    const at = new Date(); at.setHours(+m[1], +m[2], 0, 0);
    if (at <= Date.now()) at.setDate(at.getDate() + 1);
    return at - Date.now();
  }
  let total = 0, found = false;
  for (const [, n, u] of t.matchAll(/(\d+(?:\.\d+)?)\s*([a-zа-яё]+)/g)) {
    if (!(u in UNITS)) return 0;
    total += parseFloat(n) * UNITS[u]; found = true;
  }
  return found ? total : 0;
}

// ------------------------------------------------------------------ audio output popover
function showAudio(list) {
  const page = $('#page'), card = $('.media');
  $('#apop')?.remove();
  if (!page || !card || mode !== 'panel' || tab !== 'home') return;
  const pop = document.createElement('div');
  pop.id = 'apop'; pop.className = 'apop';
  const icon = n => /науш|headphone|headset|airpods|buds|гарнит/i.test(n) ? 'headphones' : 'speaker-high';
  pop.innerHTML = list.length ? list.map(d => `<button class="aopt ${d.on ? 'on' : ''}" data-act="audioSet" data-id="${esc(d.id)}" data-name="${esc(d.name)}">
      ${ic(icon(d.name), 18)}<span>${esc(d.name)}</span>${d.on ? ic('check', 16) : ''}</button>`).join('')
    : '<div class="aopt">Устройства не найдены</div>';
  const r = card.getBoundingClientRect(), p = page.getBoundingClientRect();
  pop.style.left = (r.right - p.left - 300) + 'px';
  pop.style.top = (r.top - p.top + 44) + 'px';
  page.appendChild(pop);
}
document.addEventListener('pointerdown', e => { if (!e.target.closest?.('#apop, .aout')) $('#apop')?.remove(); }, true);

// ------------------------------------------------------------------ currency converter
// Rates: Coinbase, units per 1 USD (fiat + crypto). Type into any row — every other row follows.
const FX_FIAT = [
  ['USD', 'us', 'Доллар США'], ['EUR', 'eu', 'Евро'], ['RUB', 'ru', 'Рубль'], ['BYN', 'by', 'Белорусский рубль'],
  ['UAH', 'ua', 'Гривна'], ['KZT', 'kz', 'Тенге'], ['PLN', 'pl', 'Злотый'], ['GEL', 'ge', 'Лари'], ['TRY', 'tr', 'Турецкая лира'],
  ['CNY', 'cn', 'Юань'], ['GBP', 'gb', 'Фунт стерлингов'], ['JPY', 'jp', 'Иена'], ['CHF', 'ch', 'Швейцарский франк'],
  ['CZK', 'cz', 'Чешская крона'], ['AMD', 'am', 'Драм'], ['AZN', 'az', 'Манат'], ['UZS', 'uz', 'Узбекский сум'],
  ['KGS', 'kg', 'Сом'], ['TJS', 'tj', 'Сомони'], ['MDL', 'md', 'Молдавский лей'], ['AED', 'ae', 'Дирхам ОАЭ'],
  ['THB', 'th', 'Бат'], ['INR', 'in', 'Индийская рупия'], ['KRW', 'kr', 'Вона'], ['CAD', 'ca', 'Канадский доллар'],
  ['AUD', 'au', 'Австралийский доллар'], ['SEK', 'se', 'Шведская крона'], ['NOK', 'no', 'Норвежская крона'],
  ['HUF', 'hu', 'Форинт'], ['RSD', 'rs', 'Сербский динар'], ['ILS', 'il', 'Шекель'], ['VND', 'vn', 'Донг'],
  ['IDR', 'id', 'Рупия'], ['BRL', 'br', 'Реал'], ['MXN', 'mx', 'Мексиканское песо'], ['HKD', 'hk', 'Гонконгский доллар'],
  ['SGD', 'sg', 'Сингапурский доллар'], ['EGP', 'eg', 'Египетский фунт']
];
const FX_CRYPTO = [
  ['BTC', '#f7931a', '₿', 'Bitcoin'], ['ETH', '#627eea', 'Ξ', 'Ethereum'], ['USDT', '#26a17b', '₮', 'Tether'],
  ['TON', '#0098ea', '◆', 'Toncoin'], ['SOL', '#9945ff', 'S', 'Solana'], ['XMR', '#ff6600', 'ɱ', 'Monero'],
  ['LTC', '#a6a9aa', 'Ł', 'Litecoin'], ['ZEC', '#ecb244', 'ⓩ', 'Zcash'], ['DOGE', '#c2a633', 'Ð', 'Dogecoin'],
  ['XRP', '#3d4651', 'X', 'XRP'], ['TRX', '#ef0027', 'T', 'TRON'], ['USDC', '#2775ca', '$', 'USD Coin']
];
const FX_INFO = Object.fromEntries([
  ...FX_FIAT.map(([c, f, n]) => [c, { flag: f, name: n }]),
  ...FX_CRYPTO.map(([c, col, sym, n]) => [c, { crypto: true, color: col, sym, name: n }])
]);
const FX = { rates: null, at: 0, error: false, prefs: null, picker: false, q: '' };

function fxPrefs() {
  if (!FX.prefs) {
    let p = null;
    try { p = JSON.parse(S.fxPrefs || 'null'); } catch { }
    FX.prefs = Object.assign({ list: ['USD', 'EUR', 'RUB', 'BYN', 'UAH', 'KZT', 'PLN', 'TRY', 'CNY', 'BTC', 'ETH', 'TON'], use: {}, base: 'USD', amount: 1 }, p || {});
  }
  return FX.prefs;
}
let fxSaveT = 0;
function fxSave() { clearTimeout(fxSaveT); fxSaveT = setTimeout(() => post({ t: 'fxPrefs', v: JSON.stringify(FX.prefs) }), 600); }

const fxBadge = c => {
  const i = FX_INFO[c] || {};
  if (i.flag) return `<img class="flag" src="https://flagcdn.com/w40/${i.flag}.png" alt="" draggable="false" onerror="this.replaceWith(Object.assign(document.createElement('span'),{className:'coin',textContent:'${c[0]}'}))">`;
  return `<span class="coin" style="background:${i.color || '#636366'}">${i.sym || c[0]}</span>`;
};
function fxConvert(amount, from, to) {
  const r = FX.rates;
  if (!r || !r[from] || !r[to]) return NaN;
  return amount / +r[from] * +r[to];
}
function fxFormat(v, code) {
  if (!isFinite(v)) return '';
  const crypto = FX_INFO[code]?.crypto && !/^USD[TC]$/.test(code);
  const abs = Math.abs(v);
  const digits = crypto ? (abs >= 1000 ? 2 : abs >= 1 ? 4 : Math.min(10, Math.max(4, 2 - Math.floor(Math.log10(abs || 1)) + 3))) : (abs >= 1e6 ? 0 : 2);
  return v.toLocaleString('ru-RU', { maximumFractionDigits: digits, minimumFractionDigits: 0 }).replace(/ /g, ' ');
}
// "1 500,5", "100*3", "2k" → number
function fxParse(text) {
  let t = String(text).toLowerCase().replace(/[\s ]/g, '').replace(/,/g, '.').replace(/(\d)k\b/g, '$1*1000').replace(/(\d)к\b/g, '$1*1000');
  if (!t) return NaN;
  if (!/^[\d.+\-*/()]+$/.test(t)) return NaN;
  try { const v = Function(`"use strict";return (${t})`)(); return typeof v === 'number' && isFinite(v) ? v : NaN; } catch { return NaN; }
}
function fxOrdered() {
  const p = fxPrefs();
  return [...p.list].sort((a, b) => (p.use[b] || 0) - (p.use[a] || 0) || p.list.indexOf(a) - p.list.indexOf(b));
}

function fxHtml() {
  const p = fxPrefs();
  const rows = fxOrdered().map(c => `<label class="fxrow ${c === p.base ? 'base' : ''}" data-code="${c}" title="${esc(FX_INFO[c]?.name || c)}">
      <input class="num" data-code="${c}" inputmode="decimal" autocomplete="off" spellcheck="false">
      <span class="code">${c}</span>${fxBadge(c)}
      <span class="x" data-act="fxRemove" data-code="${c}" title="Убрать">${ic('x', 10)}</span></label>`).join('');
  const when = FX.at ? new Date(FX.at).toLocaleString('ru-RU', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' }) : '';
  return `<div class="fxgrid" id="fxgrid">${rows}
      <button class="fxrow add" data-act="fxAdd">${ic('plus', 16)}Валюта</button></div>
    <div class="fxfoot">
      <span>${FX.error ? 'Нет связи — курсы от ' + (when || '—') : FX.rates ? 'Курсы Coinbase · ' + when : 'Загружаю курсы…'}</span>
      <span class="spacer"></span><span class="hint">можно писать 1500*3 или 2к</span>
      <button class="btn ghost round" data-act="fxRefresh" title="Обновить курсы">${ic('arrows-clockwise', 16)}</button>
    </div>
    ${FX.picker ? fxPickerHtml() : ''}`;
}

function fxPickerHtml() {
  const p = fxPrefs(), q = FX.q.trim().toLowerCase();
  const all = [...FX_FIAT.map(f => f[0]), ...FX_CRYPTO.map(f => f[0])]
    .filter(c => !p.list.includes(c) && (!FX.rates || FX.rates[c]))
    .filter(c => !q || c.toLowerCase().includes(q) || FX_INFO[c].name.toLowerCase().includes(q))
    .sort((a, b) => (p.use[b] || 0) - (p.use[a] || 0));
  return `<div class="fxpick" id="fxpick">
      <div class="fxsearch">${ic('magnifying-glass', 16)}<input id="fxq" placeholder="Найти валюту: рубль, BTC, лира…" autocomplete="off" spellcheck="false" value="${esc(FX.q)}">
        <button class="btn ghost round" data-act="fxClose">${ic('x', 14)}</button></div>
      <div class="fxlist" id="fxlist">${all.map(c => `<button class="fxopt" data-act="fxPick" data-code="${c}">${fxBadge(c)}<b>${c}</b><span>${esc(FX_INFO[c].name)}</span></button>`).join('') || '<div class="empty">Ничего не найдено</div>'}</div>
    </div>`;
}

function fxFill(except) {
  const p = fxPrefs();
  for (const inp of document.querySelectorAll('.fxrow input')) {
    if (inp === except) continue;
    const c = inp.dataset.code;
    inp.value = c === p.base ? fxFormat(p.amount, c) : fxFormat(fxConvert(p.amount, p.base, c), c);
  }
}

function wireFx(page) {
  if (!page.querySelector('#fxgrid')) return;
  const p = fxPrefs();
  if (!FX.at || Date.now() - FX.at > 25 * 60e3) post({ t: 'fxRefresh' });
  fxFill(null);
  for (const inp of page.querySelectorAll('.fxrow input')) {
    inp.addEventListener('focus', () => { post({ t: 'focus' }); inp.select(); });
    inp.addEventListener('input', () => {
      const v = fxParse(inp.value);
      if (isNaN(v)) return;
      const c = inp.dataset.code;
      if (p.base !== c) { p.base = c; p.use[c] = (p.use[c] || 0) + 1; }
      for (const r of page.querySelectorAll('.fxrow')) r.classList.toggle('base', r.dataset.code === c);
      p.amount = v; fxFill(inp); fxSave();
    });
    inp.addEventListener('blur', () => { if (inp.dataset.code === p.base) inp.value = fxFormat(p.amount, p.base); });
    inp.addEventListener('keydown', e => {
      if (e.key === 'Enter') { inp.value = fxFormat(p.amount, p.base); inp.select(); }
    });
  }
  // Clicking a currency code copies its value.
  for (const code of page.querySelectorAll('.fxrow .code')) code.addEventListener('click', e => {
    e.preventDefault();
    const v = code.parentElement.querySelector('input').value;
    if (v) { post({ t: 'copy', text: v.replace(/ /g, '') }); flash(`Скопировано ${v} ${code.textContent}`); }
  });
  const q = page.querySelector('#fxq');
  if (q) {
    q.focus();
    q.addEventListener('input', () => {
      FX.q = q.value;
      const list = $('#fxlist'), html = fxPickerHtml();
      const tmp = document.createElement('div'); tmp.innerHTML = html;
      list.replaceWith(tmp.querySelector('#fxlist'));
    });
    q.addEventListener('keydown', e => {
      if (e.key === 'Escape') { FX.picker = false; renderPage(false); }
      if (e.key === 'Enter') $('#fxlist .fxopt')?.click();
    });
  }
}

function fxAct(act, el) {
  const p = fxPrefs();
  if (act === 'fxAdd') { FX.picker = true; FX.q = ''; renderPage(false); }
  else if (act === 'fxClose') { FX.picker = false; renderPage(false); }
  else if (act === 'fxPick') {
    const c = el.dataset.code;
    p.list.push(c); p.use[c] = (p.use[c] || 0) + 1;
    FX.picker = false; fxSave(); renderPage(false);
  } else if (act === 'fxRemove') {
    const c = el.dataset.code;
    p.list = p.list.filter(x => x !== c);
    if (p.base === c && p.list.length) { p.amount = fxConvert(p.amount, c, p.list[0]) || 1; p.base = p.list[0]; }
    fxSave(); renderPage(false);
  } else if (act === 'fxRefresh') { post({ t: 'fxRefresh' }); flash('Обновляю курсы…'); }
}

function wirePage(page) {
  wireFx(page);
  const note = page.querySelector('#note');
  if (note) {
    note.value = S.note || '';
    const count = () => { page.querySelector('#ncount').textContent = note.value.length ? `${note.value.length} символов` : ''; };
    count();
    let t = 0;
    note.addEventListener('input', () => { S.note = note.value; count(); clearTimeout(t); t = setTimeout(() => post({ t: 'note', text: note.value }), 250); });
    note.addEventListener('focus', () => post({ t: 'focus' }));
  }
  const cq = page.querySelector('#cq');
  if (cq) {
    cq.addEventListener('input', () => { clipSel = -1; renderClipList(); });
    cq.addEventListener('focus', () => post({ t: 'focus' }));
    cq.addEventListener('keydown', e => {
      const list = clipVisible();
      if (!list.length) return;
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp' || e.key === 'ArrowRight' || e.key === 'ArrowLeft') {
        e.preventDefault();
        const idx = Math.max(0, list.findIndex(c => c.i === clipSel));
        const next = Math.max(0, Math.min(list.length - 1, idx + (e.key === 'ArrowDown' || e.key === 'ArrowRight' ? 1 : -1)));
        clipSel = list[clipSel === -1 ? 0 : next].i;
        renderClipList();
        document.querySelector('.citem.sel')?.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
      } else if (e.key === 'Enter') {
        e.preventDefault();
        const c = list.find(c => c.i === clipSel) || list[0];
        post({ t: e.shiftKey ? 'clipCopy' : 'clipPaste', i: c.i });
      }
    });
  }
  const strip = page.querySelector('#sitems');
  if (strip) strip.addEventListener('wheel', e => { if (Math.abs(e.deltaY) > Math.abs(e.deltaX)) { strip.scrollLeft += e.deltaY; e.preventDefault(); } }, { passive: false });
  const form = page.querySelector('#tform');
  if (form) {
    const input = form.querySelector('input');
    input.addEventListener('focus', () => post({ t: 'focus' }));
    form.addEventListener('submit', e => {
      e.preventDefault();
      const ms = parseDuration(input.value), min = ms / 60000;
      if (min >= 1 / 60 && min <= 7 * 1440) { post({ t: 'timer', cmd: 'start', min }); input.value = ''; input.blur(); }
      else input.animate([{ transform: 'translateX(-6px)' }, { transform: 'translateX(6px)' }, { transform: 'none' }], { duration: 240, easing: 'ease-out' });
    });
  }
}

function tickPage() {
  if (mode !== 'panel') return;
  if (tab === 'home' && S.media.has && S.media.dur > 0) {
    const m = S.media, pos = Math.min(m.dur, m.pos + (m.playing ? (Date.now() - m.at) / 1000 : 0));
    const fill = $('#mfill');
    if (fill) fill.style.transform = `scaleX(${pos / m.dur})`;
    const p = $('#mpos'), r = $('#mrem');
    if (p) p.textContent = clock(pos);
    if (r) r.textContent = '-' + clock(m.dur - pos);
  }
  if (tab === 'timer') {
    const left = timerLeft(), total = S.timer.totalMs / 1000;
    const v = $('#tval'), arc = $('#tarc');
    if (v) v.textContent = clock(left);
    if (arc) arc.setAttribute('stroke-dashoffset', total > 0 ? 578.05 * (1 - Math.max(0, left) / total) : 578.05);
  }
}

// ------------------------------------------------------------------ actions

function act(name, el, e) {
  const [cmd, arg] = name.split(/:(.*)/s);
  switch (cmd) {
    case 'tab': {
      const prev = tab;
      tab = arg === 'shelf' ? 'home' : arg;
      if (mode !== 'panel') open(tab);
      else if (prev !== arg) { renderPage(true); refreshTabs(); }
      break;
    }
    case 'media':
      post({ t: 'media', cmd: arg });
      if (arg === 'toggle' && S.media.has) {
        const m = S.media;
        m.pos = Math.min(m.dur || 0, m.pos + (m.playing ? (Date.now() - m.at) / 1000 : 0)); m.at = Date.now();
        m.playing = !m.playing;
        mediaHold = { until: Date.now() + 2500, playing: m.playing, key: m.key };
        updateMedia(true);
        renderCompact();
      } else if (arg !== 'toggle') bounce($('.media'), arg === 'next' ? 1 : -1);
      break;
    case 'seek': {
      const r = el.getBoundingClientRect(), frac = Math.min(1, Math.max(0, (e.clientX - r.left) / r.width));
      post({ t: 'media', cmd: 'seek', frac });
      S.media.pos = frac * S.media.dur; S.media.at = Date.now(); tickPage();
      break;
    }
    case 'launch': post({ t: 'launch', id: el.dataset.id }); close(); break;
    case 'removeApp': e.stopPropagation(); post({ t: 'removeApp', id: el.dataset.id }); break;
    case 'addApp': post({ t: 'addApp' }); break;
    case 'snip': close(); post({ t: 'snip' }); break;
    case 'fullshot': close(); setTimeout(() => post({ t: 'fullshot' }), 260); break;
    case 'pick': close(); setTimeout(() => post({ t: 'pick' }), 200); break;
    case 'voice': post({ t: 'voice' }); break;
    case 'cfilter': clipFilter = arg; clipSel = -1; for (const c of document.querySelectorAll('.cbar .chip')) c.classList.toggle('on', c.textContent === arg); renderClipList(); break;
    case 'chistory': post({ t: 'clipHistory', on: !S.clips.history }); break;
    case 'cpin': e.stopPropagation(); post({ t: 'clipPin', i: +el.closest('.citem').dataset.i }); break;
    case 'cremove': e.stopPropagation(); post({ t: 'clipRemove', i: +el.closest('.citem').dataset.i }); break;
    case 'cocr': {
      e.stopPropagation();
      const c = S.clips.items.find(x => x.i === +el.closest('.citem').dataset.i);
      if (c?.ocr) { post({ t: 'copy', text: c.ocr }); }
      break;
    }
    case 'cclear': armed(el, () => post({ t: 'clipClear' })); break;
    case 'ack': ackTimer(); break;
    case 'pin': pinned = !pinned; el.classList.toggle('on', pinned); el.innerHTML = ic(pinned ? 'push-pin:fill' : 'push-pin', 19); break;
    case 'menu': post({ t: 'menu' }); break;
    case 'ytGet': post({ t: 'ytGet', url: ymenu.data?.url || '', q: el.dataset.q }); $('#ymenu')?.remove(); flash('Скачиваю — прогресс в пилюле'); break;
    case 'ytInstall': post({ t: 'ytInstall' }); showYMenu({ state: 'installing' }); break;
    case 'ytCancel': post({ t: 'ytCancel' }); $('#ymenu')?.remove(); break;
    case 'ytSearch': post({ t: 'ytSearch', q: el.dataset.q }); $('#ymenu')?.remove(); break;
    case 'audioList':
      e?.stopPropagation();
      if ($('#apop')) $('#apop').remove(); else post({ t: 'audioList' });
      break;
    case 'audioSet': post({ t: 'audioSet', id: el.dataset.id }); $('#apop')?.remove(); flash('Звук → ' + el.dataset.name); break;
    case 'fxAdd': case 'fxClose': case 'fxPick': case 'fxRemove': case 'fxRefresh':
      e?.preventDefault(); e?.stopPropagation(); fxAct(cmd, el); break;
    case 'shelfRemove': e.stopPropagation(); post({ t: 'shelfRemove', id: el.dataset.id }); break;
    case 'shelfPaste': post({ t: 'shelfPaste' }); break;
    case 'shelfClearOld': post({ t: 'shelfClearOld' }); break;
    case 'shelfClear': armed(el, () => post({ t: 'shelfClear' })); break;
    case 'timer': post({ t: 'timer', cmd: 'start', min: +arg }); break;
    case 'timerToggle': post({ t: 'timer', cmd: 'toggle' }); break;
    case 'timerReset': post({ t: 'timer', cmd: 'reset' }); break;
    case 'copyNote': if (S.note) post({ t: 'copy', text: S.note }); break;
    case 'color': post({ t: 'copy', text: arg }); break;
    case 'toast':
      e.stopPropagation();
      if (arg.startsWith('tab:')) { current = null; open(arg.slice(4) === 'shelf' ? 'home' : arg.slice(4)); break; }
      if (current?.kind === 'timer') post({ t: 'timerAck' });
      if (arg !== 'dismiss') post({ t: 'toastAction', id: arg });
      clearTimeout(toastT); nextToast();
      break;
  }
}

// Two-step destructive buttons: first click arms (red), second within 2.5 s confirms.
function armed(el, run) {
  if (el.dataset.armed) { delete el.dataset.armed; el.classList.remove('armed'); run(); return; }
  el.dataset.armed = '1'; el.classList.add('armed');
  flash('Нажмите ещё раз, чтобы очистить');
  setTimeout(() => { if (el.isConnected) { delete el.dataset.armed; el.classList.remove('armed'); } }, 2500);
}

function bounce(el, dir = 0) {
  if (!el) return;
  el.animate([
    { transform: 'scale(1)' },
    { transform: `scale(.965) translateX(${dir * 4}px)`, offset: .28 },
    { transform: `scale(1.012) translateX(${-dir * 1.5}px)`, offset: .62 },
    { transform: 'scale(1)' }
  ], { duration: 520, easing: 'cubic-bezier(.3,.7,.3,1)' });
}

// Media card 3D tilt + gloss: the hovered corner dips toward the cursor; eased in one rAF loop, idle when settled.
const tilt = { card: null, tx: 0, ty: 0, x: 0, y: 0, gx: 50, gy: 50, g: 0, tg: 0, raf: 0 };
function tiltFrame() {
  const t = tilt, k = 0.16;
  t.x += (t.tx - t.x) * k; t.y += (t.ty - t.y) * k; t.g += (t.tg - t.g) * k;
  const c = t.card;
  if (c && c.isConnected) {
    const angle = Math.hypot(t.x, t.y);
    c.style.rotate = angle < 0.01 ? 'none' : `${-t.y} ${t.x} 0 ${angle.toFixed(2)}deg`;
    c.style.setProperty('--gx', t.gx + '%'); c.style.setProperty('--gy', t.gy + '%');
    c.style.setProperty('--gloss', t.g.toFixed(3));
  }
  const settled = Math.abs(t.tx - t.x) + Math.abs(t.ty - t.y) + Math.abs(t.tg - t.g) < 0.01;
  t.raf = settled ? 0 : requestAnimationFrame(tiltFrame);
}
function tiltKick() { if (!tilt.raf) tilt.raf = requestAnimationFrame(tiltFrame); }
document.addEventListener('pointermove', e => {
  const card = e.target.closest?.('.media');
  if (!card || card.classList.contains('empty')) {
    if (tilt.card) { tilt.tx = tilt.ty = tilt.tg = 0; tiltKick(); }
    return;
  }
  const r = card.getBoundingClientRect();
  const px = (e.clientX - r.left) / r.width, py = (e.clientY - r.top) / r.height;
  tilt.card = card;
  tilt.tx = (px - 0.5) * 10;   // deg around Y
  tilt.ty = (py - 0.5) * 10;   // deg around X
  tilt.gx = px * 100; tilt.gy = py * 100; tilt.tg = 1;
  tiltKick();
}, { passive: true });
document.addEventListener('pointerleave', () => { tilt.tx = tilt.ty = tilt.tg = 0; tiltKick(); });

// Play/pause updates in place so CSS transitions (cover scale, icon) can spring instead of re-rendering.
let mediaHold = null;
function updateMedia(fromUser) {
  const card = $('.media');
  if (mode !== 'panel' || tab !== 'home' || !card) return;
  const m = S.media;
  if (!m.has || card.classList.contains('empty') || card.dataset.key !== m.key) { renderPage(false); return; }
  card.classList.toggle('paused', !m.playing);
  const play = card.querySelector('.play');
  const icon = m.playing ? 'pause' : 'play';
  if (play && play.dataset.icon !== icon) {
    play.dataset.icon = icon;
    play.innerHTML = ic(icon + ':fill', 24);
    if (fromUser) play.animate([{ transform: 'scale(.82)' }, { transform: 'scale(1.08)', offset: .55 }, { transform: 'scale(1)' }], { duration: 420, easing: 'cubic-bezier(.3,.7,.3,1)' });
  }
  if (fromUser) bounce(card);
  tickPage();
}

document.addEventListener('click', e => {
  if (suppressClick) { suppressClick = false; return; }
  const el = e.target.closest('[data-act]');
  if (el) { act(el.dataset.act, el, e); return; }
  const c = e.target.closest('.citem');
  if (c) {
    clipSel = +c.dataset.i;
    for (const x of document.querySelectorAll('.citem.sel')) x.classList.remove('sel');
    c.classList.add('sel');
    post({ t: 'clipCopy', i: clipSel });
    bounce(c);
    return;
  }
  if (mode === 'compact' && activity() === 'done') { open('timer'); return; }
  if (!e.target.closest('#wrap')) return;
  if (mode === 'compact') open();
  else if (mode === 'toast' && current) {
    const k = current.kind;
    clearTimeout(toastT); current = null;
    open(k === 'shot' ? 'home' : k === 'voice' ? 'note' : k === 'timer' ? 'timer' : 'home');
    if (k === 'timer') post({ t: 'timerAck' });
  }
});

document.addEventListener('dblclick', e => {
  const c = e.target.closest('.citem');
  if (c && !e.target.closest('[data-act]')) { post({ t: 'clipPaste', i: +c.dataset.i }); return; }
  const f = e.target.closest('.sitem');
  if (f && !e.target.closest('[data-act]')) post({ t: 'shelfOpen', id: f.dataset.file });
});

document.addEventListener('keydown', e => {
  if (e.key === 'Escape') {
    if (mode === 'panel') close();
    else if (mode === 'toast') { clearTimeout(toastT); nextToast(); }
  }
});

document.addEventListener('contextmenu', e => {
  e.preventDefault();
  if (mode === 'compact' && e.target.closest('#wrap')) { post({ t: 'pasteLast' }); pillPulse(); return; }
  // Media card (not its buttons / progress bar): download / find-on-YouTube menu.
  const card = e.target.closest('.media');
  if (mode === 'panel' && card && !card.classList.contains('empty') && !e.target.closest('.controls, .progress, .aout, button')) {
    ymenu.x = e.clientX; ymenu.y = e.clientY;
    showYMenu({ state: 'loading' });
    post({ t: 'mediaMenu' });
  }
});

// ------------------------------------------------------------------ media download menu
const ymenu = { x: 0, y: 0, data: null };
const QUALITY = { 4320: '8K', 2160: '4K', 1440: '2K', 1080: 'Full HD', 720: 'HD' };
function showYMenu(d) {
  const page = $('#page');
  $('#ymenu')?.remove();
  if (!page || mode !== 'panel' || tab !== 'home') return;
  ymenu.data = d;
  let body;
  if (d.state === 'loading') body = `<div class="yhead"><span class="spin">${ic('arrow-counter-clockwise', 15)}</span>Ищу видео…</div>`;
  else if (d.state === 'installing') body = `<div class="yhead"><span class="spin">${ic('arrow-counter-clockwise', 15)}</span>Устанавливаю yt-dlp и ffmpeg…</div><div class="ynote">Прогресс — в пилюле острова.</div>`;
  else if (d.state === 'search') body = `<button class="aopt" data-act="ytSearch" data-q="${esc(d.query)}">${ic('magnifying-glass', 17)}<span>Найти на YouTube</span></button>
      <div class="ynote">Скачивание доступно только для YouTube и SoundCloud.</div>`;
  else if (d.state === 'install') body = `<div class="yhead">${ic('download-simple', 16)}Скачивание видео</div>
      <div class="ynote">Нужны yt-dlp и ffmpeg (~115 МБ, официальные сборки с GitHub). Используйте для своего контента и того, что разрешено скачивать.</div>
      <button class="aopt primary" data-act="ytInstall">${ic('download-simple', 17)}<span>Установить</span></button>`;
  else if (d.state === 'busy') body = `<div class="yhead">${ic('download-simple', 16)}<span class="ell">${esc(d.title || 'Загрузка')}</span></div>
      <button class="aopt" data-act="ytCancel">${ic('x', 17)}<span>Отменить загрузку</span></button>`;
  else if (d.state === 'error') body = `<div class="ynote">${esc(d.message)}</div>`;
  else if (d.state === 'ready') body = `<div class="yhead" title="${esc(d.title)}">${ic('download-simple', 16)}<span class="ell">${d.searched ? 'Найдено: ' : ''}${esc(d.title)}</span></div>
      ${d.heights.map(h => `<button class="aopt" data-act="ytGet" data-q="${h}"><b class="q">${h}p</b><span>${QUALITY[h] || ''}</span>${ic('video-camera', 16)}</button>`).join('')}
      <button class="aopt" data-act="ytGet" data-q="audio"><b class="q">MP3</b><span>Только звук</span>${ic('music-notes', 16)}</button>`;
  const el = document.createElement('div');
  el.id = 'ymenu'; el.className = 'apop ymenu';
  el.innerHTML = body;
  page.appendChild(el);
  const p = page.getBoundingClientRect();
  el.style.left = Math.max(4, Math.min(ymenu.x - p.left, p.width - el.offsetWidth - 4)) + 'px';
  el.style.top = Math.max(4, Math.min(ymenu.y - p.top, p.height - el.offsetHeight - 4)) + 'px';
}
document.addEventListener('pointerdown', e => { if (!e.target.closest?.('#ymenu')) $('#ymenu')?.remove(); }, true);

// Pill gestures: wheel = volume, middle click = play/pause, right click = paste the last copied item.
let wheelAt = 0;
wrap.addEventListener('wheel', e => {
  if (mode !== 'compact' && !e.target.closest('.tabs')) return;
  e.preventDefault();
  const now = performance.now();
  if (now - wheelAt < 45) return;
  wheelAt = now;
  post({ t: 'volume', dir: e.deltaY < 0 ? 1 : -1 });
}, { passive: false });
wrap.addEventListener('auxclick', e => {
  if (e.button !== 1 || mode !== 'compact') return;
  e.preventDefault();
  post({ t: 'media', cmd: 'toggle' });
  if (S.media.has) { S.media.playing = !S.media.playing; mediaHold = { until: Date.now() + 2500, playing: S.media.playing, key: S.media.key }; renderCompact(); }
  pillPulse();
});
wrap.addEventListener('mousedown', e => { if (e.button === 1) e.preventDefault(); }); // no autoscroll cursor
function pillPulse() {
  shape.animate([{ transform: 'scale(1)' }, { transform: 'scale(.94)', offset: .3 }, { transform: 'scale(1.02)', offset: .65 }, { transform: 'scale(1)' }], { duration: 380, easing: 'ease-out' });
}

// ------------------------------------------------------------------ hover (the island reacts instantly, opens after a short dwell)

// Only real pointer movement counts: a window appearing under a stale cursor position must not open the panel.
wrap.addEventListener('pointermove', () => {
  if (hovering) return;
  hovering = true;
  clearTimeout(leaveT);
  if (mode === 'compact') {
    layout(HOVER);
    if (S.hoverOpen && !dragging) dwellT = setTimeout(() => { if (hovering && !dragging && mode === 'compact') open(); }, 110);
  } else if (mode === 'toast') { pauseToast(); layout(HOVER); }
});
wrap.addEventListener('pointerleave', () => {
  if (!hovering) return;
  hovering = false;
  clearTimeout(dwellT);
  if (mode === 'compact') layout(HOVER);
  else if (mode === 'toast') { resumeToast(); layout(HOVER); }
  else if (mode === 'panel') scheduleClose();
});

// ------------------------------------------------------------------ dragging the island / files out of the shelf

let down = null, fdown = null;
wrap.addEventListener('pointerdown', e => {
  if (e.button !== 0) return;
  const file = e.target.closest('.sitem, [data-drag]');
  if (file && !e.target.closest('[data-act]')) {
    fdown = { x: e.screenX, y: e.screenY, msg: { t: 'shelfDrag', ids: file.dataset.file ? [file.dataset.file] : S.shelf.map(f => f.id) } };
    return;
  }
  const clip = e.target.closest('.citem');
  if (clip && !e.target.closest('[data-act]')) { fdown = { x: e.screenX, y: e.screenY, msg: { t: 'clipDrag', i: +clip.dataset.i } }; return; }
  if (e.target.closest('button, input, textarea, a, .progress, .app, .strip, .clist, [data-act]')) return;
  down = { x: e.screenX, y: e.screenY };
  clearTimeout(dwellT);
});
window.addEventListener('pointermove', e => {
  if (fdown && Math.hypot(e.screenX - fdown.x, e.screenY - fdown.y) > 5) {
    fileDrag = true;
    suppressClick = true;
    setTimeout(() => { suppressClick = false; }, 400);
    post(fdown.msg);
    fdown = null;
    return;
  }
  if (!down || dragging) return;
  if (Math.hypot(e.screenX - down.x, e.screenY - down.y) > 5) {
    down = null;
    dragging = lifted = true;
    suppressClick = true;
    post({ t: 'dragStart' });
    layout(HOVER);
  }
});
window.addEventListener('pointerup', () => { down = null; fdown = null; });

// ------------------------------------------------------------------ dropping files in

let dropDepth = 0;
const hasFiles = e => [...(e.dataTransfer?.types || [])].includes('Files');
document.addEventListener('dragenter', e => {
  if (!hasFiles(e)) return;
  e.preventDefault();
  dropDepth++;
  fileDrag = true;
  clearTimeout(leaveT);
  if (mode !== 'panel' || tab !== 'home') open('home');
  const inDock = e.target.closest && e.target.closest('#dock');
  $('#dock')?.classList.toggle('drop', !!inDock);
  $('#strip')?.classList.toggle('drop', !inDock);
});
document.addEventListener('dragover', e => { if (hasFiles(e)) { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; } });
document.addEventListener('dragleave', () => {
  if (--dropDepth > 0) return;
  dropDepth = 0;
  endFileDrag();
  scheduleClose(500);
});
document.addEventListener('drop', e => {
  e.preventDefault();
  dropDepth = 0;
  const files = [...(e.dataTransfer?.files || [])];
  const inDock = e.target.closest && e.target.closest('#dock');
  endFileDrag();
  hovering = true;
  if (!files.length) return;
  if (inDock) post({ t: 'appDrop' }, files);
  else post({ t: 'shelfDrop' }, files);
});
function endFileDrag() {
  fileDrag = false;
  $('#strip')?.classList.remove('drop');
  $('#dock')?.classList.remove('drop');
}

// ------------------------------------------------------------------ native messages

function artColour(url) {
  if (!url) { S.artColor = '#ff5d8f'; return; }
  const img = new Image();
  img.onload = () => {
    const c = document.createElement('canvas'); c.width = c.height = 12;
    const g = c.getContext('2d'); g.drawImage(img, 0, 0, 12, 12);
    const d = g.getImageData(0, 0, 12, 12).data;
    let r = 0, gg = 0, b = 0, wsum = 0;
    for (let i = 0; i < d.length; i += 4) {
      const mx = Math.max(d[i], d[i + 1], d[i + 2]), mn = Math.min(d[i], d[i + 1], d[i + 2]);
      const w = (mx - mn) / 255 + .05;
      r += d[i] * w; gg += d[i + 1] * w; b += d[i + 2] * w; wsum += w;
    }
    r /= wsum; gg /= wsum; b /= wsum;
    // lift to a vivid, readable accent on black
    const mx = Math.max(r, gg, b, 1), k = 235 / mx;
    const mix = (v) => Math.round(Math.min(255, v * k * .85 + 255 * .15));
    S.artColor = `rgb(${mix(r)},${mix(gg)},${mix(b)})`;
    refresh('media');
  };
  img.src = url;
}

function applyPatch(k, v) {
  if (k === 'media') {
    if (v.art !== null && v.art !== undefined) { S.art = v.art; artColour(v.art); }
    if (!v.has) S.art = '';
    // ignore the stale status the player reports right after our own play/pause click
    if (mediaHold && Date.now() < mediaHold.until && v.key === mediaHold.key) {
      if (v.playing === mediaHold.playing) mediaHold = null; else v.playing = mediaHold.playing;
    }
    const sameTrack = S.media.key === v.key && S.media.has === v.has;
    S.media = v;
    if (sameTrack && mode === 'panel' && tab === 'home') { updateMedia(false); renderCompact(); return; }
  } else if (k === 'clips') {
    const top = v.items.find(c => !c.pinned) || v.items[0], prev = S.clips.items.find(c => !c.pinned) || S.clips.items[0];
    if (top && (!prev || (top.text ?? top.image) !== (prev.text ?? prev.image) || v.items.length > S.clips.items.length) && S.clips.items.length) lastClipAt = Date.now();
    S.clips = v;
  } else if (k === 'mediaPos') { S.media.pos = v.pos; S.media.at = v.at; return; }
  else S[k] = v;
  refresh(k);
}

// While the panel is open the pill is hidden: a running download shows as a chip in the tab bar.
function dlChip() {
  const chip = $('#dlchip');
  if (!chip) return;
  const d = S.dl;
  chip.classList.toggle('on', !!d);
  if (!d) return;
  chip.title = d.name || '';
  chip.innerHTML = `${ic('download-simple', 14)}<span class="num">${d.pct != null ? Math.floor(d.pct) + '%' : size(d.bytes || 0)}</span>` +
    (d.pct != null ? `<i style="transform:scaleX(${(d.pct / 100).toFixed(3)})"></i>` : '');
}

function refresh(k) {
  if (mode === 'compact' || k === 'shelf' || k === 'rec' || k === 'timer' || k === 'media' || k === 'dl') {
    const before = views.compact._html;
    renderCompact();
    if (mode === 'compact' && before !== views.compact._html) layout(CLOSE);
  }
  if (k === 'dl') dlChip();
  if (mode !== 'panel') { panelDirty = true; return; }
  if (k === 'shelf') refreshTabs();
  if (k === 'clips') { if (mode === 'panel') refreshTabs(); if (tab === 'clip') { renderClipList(); const sw = $('.cbar .switch'); if (sw) sw.classList.toggle('on', S.clips.history); } return; }
  const relevant = { home: ['media', 'apps', 'rec', 'shelf'], timer: ['timer'], note: ['note'] }[tab] || [];
  if (!relevant.includes(k)) return;
  if (typing()) return; // never rebuild a page under the user's caret
  const scroll = $('#page')?.scrollTop;
  renderPage(false);
  if (scroll) $('#page').scrollTop = scroll;
}

function onNative(m) {
  switch (m.t) {
    case 'patch': for (const k in m) if (k !== 't') applyPatch(k, m[k]); break;
    case 'geo': S.geo = m; layout(); break;
    case 'rates':
      try { FX.rates = JSON.parse(m.json); FX.at = m.at; FX.error = false; } catch { }
      if (mode === 'panel' && tab === 'fx') { if (FX.picker || typing()) fxFill(document.activeElement); else renderPage(false); }
      break;
    case 'lvl': pushLevel(m.v); break;
    case 'mediaMenu': if ($('#ymenu')) showYMenu(m.data); break;
    case 'audio': showAudio(m.list); break;
    case 'ratesError': FX.error = true; if (mode === 'panel' && tab === 'fx' && !typing() && !FX.picker) renderPage(false); break;
    case 'toast': showToast(m); break;
    case 'flash': flash(m.text); break;
    case 'toggle': mode === 'panel' ? close() : open(); break;
    case 'open': open(m.tab); if (m.tab === 'clip') setTimeout(() => $('#cq')?.focus(), 80); break;
    case 'close': pinned = false; close(); break;
    case 'blur': if (mode === 'panel' && !pinned && !hovering && !fileDrag) close(); break;
    case 'dragEnd':
      dragging = lifted = false;
      setTimeout(() => { suppressClick = false; }, 60);
      layout(SNAP);
      break;
    case 'settled': break;
    case 'dragDone': fileDrag = false; break;
    case 'vanish': hidden = true; stage.classList.add('gone'); if (mode === 'toast' && current) pauseToast(); layout(CLOSE); break;
    case 'appear':
      hidden = true; layout(CLOSE);
      for (const s of Object.values(sp)) s.set(s.t);
      hidden = false; stage.classList.remove('gone');
      layout([260, 20]);
      if (mode === 'toast' && current && !hovering) resumeToast();
      else if (mode === 'compact' && toasts.length) setTimeout(nextToast, 300);
      break;
  }
}

if (native) native.addEventListener('message', e => onNative(e.data));

// ------------------------------------------------------------------ boot

let tickN = 0;
setInterval(() => {
  const busy = mode === 'panel' || S.rec.state !== 'off' || S.timer.endsAt || S.timer.doneAt || hovering;
  if (!busy && ++tickN % 4) return;
  tickTexts();
  tickPage();
  if (mode === 'compact') {
    const w = Math.max(196, views.compact.offsetWidth);
    if (Math.abs(w + (hovering ? 16 : 0) - sp.w.t) > .5 && !dragging) layout(CLOSE);
  }
}, 250);

renderCompact();
switchView(true);
for (const s of Object.values(sp)) s.set(s.t);
layout();
for (const s of Object.values(sp)) s.set(s.t);
apply();

if (native) post({ t: 'ready' });
else demo();

function demo() {
  document.body.classList.add('demo');
  const c = document.createElement('canvas'); c.width = c.height = 120;
  const g = c.getContext('2d');
  const gr = g.createLinearGradient(0, 0, 0, 120); gr.addColorStop(0, '#ff7a59'); gr.addColorStop(.6, '#d6336c'); gr.addColorStop(1, '#3b1f5c');
  g.fillStyle = gr; g.fillRect(0, 0, 120, 120);
  g.fillStyle = '#ffd59e'; g.beginPath(); g.arc(60, 64, 26, 0, 7); g.fill();
  g.fillStyle = '#2a1745'; g.beginPath(); g.moveTo(0, 120); g.lineTo(38, 78); g.lineTo(70, 104); g.lineTo(96, 84); g.lineTo(120, 104); g.lineTo(120, 120); g.fill();
  onNative({ t: 'patch', battery: { has: true, pct: 82, charging: false }, shelf: [
    { id: 'a', name: 'Отчёт Q3.pdf', ext: 'pdf', size: 1887436 }, { id: 'b', name: 'logo-final.png', ext: 'png', size: 411648 },
    { id: 'c', name: 'заметки.txt', ext: 'txt', size: 2048 }] });
  onNative({ t: 'patch', media: { has: true, title: 'Midnight Drive', artist: 'Neon Harbor', source: 'Spotify', playing: true, pos: 85, dur: 214, at: Date.now(), art: c.toDataURL() } });
  const thumb = (c1, c2) => { const k = document.createElement('canvas'); k.width = k.height = 80; const x = k.getContext('2d'); const g2 = x.createLinearGradient(0, 0, 80, 80); g2.addColorStop(0, c1); g2.addColorStop(1, c2); x.fillStyle = g2; x.fillRect(0, 0, 80, 80); return k.toDataURL(); };
  onNative({ t: 'patch', clips: { history: true, items: [
    { i: 0, kind: 'Картинки', image: c.toDataURL(), pinned: true }, { i: 1, kind: 'Картинки', image: thumb('#3b82f6', '#a855f7') },
    { i: 2, kind: 'Картинки', image: thumb('#34d399', '#065f46') }, { i: 3, kind: 'Ссылки', text: 'https://github.com/exuupery/island' },
    { i: 4, kind: 'Текст', text: 'Напомни мне завтра позвонить поставщику и уточнить сроки поставки.' }, { i: 5, kind: 'Текст', text: 'Проверить звук перед записью' }] } });
  onNative({ t: 'patch', shelf: [{ id: 'a', name: 'Отчёт Q3.pdf', ext: 'pdf', size: 1887436 }, { id: 'b', name: 'logo-final.png', ext: 'png', size: 411648 },
    { id: 'c', name: 'заметки.txt', ext: 'txt', size: 2048 }, { id: 'd', name: 'Проекты', ext: 'folder', size: -1 }] });
  const q = location.search;
  if (q.includes('panel')) setTimeout(() => open(q.includes('clip') ? 'clip' : q.includes('timer') ? 'timer' : 'home'), 300);
  if (q.includes('toast')) setTimeout(() => onNative({ t: 'toast', kind: 'color', title: '#D97757', sub: 'rgb(217, 119, 87)', color: '#D97757', actions: [{ id: 'copy:#D97757', icon: 'copy' }], ms: 60000, extra: { recent: ['#8ea0ff', '#34d399', '#f5c542', '#1c1c1e'] } }), 300);
}
