// Small interface helpers: one work queue so taps never race each other, toasts, the bottom action sheet,
// screen switching, and date formatting in the phone's language.
import { T, locale } from './lang.js';
import { $ } from './state.js';
import { un64 } from './crypto.js';

let queue = Promise.resolve();
export function run(fn) { queue = queue.then(fn).catch(e => { console.error(e); toast(e.message || T('failed')); }); return queue; }

let toastTimer;
export function toast(text) { const t = $('toast'); t.textContent = text; t.hidden = false; clearTimeout(toastTimer); toastTimer = setTimeout(() => t.hidden = true, 3200); }

export function sheet(title, actions) {
  const s = $('sheet'), body = s.querySelector('.sheet-body'); body.replaceChildren();
  if (title) { const t = document.createElement('div'); t.className = 'sheet-title'; t.textContent = title; body.append(t); }
  for (const a of actions) { const b = document.createElement('button'); b.textContent = a.label; if (a.danger) b.className = 'danger'; b.addEventListener('click', () => { s.hidden = true; a.run(); }); body.append(b); }
  s.hidden = false;
}
$('sheet').querySelector('.sheet-cancel').addEventListener('click', () => $('sheet').hidden = true);
$('sheet').addEventListener('click', e => { if (e.target === $('sheet')) $('sheet').hidden = true; });

export function show(screen) { for (const s of ['install', 'pair', 'folders', 'list', 'editor']) $(s).hidden = s !== screen; }
// What the link is doing, for whoever shows it (the list's bottom bar). One listener, wired by the module that draws it.
let statusListener = null;
export function onStatus(fn) { statusListener = fn; }
export function setStatus(text, kind = 'ok') { statusListener?.({ text, kind }); }
export function autosize(el) { el.style.height = 'auto'; el.style.height = el.scrollHeight + 'px'; }

// The drawings the app reuses outside the static markup, and the preview picture of a video: the note carries it as
// base64, and one object URL per attachment lets the browser keep the decoded picture between renders.
export const ICON = {
  share: '<svg viewBox="0 0 24 24"><path d="M12 3v13M7 8l5-5 5 5"/><path d="M5 12v8h14v-8"/></svg>',
  folder: '<svg viewBox="0 0 24 24"><path d="M3 7.5V6a1 1 0 0 1 1-1h5.2l2 2H20a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1z"/></svg>',
  trash: '<svg viewBox="0 0 24 24"><path d="M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13M10 11v6M14 11v6"/></svg>',
  close: '<svg viewBox="0 0 24 24"><path d="M6 6l12 12M18 6L6 18"/></svg>',
};
const thumbUrls = new Map();
export function thumbUrl(id, base64) {
  // A picture that cannot be read is worth no picture; it must not take the screen that was drawing it down with it.
  if (!thumbUrls.has(id)) {
    try { thumbUrls.set(id, URL.createObjectURL(new Blob([un64(base64)], { type: 'image/jpeg' }))); }
    catch { thumbUrls.set(id, null); }
  }
  return thumbUrls.get(id);
}

// Opened in Safari rather than from the Home Screen: explain the two taps that make it an app.
export let installSkipped = false; try { installSkipped = sessionStorage.getItem('skipInstall') === '1'; } catch { }
export function skipInstall() { installSkipped = true; try { sessionStorage.setItem('skipInstall', '1'); } catch { } }
export const needsInstall = () => !navigator.standalone && !installSkipped && /iPhone|iPad|iPod/.test(navigator.userAgent) && !window.matchMedia('(display-mode: standalone)').matches;

export const fmt = {
  time: new Intl.DateTimeFormat(locale, { hour: '2-digit', minute: '2-digit' }),
  day: new Intl.DateTimeFormat(locale, { weekday: 'long' }),
  date: new Intl.DateTimeFormat(locale, { dateStyle: 'short' }),
  full: new Intl.DateTimeFormat(locale, { day: 'numeric', month: 'long', year: 'numeric', hour: '2-digit', minute: '2-digit' }),
};
function startOfDay(d) { const x = new Date(d); x.setHours(0, 0, 0, 0); return x; }
const daysAgo = iso => (startOfDay(new Date()) - startOfDay(new Date(iso))) / 86400000;
export function dateLabel(iso) { const d = new Date(iso), diff = daysAgo(iso); if (diff < 1) return fmt.time.format(d); if (diff < 7) return fmt.day.format(d); return fmt.date.format(d); }
