// The note list: grouped by date like the Notes app, with search, the three folders (notes, archive, recently
// deleted), select mode with bulk actions, and a swipe on a row for the folder's own action.
import { T, locale } from './lang.js';
import * as Checklist from './checklist.js';
import { decryptFile } from './crypto.js';
import { S, $, inFolder, inTrash, inArchive, folderTitle } from './state.js';
import { run, sheet, show, dateLabel, sectionOf } from './ui.js';
import { get } from './store.js';
import { openNote, newNote, deleteNote, restoreNote, archiveNote, bulk } from './editor.js';
import { syncNow } from './sync.js';

const thumbUrls = new Map();

export function renderList() {
  const q = $('search').value.trim().toLocaleLowerCase(locale), selecting = S.selecting, selected = S.selected;
  const shown = S.notes.filter(n => inFolder(n) && (!q || (n.Title + ' ' + n.Text + ' ' + n.Attachments.map(a => a.Name).join(' ')).toLocaleLowerCase(locale).includes(q)))
    .sort((a, b) => Number(b.Pinned) - Number(a.Pinned) || Date.parse(b.Updated) - Date.parse(a.Updated));
  const inSub = inTrash() || inArchive();
  $('listTitle').textContent = folderTitle();
  $('folderBack').hidden = !inSub || selecting; $('trashLink').hidden = $('archiveLink').hidden = inSub || selecting; $('compose').hidden = inSub;
  $('select').hidden = selecting || shown.length === 0; $('selectDone').hidden = !selecting; $('more').hidden = selecting;
  $('list').classList.toggle('selecting', selecting);
  $('selectBar').hidden = !selecting; $('list').querySelector('.toolbar:not(#selectBar)').hidden = selecting;
  for (const id of [...selected]) if (!shown.some(n => n.Id === id)) selected.delete(id);
  $('selectAll').textContent = selected.size === shown.length && shown.length > 0 ? T('deselectAll') : T('selectAll');
  $('selectDelete').disabled = $('selectRestore').disabled = $('selectArchive').disabled = selected.size === 0;
  $('selectRestore').hidden = !inTrash(); $('selectArchive').hidden = inTrash();
  $('selectDelete').textContent = inTrash() ? T('deletePermanently') : T('delete');
  $('selectArchive').textContent = inArchive() ? T('unarchive') : T('archiveNote');
  $('count').textContent = selecting ? (selected.size === 0 ? T('selectPrompt') : T('selectedCount', selected.size)) : shown.length === 0 ? T('countNone') : T('countMany', shown.length);
  $('empty').hidden = shown.length > 0; $('empty').textContent = q ? T('noResults') : inTrash() ? T('noDeleted') : inArchive() ? T('noArchived') : T('noNotes');
  const sections = $('sections'); sections.replaceChildren();
  let group = null, lastSection;
  for (const n of shown) {
    const section = inTrash() ? null : sectionOf(n);
    if (section !== lastSection) { if (section) { const h = document.createElement('div'); h.className = 'section-title'; h.textContent = section; sections.append(h); } group = document.createElement('div'); group.className = 'group'; sections.append(group); lastSection = section; }
    group.append(row(n));
  }
}
function row(n) {
  const el = document.createElement('div'); el.className = 'row';
  // The swipe action of the folder: delete in the main list, restore in the trash, unarchive in the archive.
  const action = document.createElement('div'); action.className = 'row-action' + (inTrash() ? ' restore' : inArchive() ? ' archive' : ''); action.textContent = inTrash() ? T('restore') : inArchive() ? T('unarchive') : T('delete');
  const inner = document.createElement('div'); inner.className = 'row-inner';
  const check = document.createElement('div'); check.className = 'row-check'; check.innerHTML = '<svg viewBox="0 0 24 24"><path d="M5 12l5 5 9-10"/></svg>'; inner.append(check);
  if (S.selected.has(n.Id)) el.classList.add('checked');
  const text = document.createElement('div'); text.className = 'row-text';
  const title = document.createElement('div'); title.className = 'row-title'; title.textContent = n.Title.trim() || T('newNote');
  const sub = document.createElement('div'); sub.className = 'row-sub';
  const when = document.createElement('b'); when.textContent = dateLabel(n.Updated);
  sub.append(when, document.createTextNode(Checklist.preview(n.Text).replace(/\s+/g, ' ').trim() || (n.Attachments.length ? T('attachmentsCount', n.Attachments.length) : T('noText'))));
  text.append(title, sub); inner.append(text);
  if (n.Pinned && inTrash()) inner.append(pinIcon());
  const image = n.Attachments.find(a => a.MediaType.startsWith('image/')), clip = n.Attachments.find(a => a.Thumb);
  if (image) { const img = document.createElement('img'); img.className = 'row-thumb'; img.alt = ''; inner.append(img); thumbnail(image).then(url => { if (url) img.src = url; else img.remove(); }); }
  else if (clip) { const img = document.createElement('img'); img.className = 'row-thumb'; img.alt = ''; img.src = 'data:image/jpeg;base64,' + clip.Thumb; inner.append(img); }
  el.append(action, inner);
  if (!S.selecting) swipe(el, inner, () => run(() => inTrash() ? restoreNote(n) : inArchive() ? archiveNote(n, false) : deleteNote(n)));
  inner.addEventListener('click', () => {
    if (el.dataset.swiped) return;
    if (S.selecting) { if (S.selected.has(n.Id)) S.selected.delete(n.Id); else S.selected.add(n.Id); renderList(); return; }
    run(() => openNote(n));
  });
  return el;
}
function pinIcon() { const s = document.createElementNS('http://www.w3.org/2000/svg', 'svg'); s.setAttribute('viewBox', '0 0 24 24'); s.setAttribute('class', 'row-pin'); s.innerHTML = '<path d="M9 3h6l-1 6 4 3v2H6v-2l4-3z"/><path d="M12 14v7"/>'; return s; }
// Swipe a row to the left to reveal (and, past the halfway point, trigger) the action, like the Notes list.
function swipe(el, inner, action) {
  let startX = 0, startY = 0, dx = 0, active = false, open = false;
  inner.addEventListener('touchstart', e => { startX = e.touches[0].clientX; startY = e.touches[0].clientY; dx = open ? -88 : 0; active = false; inner.style.transition = 'none'; }, { passive: true });
  inner.addEventListener('touchmove', e => {
    const mx = e.touches[0].clientX - startX, my = e.touches[0].clientY - startY;
    if (!active && Math.abs(mx) > 8 && Math.abs(mx) > Math.abs(my)) active = true;
    if (!active) return;
    dx = Math.max(-160, Math.min(0, (open ? -88 : 0) + mx)); inner.style.transform = `translateX(${dx}px)`; el.dataset.swiped = '1'; el.classList.add('swiping');
  }, { passive: true });
  inner.addEventListener('touchend', () => {
    inner.style.transition = '';
    if (!active) { delete el.dataset.swiped; return; }
    if (dx < -130) { inner.style.transform = 'translateX(-100%)'; setTimeout(action, 150); return; }
    open = dx < -44; inner.style.transform = open ? 'translateX(-88px)' : ''; if (!open) setTimeout(() => el.classList.remove('swiping'), 220); setTimeout(() => delete el.dataset.swiped, 50);
  });
  el.querySelector('.row-action').addEventListener('click', action);
}
async function thumbnail(a) {
  if (thumbUrls.has(a.Id)) return thumbUrls.get(a.Id);
  const encrypted = await get('files', a.Id); if (!encrypted) return null;
  try { const url = URL.createObjectURL(await decryptFile(encrypted, a)); thumbUrls.set(a.Id, url); return url; } catch { return null; }
}

// ---------- wiring ----------
function goto(folder) { S.folder = folder; S.selected.clear(); renderList(); }
$('search').addEventListener('input', renderList);
$('compose').addEventListener('click', () => run(newNote));
$('trashLink').addEventListener('click', () => goto('trash'));
$('archiveLink').addEventListener('click', () => goto('archive'));
$('folderBack').addEventListener('click', () => goto('all'));
$('select').addEventListener('click', () => { S.selecting = true; S.selected.clear(); renderList(); });
$('selectDone').addEventListener('click', () => { S.selecting = false; S.selected.clear(); renderList(); });
$('selectAll').addEventListener('click', () => { const ids = S.notes.filter(inFolder).map(n => n.Id); if (S.selected.size === ids.length && ids.length > 0) S.selected.clear(); else for (const id of ids) S.selected.add(id); renderList(); });
$('selectDelete').addEventListener('click', () => { if (inTrash()) sheet(T('purgeManyConfirm', S.selected.size), [{ label: T('deletePermanently'), danger: true, run: () => run(() => bulk('purge')) }]); else run(() => bulk('delete')); });
$('selectRestore').addEventListener('click', () => run(() => bulk('restore')));
$('selectArchive').addEventListener('click', () => run(() => bulk(inArchive() ? 'unarchive' : 'archive')));
$('more').addEventListener('click', () => sheet(null, [
  { label: T('syncNow'), run: () => run(syncNow) },
  { label: T('repair'), run: () => { show('pair'); $('pairCode').focus(); } },
]));
