// Small interface helpers: one work queue so taps never race each other, toasts, the bottom action sheet,
// screen switching, and date formatting in the phone's language.
import { T, locale } from './lang.js';
import { $ } from './state.js';
import { un64 } from './crypto.js';

let queue = Promise.resolve();
export function run(fn) { queue = queue.then(fn).catch(e => { console.error(e); toast(e.message || T('failed')); }); return queue; }

// Motion follows the Notes app: screens slide, the action sheet rises, toasts fade. Phones set to reduce motion
// get the same screens without the movement.
export const calm = () => matchMedia('(prefers-reduced-motion: reduce)').matches;
export const EASE = 'cubic-bezier(.32,.72,0,1)';

let toastTimer;
export function toast(text) {
  const t = $('toast'); t.textContent = text; t.classList.remove('leaving'); t.hidden = false; clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { t.classList.add('leaving'); toastTimer = setTimeout(() => { t.hidden = true; t.classList.remove('leaving'); }, 200); }, 3200);
}

// The sheet slides down before it hides; an action that opens a new sheet straight away keeps it open.
let sheetTimer;
export function closeSheet() {
  const s = $('sheet'); if (s.hidden || s.classList.contains('closing')) return;
  if (calm()) { s.hidden = true; return; }
  s.classList.add('closing'); sheetTimer = setTimeout(() => { s.hidden = true; s.classList.remove('closing'); }, 200);
}
export function sheet(title, actions) {
  const s = $('sheet'), body = s.querySelector('.sheet-body'); body.replaceChildren();
  clearTimeout(sheetTimer); s.classList.remove('closing');
  if (title) { const t = document.createElement('div'); t.className = 'sheet-title'; t.textContent = title; body.append(t); }
  for (const a of actions) { const b = document.createElement('button'); b.textContent = a.label; if (a.danger) b.className = 'danger'; b.addEventListener('click', () => { closeSheet(); a.run(); }); body.append(b); }
  s.hidden = false;
}
$('sheet').querySelector('.sheet-cancel').addEventListener('click', closeSheet);
$('sheet').addEventListener('click', e => { if (e.target === $('sheet')) closeSheet(); });

// Screens. 'push' slides the new screen in from the right over the old one, which drifts left and dims; 'pop' is
// the way back. Without a direction the switch is instant. The list's place is kept while it is out of sight.
const SCREENS = ['install', 'pair', 'folders', 'list', 'editor'];
const UNDER = [{ transform: 'translateX(-30%)', opacity: .6 }, { transform: 'none', opacity: 1 }];
const OVER = [{ transform: 'translateX(100%)' }, { transform: 'none' }];
let moving = null;   // the slide under way, so a new switch can finish it at once
export function show(screen, how) {
  moving?.finish();
  const from = SCREENS.find(s => s !== screen && !$(s).hidden), target = $(screen);
  reveal(screen);
  const settle = () => { for (const s of SCREENS) if (s !== screen && !$(s).hidden) { const p = $(s).querySelector('.page'); if (p) p.dataset.top = p.scrollTop; $(s).hidden = true; } };
  if (!how || !from || calm()) { settle(); return; }
  slide(how === 'push' ? target : $(from), how === 'push' ? $(from) : target, how === 'push', settle);
}
// Unhides a screen where it was left: a page that was out of sight may come back scrolled to the top otherwise.
export function reveal(screen) {
  const target = $(screen), page = target.querySelector('.page'); if (!target.hidden) return;
  target.hidden = false; if (page?.dataset.top) page.scrollTop = +page.dataset.top;
}
// The animation itself, also used to finish a swipe the finger started (start = where the finger left both screens):
// top moves between off-screen and in place, under between drifted-and-dimmed and in place.
export function slide(top, under, entering, done, start = null) {
  const opts = { duration: 380, easing: EASE, fill: 'forwards' };
  const a = top.animate([start?.top ?? OVER[entering ? 0 : 1], OVER[entering ? 1 : 0]], opts);
  const b = under.animate([start?.under ?? UNDER[entering ? 1 : 0], UNDER[entering ? 0 : 1]], opts);
  top.style.transform = under.style.transform = under.style.opacity = '';
  const m = { finish: () => { if (moving !== m) return; moving = null; done(); a.cancel(); b.cancel(); } };
  moving = m; a.finished.then(m.finish, () => {});
}
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
