// The Folders screen and the note list, laid out like the Notes app: one card of rows (sections only for pinned
// notes), search, the note count in the bottom bar, select mode with bulk actions, and swipe actions on a row
// (share, move, delete; a long swipe fires the last one).
import { T, locale } from './lang.js';
import * as Checklist from './checklist.js';
import { decryptFile } from './crypto.js';
import { S, $, inFolder, inTrash, inArchive, folderTitle } from './state.js';
import { run, sheet, show, dateLabel, onStatus, thumbUrl, ICON } from './ui.js';
import { get } from './store.js';
import { openNote, newNote, deleteNote, restoreNote, archiveNote, purgeNote, bulk, shareText } from './editor.js';
import { syncNow } from './sync.js';

const thumbUrls = new Map();
let status = null, shown = 0;   // what the link is doing, and how many notes the list is showing

function renderFolders() {
  const counts = { all: 0, archive: 0, trash: 0 };
  for (const n of S.notes) counts[n.Deleted ? 'trash' : n.Archived ? 'archive' : 'all']++;
  for (const el of document.querySelectorAll('.folder-count')) el.textContent = counts[el.dataset.count];
}
export function renderList() {
  const q = $('search').value.trim().toLocaleLowerCase(locale), selecting = S.selecting, selected = S.selected;
  const list = S.notes.filter(n => inFolder(n) && (!q || (n.Title + ' ' + n.Text + ' ' + n.Attachments.map(a => a.Name).join(' ')).toLocaleLowerCase(locale).includes(q)))
    .sort((a, b) => Number(b.Pinned) - Number(a.Pinned) || Date.parse(b.Updated) - Date.parse(a.Updated));
  const title = folderTitle();
  $('listTitle').textContent = title;
  $('folderBack').hidden = selecting; $('edit').hidden = !inTrash() || selecting || list.length === 0; $('more').hidden = selecting || inTrash();
  $('selectDone').hidden = !selecting; $('trashInfo').hidden = !inTrash();
  $('list').classList.toggle('selecting', selecting);
  $('selectBar').hidden = !selecting; $('list').querySelector('.toolbar:not(#selectBar)').hidden = selecting;
  for (const id of [...selected]) if (!list.some(n => n.Id === id)) selected.delete(id);
  $('selectAll').textContent = selected.size === list.length && list.length > 0 ? T('deselectAll') : T('selectAll');
  $('selectDelete').disabled = $('selectRestore').disabled = $('selectArchive').disabled = selected.size === 0;
  $('selectRestore').hidden = !inTrash(); $('selectArchive').hidden = inTrash();
  $('selectDelete').textContent = inTrash() ? T('deletePermanently') : T('delete');
  $('selectArchive').textContent = inArchive() ? T('unarchive') : T('archiveNote');
  shown = list.length; renderCount();
  $('empty').hidden = list.length > 0; $('empty').textContent = q ? T('noResults') : inTrash() ? T('noDeleted') : inArchive() ? T('noArchived') : T('noNotes');
  const sections = $('sections'); sections.replaceChildren();
  // Only pinned notes get sections ("Pinned" and the folder's own name); otherwise the list is one card, as in Notes.
  const pinnedAny = !inTrash() && list.some(n => n.Pinned);
  let group = null, lastSection;
  for (const n of list) {
    const section = pinnedAny ? (n.Pinned ? T('pinned') : title) : null;
    if (section !== lastSection) { if (section) { const h = document.createElement('div'); h.className = 'section-title'; h.textContent = section; sections.append(h); } group = document.createElement('div'); group.className = 'group'; sections.append(group); lastSection = section; }
    group.append(row(n));
  }
}
// The bottom bar shows the count, or what the link is doing until the notes are up to date.
function renderCount() {
  const c = $('count');
  if (S.selecting) c.textContent = S.selected.size === 0 ? T('selectPrompt') : T('selectedCount', S.selected.size);
  else if (status && status.kind !== 'ok') c.textContent = status.text;
  else c.textContent = shown === 0 ? T('countNone') : T('countMany', shown);
  c.classList.toggle('busy', !S.selecting && status?.kind === 'busy');
}
function row(n) {
  const el = document.createElement('div'); el.className = 'row';
  const inner = document.createElement('div'); inner.className = 'row-inner';
  const check = document.createElement('div'); check.className = 'row-check'; check.innerHTML = '<svg viewBox="0 0 24 24"><path d="M5 12l5 5 9-10"/></svg>'; inner.append(check);
  if (S.selected.has(n.Id)) el.classList.add('checked');
  const text = document.createElement('div'); text.className = 'row-text';
  const title = document.createElement('div'); title.className = 'row-title'; title.textContent = n.Title.trim() || T('newNote');
  const sub = document.createElement('div'); sub.className = 'row-sub';
  const when = document.createElement('b'); when.textContent = dateLabel(n.Updated);
  // The first line of the body, as the Notes list shows it.
  const line = Checklist.preview(n.Text).split('\n').map(l => l.trim()).find(Boolean) || '';
  sub.append(when, document.createTextNode(line || (n.Attachments.length ? T('attachmentsCount', n.Attachments.length) : T('noText'))));
  text.append(title, sub); inner.append(text);
  const image = n.Attachments.find(a => a.MediaType.startsWith('image/')), clip = n.Attachments.find(a => a.Thumb);
  if (image) { const img = document.createElement('img'); img.className = 'row-thumb'; img.alt = ''; inner.append(img); thumbnail(image).then(url => { if (url) img.src = url; else img.remove(); }); }
  else if (clip) { const img = document.createElement('img'); img.className = 'row-thumb'; img.alt = ''; img.src = thumbUrl(clip.Id, clip.Thumb); inner.append(img); }
  el.append(inner);
  if (!S.selecting) swipe(el, inner, actionsFor(n));
  inner.addEventListener('click', () => {
    if (el.dataset.swiped) return;
    if (S.selecting) { if (S.selected.has(n.Id)) S.selected.delete(n.Id); else S.selected.add(n.Id); renderList(); return; }
    run(() => openNote(n));
  });
  return el;
}
// The swipe actions of the folder: share, move and delete in the lists; restore and delete for good in the trash.
function actionsFor(n) {
  if (inTrash()) return [
    { kind: 'folder', label: T('restore'), run: () => run(() => restoreNote(n)) },
    { kind: 'trash', label: T('deletePermanently'), run: () => sheet(T('purgeConfirm'), [{ label: T('deletePermanently'), danger: true, run: () => run(() => purgeNote(n)) }]) },
  ];
  return [
    { kind: 'share', label: T('saveShare'), run: () => shareText(n) },
    { kind: 'folder', label: inArchive() ? T('unarchive') : T('archiveNote'), run: () => run(() => archiveNote(n, !inArchive())) },
    { kind: 'trash', label: T('delete'), run: () => run(() => deleteNote(n)) },
  ];
}
// Swipe a row to the left to reveal the actions; past the halfway point the last one fires, like the Notes list.
// The buttons are drawn the first time a row is touched: a list of three hundred notes would otherwise build nine
// hundred of them on every keystroke in the search field, for the one row that is ever swiped.
function swipe(el, inner, actions) {
  const last = actions[actions.length - 1];
  let bar = null, width = 0, startX = 0, startY = 0, dx = 0, active = false, open = false;
  function buttons() {
    if (bar) return;
    bar = document.createElement('div'); bar.className = 'row-actions';
    for (const a of actions) { const b = document.createElement('button'); b.className = 'row-action ' + a.kind; b.setAttribute('aria-label', a.label); b.innerHTML = ICON[a.kind]; b.addEventListener('click', a.run); bar.append(b); }
    el.prepend(bar); width = bar.offsetWidth;
  }
  inner.addEventListener('touchstart', e => { buttons(); startX = e.touches[0].clientX; startY = e.touches[0].clientY; dx = open ? -width : 0; active = false; inner.style.transition = 'none'; }, { passive: true });
  inner.addEventListener('touchmove', e => {
    const mx = e.touches[0].clientX - startX, my = e.touches[0].clientY - startY;
    if (!active && Math.abs(mx) > 8 && Math.abs(mx) > Math.abs(my)) active = true;
    if (!active) return;
    dx = Math.max(-el.clientWidth, Math.min(0, (open ? -width : 0) + mx)); inner.style.transform = `translateX(${dx}px)`; el.dataset.swiped = '1'; el.classList.add('swiping');
  }, { passive: true });
  inner.addEventListener('touchend', () => {
    inner.style.transition = '';
    if (!active) { delete el.dataset.swiped; return; }
    if (dx < -el.clientWidth * 0.55) { inner.style.transform = 'translateX(-100%)'; setTimeout(last.run, 150); return; }
    open = dx < -width / 2; inner.style.transform = open ? `translateX(-${width}px)` : ''; if (!open) setTimeout(() => el.classList.remove('swiping'), 220); setTimeout(() => delete el.dataset.swiped, 50);
  });
}
async function thumbnail(a) {
  if (thumbUrls.has(a.Id)) return thumbUrls.get(a.Id);
  const encrypted = await get('files', a.Id); if (!encrypted) return null;
  try { const url = URL.createObjectURL(await decryptFile(encrypted, a)); thumbUrls.set(a.Id, url); return url; } catch { return null; }
}

// ---------- wiring ----------
function goto(folder) { S.folder = folder; S.selected.clear(); S.selecting = false; show('list'); renderList(); $('list').querySelector('.page').scrollTop = 0; }
function showFolders() { renderFolders(); show('folders'); }
const startSelect = () => { S.selecting = true; S.selected.clear(); renderList(); };
for (const b of document.querySelectorAll('.folder-row')) b.addEventListener('click', () => goto(b.dataset.folder));
$('folderBack').addEventListener('click', showFolders);
$('search').addEventListener('input', renderList);
for (const b of document.querySelectorAll('[data-compose]')) b.addEventListener('click', () => run(newNote));
$('edit').addEventListener('click', startSelect);
$('selectDone').addEventListener('click', () => { S.selecting = false; S.selected.clear(); renderList(); });
$('selectAll').addEventListener('click', () => { const ids = S.notes.filter(inFolder).map(n => n.Id); if (S.selected.size === ids.length && ids.length > 0) S.selected.clear(); else for (const id of ids) S.selected.add(id); renderList(); });
$('selectDelete').addEventListener('click', () => { if (inTrash()) sheet(T('purgeManyConfirm', S.selected.size), [{ label: T('deletePermanently'), danger: true, run: () => run(() => bulk('purge')) }]); else run(() => bulk('delete')); });
$('selectRestore').addEventListener('click', () => run(() => bulk('restore')));
$('selectArchive').addEventListener('click', () => run(() => bulk(inArchive() ? 'unarchive' : 'archive')));
$('more').addEventListener('click', () => sheet(null, [
  ...(shown ? [{ label: T('selectNotes'), run: startSelect }] : []),
  { label: T('syncNow'), run: () => run(syncNow) },
  { label: T('repair'), run: () => { show('pair'); $('pairCode').focus(); } },
]));
onStatus(s => { status = s; renderCount(); });
