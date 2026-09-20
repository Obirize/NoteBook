// Small interface helpers: one work queue so taps never race each other, toasts, the bottom action sheet,
// screen switching, and date formatting in the phone's language.
import { T, locale } from './lang.js';
import { $ } from './state.js';

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

export function show(screen) { for (const s of ['install', 'pair', 'list', 'editor']) $(s).hidden = s !== screen; }
export function setStatus(text, busy = false) { const s = $('syncStatus'); s.textContent = text; s.className = 'status' + (busy ? ' busy' : ''); }
export function autosize(el) { el.style.height = 'auto'; el.style.height = el.scrollHeight + 'px'; }

// Opened in Safari rather than from the Home Screen: explain the two taps that make it an app.
export let installSkipped = false; try { installSkipped = sessionStorage.getItem('skipInstall') === '1'; } catch { }
export function skipInstall() { installSkipped = true; try { sessionStorage.setItem('skipInstall', '1'); } catch { } }
export const needsInstall = () => !navigator.standalone && !installSkipped && /iPhone|iPad|iPod/.test(navigator.userAgent) && !window.matchMedia('(display-mode: standalone)').matches;

export const fmt = {
  time: new Intl.DateTimeFormat(locale, { hour: '2-digit', minute: '2-digit' }),
  day: new Intl.DateTimeFormat(locale, { weekday: 'long' }),
  date: new Intl.DateTimeFormat(locale, { day: 'numeric', month: 'short', year: 'numeric' }),
  month: new Intl.DateTimeFormat(locale, { month: 'long', year: 'numeric' }),
  full: new Intl.DateTimeFormat(locale, { day: 'numeric', month: 'long', year: 'numeric', hour: '2-digit', minute: '2-digit' }),
};
function startOfDay(d) { const x = new Date(d); x.setHours(0, 0, 0, 0); return x; }
const daysAgo = iso => (startOfDay(new Date()) - startOfDay(new Date(iso))) / 86400000;
export function dateLabel(iso) { const d = new Date(iso), diff = daysAgo(iso); if (diff < 1) return fmt.time.format(d); if (diff < 7) return fmt.day.format(d); return fmt.date.format(d); }
export function sectionOf(n) {
  if (n.Pinned && !n.Deleted) return T('pinned');
  const diff = daysAgo(n.Updated);
  if (diff < 1) return T('today'); if (diff < 2) return T('yesterday'); if (diff < 7) return T('previous7'); if (diff < 30) return T('previous30');
  return fmt.month.format(new Date(n.Updated));
}
