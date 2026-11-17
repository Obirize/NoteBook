// The note editor, laid out like the Notes app: back, share and "…" in the top bar; checklist, camera, pin and
// new note in the bottom bar. Edits are saved at once and sent to the PC shortly after.
import { id } from './crypto.js';
import { T } from './lang.js';
import * as Checklist from './checklist.js';
import * as History from './history.js';
import { S, $, isEmpty, folderTitle } from './state.js';
import { run, toast, sheet, show, reveal, slide, autosize, fmt } from './ui.js';
import { touch, forget } from './store.js';
import { localSave, flushAll, cancelPendingSends, announcePurge } from './sync.js';
import { renderList, listBehind } from './list.js';
import { renderAttachments, knowStored, storedOf, shareFiles, shareAttachments, selectAttachments } from './attachments.js';

const bodyEl = $('body');

// The note is drawn whole before the screen slides in: the first frame must not show the last note's place or photos.
export async function openNote(n) {
  await knowStored();
  S.current = n; show('editor', 'push');
  $('backLabel').textContent = folderTitle();
  $('noteDate').textContent = fmt.full.format(new Date(n.Updated));
  $('title').value = n.Title; Checklist.setBody(bodyEl, n.Text); autosize($('title')); History.resetHistory();
  $('title').readOnly = n.Deleted; bodyEl.contentEditable = n.Deleted ? 'false' : 'true';
  selectAttachments(false);
  $('share').hidden = $('noteMore').hidden = n.Deleted;
  $('editorBar').hidden = n.Deleted; $('trashBar').hidden = !n.Deleted; $('trashNotice').hidden = !n.Deleted;
  updatePin(); page.scrollTop = 0;
  await renderAttachments(n, true);
}
function updatePin() { const n = S.current; $('pin').style.color = n?.Pinned ? 'var(--accent)' : 'var(--muted)'; $('pin').setAttribute('aria-label', n?.Pinned ? T('unpin') : T('pin')); }
// The list is brought up to date before it slides back in, so it shows the note as it now is.
export function closeEditor() { S.current = null; if (listBehind()) renderList(); show('list', 'pop'); }

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
const shareNote = () => { const n = S.current; if (n) shareAttachments(storedOf(n), n).catch(e => toast(e.message || T('failed'))); };

// ---------- wiring ----------
$('back').addEventListener('click', () => run(leaveEditor));
$('share').addEventListener('click', shareNote);
$('noteMore').addEventListener('click', () => { const n = S.current; if (!n) return; sheet(null, [
  { label: n.Archived ? T('unarchive') : T('archiveNote'), run: () => run(() => archiveNote(n, !n.Archived)) },
  ...(storedOf(n).length ? [{ label: T('selectAttachments'), run: () => selectAttachments(true) }, { label: T('saveAll', storedOf(n).length), run: () => shareAttachments(storedOf(n)).catch(e => toast(e.message || T('failed'))) }] : []),
  { label: T('delete'), danger: true, run: () => run(async () => { if (n.draft) { S.current = null; closeEditor(); } else await deleteNote(n); }) },
]); });
$('pin').addEventListener('click', () => run(async () => { const n = S.current; if (!n || n.Deleted) return; await commitDraft(n); n.Pinned = !n.Pinned; touch(n); await localSave(n, true); updatePin(); }));
$('composeNote').addEventListener('click', () => run(async () => { await leaveEditor(); await newNote(); }));
$('restore').addEventListener('click', () => run(() => restoreNote(S.current)));
$('purge').addEventListener('click', () => { const n = S.current; sheet(T('purgeConfirm'), [{ label: T('deletePermanently'), danger: true, run: () => run(() => purgeNote(n)) }]); });
$('title').addEventListener('input', () => { autosize($('title')); edited('Title', $('title').value); });
$('title').addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); bodyEl.focus(); if (bodyEl.firstChild) Checklist.placeCaret(bodyEl.firstChild); } });
const bodyChanged = () => { edited('Text', Checklist.getBody(bodyEl)); keepInSight(); };
// A change the app makes itself (a new line, a check box): kept as one undo step when it happens.
function ours(change) { const before = History.snapshot(bodyEl); if (!change()) return false; History.remember(bodyEl, 'other', before); bodyChanged(); return true; }
bodyEl.addEventListener('input', bodyChanged);
// Only plain text comes in; iOS would otherwise paste styled fragments into the note.
bodyEl.addEventListener('paste', e => { e.preventDefault(); History.remember(bodyEl, 'other'); document.execCommand('insertText', false, e.clipboardData.getData('text/plain')); });
bodyEl.addEventListener('keydown', e => {
  if (S.current?.Deleted) return;
  // Redo from a keyboard: the browser no longer offers it itself, since its undo is answered by the app.
  const key = e.key.toLowerCase();
  if ((e.ctrlKey || e.metaKey) && (key === 'y' || key === 'z' && e.shiftKey)) { e.preventDefault(); if (History.redo(bodyEl)) bodyChanged(); }
  else if (e.key === 'Enter' && !e.shiftKey && ours(() => Checklist.enter(bodyEl))) e.preventDefault();
  else if (e.key === 'Backspace' && ours(() => Checklist.backspace(bodyEl))) e.preventDefault();
});
// The on-screen keyboard reports a new line through beforeinput rather than a key; handle it the same way. Undo and
// redo (shaking the phone, or the keyboard's buttons) come this way too and are answered from the app's own list.
const KIND = { insertText: 'typing', insertCompositionText: 'typing', insertReplacementText: 'typing', deleteContentBackward: 'deleting', deleteContentForward: 'deleting' };
bodyEl.addEventListener('beforeinput', e => {
  if (S.current?.Deleted) return;
  if (e.inputType === 'historyUndo' || e.inputType === 'historyRedo') {
    if (!e.cancelable) return;
    e.preventDefault(); if ((e.inputType === 'historyUndo' ? History.undo : History.redo)(bodyEl)) bodyChanged();
  } else if (e.inputType === 'insertParagraph' || e.inputType === 'insertLineBreak') { if (ours(() => Checklist.enter(bodyEl))) e.preventDefault(); }
  else History.remember(bodyEl, KIND[e.inputType] || 'other');
});
// A tap on an item's circle flips it without opening the keyboard.
bodyEl.addEventListener('pointerdown', e => { if (!S.current?.Deleted && ours(() => Checklist.tapToggle(bodyEl, e))) e.preventDefault(); });
$('checklist').addEventListener('click', () => { if (!S.current || S.current.Deleted) return; bodyEl.focus(); ours(() => { Checklist.toggleSelection(bodyEl); return true; }); });
// While typing, "Done" replaces the top-bar icons, as in Notes.
const typing = () => document.activeElement === $('title') || document.activeElement === bodyEl;
for (const el of [$('title'), bodyEl]) {
  el.addEventListener('focus', () => { $('done').hidden = false; $('share').hidden = $('noteMore').hidden = true; });
  el.addEventListener('blur', () => setTimeout(() => { if (!typing()) { $('done').hidden = true; $('share').hidden = $('noteMore').hidden = !!S.current?.Deleted; } }, 50));
}
$('done').addEventListener('click', () => document.activeElement?.blur());
// The bottom bar follows the keyboard: the editor shrinks to the visible part of the screen while a field has focus.
// iOS also slides the whole page up when the tapped line would end under the keyboard, and the two moves fought: the
// note sat too low for a moment, then the tapped line stayed hidden until iOS scrolled to it a second later. So the
// caret's line is lifted above where the keyboard will reach as soon as the caret lands (the keyboard's height is
// remembered from last time), and kept in sight from then on, the way the Notes app does.
const page = $('editor').querySelector('.page'), GAP = 12;
let keyboard = 0, lifting = false;
try { keyboard = +localStorage.getItem('keyboard') || 0; } catch { }
function caretBottom() {
  if (document.activeElement === $('title')) return $('title').getBoundingClientRect().bottom;
  const sel = getSelection(); if (!sel?.rangeCount || !bodyEl.contains(sel.focusNode)) return null;
  const r = document.createRange(); r.setStart(sel.focusNode, sel.focusOffset);
  // An empty line has no caret box of its own; its block stands in for it.
  const box = r.getClientRects()[0] || Checklist.currentBlock(bodyEl)?.getBoundingClientRect();
  return box ? box.bottom : null;
}
// Scrolls the note just enough for the caret to sit a little above `limit` (a height on the screen).
function lift(limit) { const b = caretBottom(); if (b != null && b > limit - GAP) page.scrollTop += b - (limit - GAP); }
const keepInSight = () => { if ($('editor').classList.contains('keyboard')) lift(page.getBoundingClientRect().bottom); };
if (window.visualViewport) {
  const v = window.visualViewport, e = $('editor');
  // The screen's height without a keyboard. iOS keeps the page that tall under the keyboard; other phones shrink it.
  let full = window.innerHeight;
  const expected = () => keyboard || Math.round(full * 0.45);
  // With the keyboard up the home-indicator area is under the keyboard, so the bar drops its safe-area padding too.
  const fit = () => {
    if (!typing()) full = window.innerHeight;
    const up = typing() && !e.hidden && v.height < full - 80, was = e.classList.contains('keyboard');
    e.classList.toggle('keyboard', up);
    if (up) {
      e.style.top = v.offsetTop + 'px'; e.style.height = v.height + 'px';
      keyboard = full - v.height; try { localStorage.setItem('keyboard', keyboard); } catch { }
    } else { e.style.top = ''; e.style.height = ''; }
    if (up || !typing()) { page.style.paddingBottom = ''; lifting = false; }
    if (up && !was) keepInSight();
  };
  v.addEventListener('resize', fit); v.addEventListener('scroll', fit);
  for (const el of [$('title'), bodyEl]) {
    // Room below the last line, so even the note's last line can be lifted before the keyboard covers it.
    el.addEventListener('focus', () => { if (!e.classList.contains('keyboard')) { lifting = true; page.style.paddingBottom = expected() + 'px'; } fit(); });
    el.addEventListener('blur', fit);
  }
  // The caret lands a moment after the focus; that is when its line can be lifted.
  document.addEventListener('selectionchange', () => {
    if (!lifting || !typing()) return; lifting = false;
    lift(full - expected() - $('editorBar').offsetHeight);
  });
}
// Swiping in from the left edge of a note goes back, as in the Notes app: the note follows the finger with the list
// showing underneath, and on release slides the rest of the way, or back if the swipe was short and slow.
{
  const editor = $('editor'), list = $('list'); let startX = 0, startY = 0, dragging = false, moved = false, dx = 0, lastX = 0, lastT = 0, speed = 0;
  const place = () => { const p = Math.min(1, dx / editor.offsetWidth); editor.style.transform = `translateX(${dx}px)`; list.style.transform = `translateX(${-30 * (1 - p)}%)`; list.style.opacity = .6 + .4 * p; };
  const reset = () => { editor.style.transform = list.style.transform = list.style.opacity = ''; list.hidden = true; };
  editor.addEventListener('touchstart', e => { const t = e.touches[0]; dragging = t.clientX < 28; moved = false; startX = lastX = t.clientX; startY = t.clientY; lastT = e.timeStamp; dx = speed = 0; }, { passive: true });
  editor.addEventListener('touchmove', e => {
    if (!dragging) return; const t = e.touches[0];
    dx = Math.max(0, t.clientX - startX);
    if (Math.abs(t.clientY - startY) > 60 && dx < 30) { dragging = false; if (moved) reset(); return; }
    if (e.timeStamp > lastT) speed = (t.clientX - lastX) / (e.timeStamp - lastT); lastX = t.clientX; lastT = e.timeStamp;
    if (!moved && dx > 0) { moved = true; if (listBehind()) renderList(); reveal('list'); }
    if (moved) place();
  }, { passive: true });
  const release = () => {
    if (!dragging) return; dragging = false; if (!moved) return;
    const start = { top: { transform: editor.style.transform }, under: { transform: list.style.transform, opacity: +list.style.opacity } };
    if (dx > editor.offsetWidth * 0.4 || (speed > 0.4 && dx > 30)) slide(editor, list, false, () => { editor.hidden = true; run(leaveEditor); }, start);
    else slide(editor, list, true, () => { list.hidden = true; }, start);
  };
  editor.addEventListener('touchend', release); editor.addEventListener('touchcancel', release);
}
