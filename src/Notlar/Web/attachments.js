// Photos and videos on a note: a compact grid of square tiles (a tap opens the full-screen viewer, where saving,
// sharing and removing live), a select mode for saving or removing several at once, and adding new ones from
// the camera roll. Files are encrypted before they touch storage; decrypted copies exist only in memory, and only
// for what is on screen: photos for their tiles, one after another; a video only while the viewer shows it (nine
// long videos opened together would be more than the phone lets the app hold).
import { encryptFile, decryptFile } from './crypto.js';
import { ICON, thumbUrl } from './ui.js';
import { T } from './lang.js';
import { S, $, MAX_FILE } from './state.js';
import { run, toast, sheet } from './ui.js';
import { put, touch, persist, storedIds, storedFile } from './store.js';
import { localSave, send, manifest, isReady } from './sync.js';
import { commitDraft, shareText } from './editor.js';

const noteFiles = new Map();         // attachment id -> { url, file }: the open note's photos, decrypted for their tiles
const noteUrls = [];
const picked = new Set();            // attachment ids checked in select mode
let selecting = false, stored = new Set(), drawing = 0, viewing = 0;
const THUMB_LIMIT = 100 * 1048576, SHARE_LIMIT = 200 * 1048576;
// The attachments of a note that are on this phone (the rest are still to come from the PC).
export const storedOf = n => n.Attachments.filter(a => stored.has(a.Id));
const quietly = p => p.catch(e => { console.error(e); toast(e.message || T('failed')); });

export async function renderAttachments(n) {
  const turn = ++drawing;
  for (const url of noteUrls) URL.revokeObjectURL(url); noteUrls.length = 0; noteFiles.clear();
  stored = await storedIds();
  for (const id of [...picked]) if (!n.Attachments.some(a => a.Id === id)) picked.delete(id);
  const box = $('attachments'); box.replaceChildren(); box.classList.toggle('selecting', selecting);
  const later = [];                  // pictures to decrypt, one at a time once the grid is drawn
  n.Attachments.forEach((a, index) => {
    const tile = document.createElement('button'); tile.className = 'tile' + (picked.has(a.Id) ? ' picked' : ''); tile.type = 'button'; tile.setAttribute('aria-label', a.Name);
    const badge = document.createElement('span'); badge.className = 'tile-badge';
    badge.innerHTML = a.MediaType.startsWith('video/') ? '<svg viewBox="0 0 24 24"><path d="M8 5l11 7-11 7z"/></svg>' : a.MediaType.startsWith('image/') ? '' : '<svg viewBox="0 0 24 24"><path d="M6 3h8l5 5v13H6z"/><path d="M14 3v5h5"/></svg>';
    const check = document.createElement('span'); check.className = 'tile-check'; check.innerHTML = '<svg viewBox="0 0 24 24"><path d="M5 12l5 5 9-10"/></svg>';
    const pending = document.createElement('span'); pending.className = 'tile-pending'; pending.textContent = T('fromPc');
    tile.append(pending, badge, check);
    // A video's picture is in the note itself, so the tile shows it even before the file has come over from the PC.
    const ready = a.Thumb && thumbUrl(a.Id, a.Thumb);
    if (ready) { const img = document.createElement('img'); img.alt = ''; img.src = ready; tile.prepend(img); }
    tile.addEventListener('click', () => { if (selecting) { if (picked.has(a.Id)) picked.delete(a.Id); else picked.add(a.Id); tile.classList.toggle('picked', picked.has(a.Id)); updateBar(); } else run(() => openViewer(n, index)); });
    box.append(tile);
    if (!stored.has(a.Id)) return;
    if (a.MediaType.startsWith('image/')) later.push(async () => {
      let plain; try { plain = await decryptFile(await storedFile(a.Id), a); } catch { pending.textContent = T('cantOpen'); return; }
      if (turn !== drawing) return;
      const url = URL.createObjectURL(plain); noteUrls.push(url);
      noteFiles.set(a.Id, { url, file: new File([plain], a.Name, { type: a.MediaType }) });
      pending.remove();
      const img = document.createElement('img'); img.src = url; img.alt = ''; tile.prepend(img);
    });
    else {
      pending.remove();
      // Videos added on this phone get their picture as they are added, and the PC makes one for its own; this is
      // for a video that came without one. It means reading the whole video once, so very long ones keep a name.
      if (a.MediaType.startsWith('video/') && !a.Thumb && !thumbTried.has(a.Id) && a.Size <= THUMB_LIMIT) later.push(async () => {
        thumbTried.add(a.Id);
        // The picture is kept on this phone only; the PC gets it with the note's next change.
        const own = URL.createObjectURL(await decryptFile(await storedFile(a.Id), a));
        try {
          const jpeg = await videoFrame(own);
          if (!jpeg || !n.Attachments.includes(a)) return;
          a.Thumb = jpeg;
          if (turn === drawing) { const img = document.createElement('img'); img.alt = ''; img.src = thumbUrl(a.Id, jpeg); tile.prepend(img); }
          if (!n.draft) await persist(n);
        } finally { URL.revokeObjectURL(own); }
      });
      else if (!ready) { const name = document.createElement('span'); name.className = 'tile-name'; name.textContent = a.Name; tile.prepend(name); }
    }
  });
  updateBar();
  (async () => { for (const job of later) { if (turn !== drawing) return; try { await job(); } catch { } } updateBar(); })();
}
const thumbTried = new Set();   // attachments this run has already tried to make a picture for
// One frame of a video as a small JPEG (base64), drawn through a canvas; the PC shows the same picture. The element
// is emptied whatever happens: a video left with its source holds the whole decoded clip in memory.
export async function videoFrame(url) {
  const v = document.createElement('video'); v.muted = true; v.playsInline = true; v.preload = 'auto'; v.src = url;
  try {
    await new Promise((ok, no) => { v.onloadedmetadata = ok; v.onerror = no; setTimeout(no, 8000); });
    try { await v.play(); } catch { }
    await new Promise(ok => { v.onseeked = ok; v.currentTime = Math.min(0.3, (v.duration || 1) / 2); setTimeout(ok, 3000); });
    v.pause();
    const scale = 320 / (v.videoWidth || 320), canvas = document.createElement('canvas');
    canvas.width = 320; canvas.height = Math.max(1, Math.round((v.videoHeight || 180) * scale));
    canvas.getContext('2d').drawImage(v, 0, 0, canvas.width, canvas.height);
    const data = canvas.toDataURL('image/jpeg', 0.7);
    return data.length > 2000 ? data.slice(data.indexOf(',') + 1) : null;
  }
  finally { v.removeAttribute('src'); v.load(); }
}
// An attachment as a file to hand over: the photo already on screen, or decrypted now.
async function fileOf(a) {
  if (noteFiles.has(a.Id)) return noteFiles.get(a.Id).file;
  const encrypted = await storedFile(a.Id); if (!encrypted) throw Error(T('fromPc'));
  return new File([await decryptFile(encrypted, a)], a.Name, { type: a.MediaType });
}
export function shareFiles(files) {
  if (files.length && navigator.canShare?.({ files })) navigator.share({ files }).catch(() => {});
  else for (const f of files) { const link = document.createElement('a'); link.href = URL.createObjectURL(f); link.download = f.name; link.click(); }
}
// Save or share attachments (with the note's text when a note is given). Photos on screen go at once. Anything
// else is decrypted first and then offered with a second tap: the share sheet only opens straight from a tap.
export async function shareAttachments(list, note = null) {
  const go = files => note ? shareText(note, files) : shareFiles(files);
  if (list.every(a => noteFiles.has(a.Id))) return go(list.map(a => noteFiles.get(a.Id).file));
  if (list.length > 1 && list.reduce((sum, a) => sum + (noteFiles.has(a.Id) ? 0 : a.Size), 0) > SHARE_LIMIT) { toast(T('tooMuchAtOnce')); return; }
  toast(T('preparing'));
  const files = []; for (const a of list) files.push(await fileOf(a));
  sheet(T('readyToShare', files.length), [{ label: T('saveShare'), run: () => go(files) }]);
}
async function removeFiles(n, ids) { n.Attachments = n.Attachments.filter(x => !ids.includes(x.Id)); touch(n); await localSave(n, true); await renderAttachments(n); }

// ---------- select mode: pick tiles, then save or remove them together ----------
export function selectAttachments(on) {
  selecting = on; picked.clear();
  $('attachBar').hidden = !on; $('editorBar').hidden = on || !!S.current?.Deleted; $('share').hidden = $('noteMore').hidden = on || !!S.current?.Deleted; $('attachDone').hidden = !on;
  $('attachments').classList.toggle('selecting', on);
  paintPicked(); updateBar();
}
function paintPicked() { [...$('attachments').children].forEach((tile, i) => tile.classList.toggle('picked', picked.has(S.current?.Attachments[i]?.Id))); }
const here = () => S.current ? storedOf(S.current) : [];
function updateBar() {
  const all = here(), ready = all.filter(a => picked.has(a.Id));
  $('attachAll').textContent = ready.length === all.length && all.length > 0 ? T('deselectAll') : T('selectAll');
  $('attachShare').textContent = T('saveSelected', ready.length); $('attachShare').disabled = ready.length === 0;
  $('attachRemove').textContent = T('removeSelected', picked.size); $('attachRemove').disabled = picked.size === 0;
}
$('attachAll').addEventListener('click', () => { const all = here(); if (all.every(a => picked.has(a.Id))) picked.clear(); else for (const a of all) picked.add(a.Id); paintPicked(); updateBar(); });
$('attachShare').addEventListener('click', () => quietly(shareAttachments(here().filter(a => picked.has(a.Id)))));
$('attachRemove').addEventListener('click', () => { const n = S.current, ids = [...picked]; sheet(T('removeManyConfirm', ids.length), [{ label: T('removeSelected', ids.length), danger: true, run: () => run(async () => { selectAttachments(false); await removeFiles(n, ids); }) }]); });
$('attachDone').addEventListener('click', () => selectAttachments(false));

// ---------- full-screen viewer: the media, a close button, and save/share and remove actions in the bar ----------
// A photo is shown from its tile; anything else is decrypted when the viewer opens and let go when it closes. That
// happens beside the work queue, so a long video being opened never keeps a tap waiting.
function openViewer(n, index) {
  const a = n.Attachments[index], turn = ++viewing;
  let file = noteFiles.get(a.Id)?.file ?? null, own = null;
  const l = $('lightbox'); l.replaceChildren();
  const bar = document.createElement('div'); bar.className = 'viewer-bar';
  const shut = () => { l.hidden = true; l.replaceChildren(); viewing++; if (own) URL.revokeObjectURL(own); own = file = null; };
  const close = document.createElement('button'); close.className = 'icon-button'; close.setAttribute('aria-label', T('done')); close.innerHTML = ICON.close; close.addEventListener('click', shut);
  const name = document.createElement('span'); name.className = 'viewer-name'; name.textContent = a.Name;
  const share = document.createElement('button'); share.className = 'icon-button'; share.setAttribute('aria-label', T('saveShare')); share.innerHTML = ICON.share;
  share.addEventListener('click', () => { if (file) shareFiles([file]); });
  bar.append(close, name, share);
  if (!n.Deleted) {
    const remove = document.createElement('button'); remove.className = 'icon-button danger'; remove.setAttribute('aria-label', T('removeAttachment')); remove.innerHTML = ICON.trash;
    remove.addEventListener('click', () => sheet(a.Name, [{ label: T('removeAttachmentConfirm'), danger: true, run: () => run(async () => { shut(); await removeFiles(n, [a.Id]); }) }]));
    bar.append(remove);
  }
  const stage = document.createElement('div'); stage.className = 'viewer-stage';
  const say = text => { const w = document.createElement('p'); w.className = 'fine'; w.textContent = text; stage.replaceChildren(w); };
  const showMedia = url => {
    if (a.MediaType.startsWith('image/')) { const img = document.createElement('img'); img.src = url; img.alt = a.Name; stage.replaceChildren(img); }
    else if (a.MediaType.startsWith('video/')) { const v = document.createElement('video'); v.src = url; v.controls = true; v.playsInline = true; v.autoplay = true; stage.replaceChildren(v); }
    else say(T('noPreview'));
  };
  if (file) showMedia(noteFiles.get(a.Id).url);
  else if (!stored.has(a.Id)) say(T('fromPc'));
  else {
    say(T('preparing'));
    fileOf(a).then(f => { if (turn !== viewing) return; file = f; own = URL.createObjectURL(f); showMedia(own); }).catch(() => { if (turn === viewing) say(T('cantOpen')); });
  }
  l.append(bar, stage); l.hidden = false;
}

// ---------- adding from the camera roll ----------
$('attach').addEventListener('click', () => $('files').click());
$('files').addEventListener('change', () => {
  const files = [...$('files').files], n = S.current; $('files').value = '';
  run(async () => {
    if (!n || n.Deleted) return;
    for (const file of files) {
      if (file.size > MAX_FILE) throw Error(T('tooLarge'));
      if (!/^(image|video)\//.test(file.type) && !/\.(heic|heif|mov|mp4|m4v|jpe?g|png|gif|webp)$/i.test(file.name)) throw Error(T('pickMedia'));
      toast(T('encrypting', (file.size / 1048576).toFixed(1)));
      const encrypted = await encryptFile(file);
      // The picture of a video is made here, while the file itself is at hand: no second decryption, no second save.
      if (encrypted.meta.MediaType.startsWith('video/')) {
        const url = URL.createObjectURL(file);
        try { encrypted.meta.Thumb = await videoFrame(url); } catch { } finally { URL.revokeObjectURL(url); }
        thumbTried.add(encrypted.meta.Id);
      }
      await put('files', encrypted.meta.Id, encrypted.blob);
      await commitDraft(n); n.Attachments.push(encrypted.meta); touch(n); await localSave(n, true);
    }
    if (isReady()) send(await manifest());
    await renderAttachments(n); toast(T('attachmentSaved'));
  });
});
