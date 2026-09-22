// The note editor, laid out like the Notes app: back, share and "…" in the top bar; checklist, camera, pin and
// new note in the bottom bar. Edits are saved at once and sent to the PC shortly after.
import { id } from './crypto.js';
import { T } from './lang.js';
import * as Checklist from './checklist.js';
import { S, $, isEmpty, folderTitle } from './state.js';
import { run, toast, sheet, show, autosize, fmt } from './ui.js';
import { touch, forget } from './store.js';
import { localSave, flushAll, cancelPendingSends, announcePurge } from './sync.js';
import { renderList } from './list.js';
import { renderAttachments, noteFiles, shareFiles, selectAttachments } from './attachments.js';

const bodyEl = $('body');

export async function openNote(n) {
  S.current = n; show('editor');
  $('backLabel').textContent = folderTitle();
  $('noteDate').textContent = fmt.full.format(new Date(n.Updated));
  $('title').value = n.Title; Checklist.setBody(bodyEl, n.Text); autosize($('title'));
  $('title').readOnly = n.Deleted; bodyEl.contentEditable = n.Deleted ? 'false' : 'true';
  selectAttachments(false);
  $('share').hidden = $('noteMore').hidden = n.Deleted;
  $('editorBar').hidden = n.Deleted; $('trashBar').hidden = !n.Deleted; $('trashNotice').hidden = !n.Deleted;
  updatePin();
  await renderAttachments(n);
  $('editor').querySelector('.page').scrollTop = 0;
}
function updatePin() { const n = S.current; $('pin').style.color = n?.Pinned ? 'var(--accent)' : 'var(--muted)'; $('pin').setAttribute('aria-label', n?.Pinned ? T('unpin') : T('pin')); }
export function closeEditor() { S.current = null; $('editor').style.transform = ''; show('list'); renderList(); }

// A new note is only a draft until something is typed or attached; an untouched draft simply disappears on the way back.
export async function newNote() {
  const now = new Date().toISOString();
  const n = { Id: id(), Title: '', Text: '', Created: now, Updated: now, Pinned: false, Archived: false, Deleted: false, DeletedAt: null, Revision: 0, Attachments: [], draft: true };
  S.folder = 'all'; await openNote(n); $('title').focus();
}
export async function commitDraft(n) { if (!n.draft) return; delete n.draft; S.notes.push(n); }
async function leaveEditor() {
  const n = S.current;
  if (n) {
    cancelPendingSends();
    if (n.draft) S.current = null;
    else if (!n.Deleted && isEmpty(n)) { await purgeNote(n, true); return; }
    await flushAll();
  }
  closeEditor();
}
// Something changed in the open note: bump its revision, save, and let the PC know soon.
async function change(n, apply, message) { await commitDraft(n); apply(n); touch(n); await localSave(n, true); if (S.current === n) closeEditor(); else renderList(); if (message) toast(message); }
export const deleteNote = n => change(n, x => { x.Deleted = true; x.DeletedAt = new Date().toISOString(); }, T('movedToTrash'));
export const restoreNote = n => change(n, x => { x.Deleted = false; x.DeletedAt = null; x.Archived = false; }, T('restored'));
export async function archiveNote(n, archived) { if (n.draft && isEmpty(n)) return; await change(n, x => { x.Archived = archived; }, T(archived ? 'movedToArchive' : 'unarchived')); }
export async function purgeNote(n, quiet = false) { await announcePurge(n); await forget(n); if (S.current === n) closeEditor(); else renderList(); if (!quiet) toast(T('purged')); }
export async function bulk(action) {
  const targets = S.notes.filter(n => S.selected.has(n.Id)); if (targets.length === 0) return;
  for (const n of targets) {
    if (action === 'delete') { n.Deleted = true; n.DeletedAt = new Date().toISOString(); }
    else if (action === 'restore') { n.Deleted = false; n.DeletedAt = null; n.Archived = false; }
    else if (action === 'archive' || action === 'unarchive') n.Archived = action === 'archive';
    else if (action === 'purge') { await purgeNote(n, true); continue; }
    touch(n); await localSave(n, true);
  }
  S.selected.clear(); S.selecting = false; renderList();
  const key = { delete: 'movedManyToTrash', restore: 'restoredMany', archive: 'movedManyToArchive', unarchive: 'unarchivedMany', purge: 'purgedMany' }[action];
  toast(T(key, targets.length));
}
function edited(prop, value) { const n = S.current; run(async () => { if (!n || n.Deleted) return; await commitDraft(n); n[prop] = value; touch(n); $('noteDate').textContent = fmt.full.format(new Date(n.Updated)); await localSave(n); }); }
// The share sheet gets the note as text, plus its photos and videos when the phone can share files.
export function shareText(n, files = []) {
  const text = [n.Title.trim(), Checklist.preview(n.Text)].filter(Boolean).join('\n\n');
  const data = files.length && navigator.canShare?.({ files }) ? { title: n.Title, text, files } : { title: n.Title, text };
  if (navigator.share) navigator.share(data).catch(() => {}); else shareFiles(files);
}
const shareNote = () => { if (S.current) shareText(S.current, [...noteFiles.values()].map(f => f.file)); };

// ---------- wiring ----------
$('back').addEventListener('click', () => run(leaveEditor));
$('share').addEventListener('click', shareNote);
$('noteMore').addEventListener('click', () => { const n = S.current; if (!n) return; sheet(null, [
  { label: n.Archived ? T('unarchive') : T('archiveNote'), run: () => run(() => archiveNote(n, !n.Archived)) },
  ...(noteFiles.size ? [{ label: T('selectAttachments'), run: () => selectAttachments(true) }, { label: T('saveAll', noteFiles.size), run: () => shareFiles([...noteFiles.values()].map(f => f.file)) }] : []),
  { label: T('delete'), danger: true, run: () => run(async () => { if (n.draft) { S.current = null; closeEditor(); } else await deleteNote(n); }) },
]); });
$('pin').addEventListener('click', () => run(async () => { const n = S.current; if (!n || n.Deleted) return; await commitDraft(n); n.Pinned = !n.Pinned; touch(n); await localSave(n, true); updatePin(); }));
$('composeNote').addEventListener('click', () => run(async () => { await leaveEditor(); await newNote(); }));
$('restore').addEventListener('click', () => run(() => restoreNote(S.current)));
$('purge').addEventListener('click', () => { const n = S.current; sheet(T('purgeConfirm'), [{ label: T('deletePermanently'), danger: true, run: () => run(() => purgeNote(n)) }]); });
$('title').addEventListener('input', () => { autosize($('title')); edited('Title', $('title').value); });
$('title').addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); bodyEl.focus(); if (bodyEl.firstChild) Checklist.placeCaret(bodyEl.firstChild); } });
bodyEl.addEventListener('input', () => edited('Text', Checklist.getBody(bodyEl)));
// Only plain text comes in; iOS would otherwise paste styled fragments into the note.
bodyEl.addEventListener('paste', e => { e.preventDefault(); document.execCommand('insertText', false, e.clipboardData.getData('text/plain')); });
bodyEl.addEventListener('keydown', e => {
  if (S.current?.Deleted) return;
  if (e.key === 'Enter' && !e.shiftKey && Checklist.enter(bodyEl)) { e.preventDefault(); edited('Text', Checklist.getBody(bodyEl)); }
  else if (e.key === 'Backspace' && Checklist.backspace(bodyEl)) { e.preventDefault(); edited('Text', Checklist.getBody(bodyEl)); }
});
// The on-screen keyboard reports a new line through beforeinput rather than a key; handle it the same way.
bodyEl.addEventListener('beforeinput', e => { if (S.current?.Deleted) return; if ((e.inputType === 'insertParagraph' || e.inputType === 'insertLineBreak') && Checklist.enter(bodyEl)) { e.preventDefault(); edited('Text', Checklist.getBody(bodyEl)); } });
// A tap on an item's circle flips it without opening the keyboard.
bodyEl.addEventListener('pointerdown', e => { if (!S.current?.Deleted && Checklist.tapToggle(bodyEl, e)) { e.preventDefault(); edited('Text', Checklist.getBody(bodyEl)); } });
$('checklist').addEventListener('click', () => { if (!S.current || S.current.Deleted) return; bodyEl.focus(); Checklist.toggleSelection(bodyEl); edited('Text', Checklist.getBody(bodyEl)); });
// While typing, "Done" replaces the top-bar icons, as in Notes.
const typing = () => document.activeElement === $('title') || document.activeElement === bodyEl;
for (const el of [$('title'), bodyEl]) {
  el.addEventListener('focus', () => { $('done').hidden = false; $('share').hidden = $('noteMore').hidden = true; });
  el.addEventListener('blur', () => setTimeout(() => { if (!typing()) { $('done').hidden = true; $('share').hidden = $('noteMore').hidden = !!S.current?.Deleted; } }, 50));
}
$('done').addEventListener('click', () => document.activeElement?.blur());
// The bottom bar follows the keyboard: the editor shrinks to the visible part of the screen while a field has focus.
if (window.visualViewport) {
  // With the keyboard up the home-indicator area is under the keyboard, so the bar drops its safe-area padding too.
  const fit = () => { const v = window.visualViewport, e = $('editor'); const up = typing() && !e.hidden && v.height < window.innerHeight - 80; e.classList.toggle('keyboard', up); if (up) { e.style.top = v.offsetTop + 'px'; e.style.height = v.height + 'px'; } else { e.style.top = ''; e.style.height = ''; } };
  window.visualViewport.addEventListener('resize', fit); window.visualViewport.addEventListener('scroll', fit);
  for (const el of [$('title'), bodyEl]) { el.addEventListener('focus', () => setTimeout(fit, 50)); el.addEventListener('blur', () => setTimeout(fit, 80)); }
}
// Swiping in from the left edge of a note goes back, as in the Notes app.
{
  const editor = $('editor'); let startX = 0, startY = 0, dragging = false, dx = 0;
  editor.addEventListener('touchstart', e => { const t = e.touches[0]; dragging = t.clientX < 28; startX = t.clientX; startY = t.clientY; dx = 0; }, { passive: true });
  editor.addEventListener('touchmove', e => { if (!dragging) return; const t = e.touches[0]; dx = Math.max(0, t.clientX - startX); if (Math.abs(t.clientY - startY) > 60 && dx < 30) { dragging = false; editor.classList.remove('dragging'); editor.style.transform = ''; return; } editor.classList.add('dragging'); editor.style.transform = `translateX(${dx}px)`; }, { passive: true });
  editor.addEventListener('touchend', () => { if (!dragging) return; dragging = false; editor.classList.remove('dragging'); if (dx > 90) { editor.style.transform = 'translateX(100%)'; setTimeout(() => run(leaveEditor), 120); } else editor.style.transform = ''; });
}
