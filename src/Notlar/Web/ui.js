// Small interface helpers: one work queue so taps never race each other, toasts, the bottom action sheet,
// screen switching, and date formatting in the phone's language.
import { T, locale } from './lang.js';
import { S, $ } from './state.js';

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
// What the link is doing, shown in the list's bottom bar in place of the note count until the notes are up to date.
export function setStatus(text, kind = 'ok') { S.status = { text, kind }; document.dispatchEvent(new Event('status')); }
export function autosize(el) { el.style.height = 'auto'; el.style.height = el.scrollHeight + 'px'; }

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
// Only pinned notes get sections ("Pinned" and the folder's own name); otherwise the list is one card, as in Notes.
export function sectionOf(n, pinnedAny, folder) { return pinnedAny ? (n.Pinned ? T('pinned') : folder) : null; }
