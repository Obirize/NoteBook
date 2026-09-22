// Photos and videos on a note: a compact grid of square tiles (a tap opens the full-screen viewer, where saving,
// sharing and removing live), a select mode for saving or removing several at once, and adding new ones from
// the camera roll. Files are encrypted before they touch storage; decrypted copies exist only in memory.
import { encryptFile, decryptFile } from './crypto.js';
import { T } from './lang.js';
import { S, $, MAX_FILE } from './state.js';
import { run, toast, sheet } from './ui.js';
import { get, put, touch } from './store.js';
import { b64, un64 } from './crypto.js';
import { localSave, send, manifest, isReady } from './sync.js';
import { commitDraft } from './editor.js';

export const noteFiles = new Map();  // attachment id -> { url, file } for the open note (decrypted, ready to share)
const noteUrls = [];
const picked = new Set();            // attachment ids checked in select mode
let selecting = false;

export async function renderAttachments(n) {
  for (const url of noteUrls) URL.revokeObjectURL(url); noteUrls.length = 0; noteFiles.clear();
  for (const id of [...picked]) if (!n.Attachments.some(a => a.Id === id)) picked.delete(id);
  const box = $('attachments'); box.replaceChildren(); box.classList.toggle('selecting', selecting);
  n.Attachments.forEach((a, index) => {
    const tile = document.createElement('button'); tile.className = 'tile' + (picked.has(a.Id) ? ' picked' : ''); tile.type = 'button'; tile.setAttribute('aria-label', a.Name);
    const badge = document.createElement('span'); badge.className = 'tile-badge';
    badge.innerHTML = a.MediaType.startsWith('video/') ? '<svg viewBox="0 0 24 24"><path d="M8 5l11 7-11 7z"/></svg>' : a.MediaType.startsWith('image/') ? '' : '<svg viewBox="0 0 24 24"><path d="M6 3h8l5 5v13H6z"/><path d="M14 3v5h5"/></svg>';
    const check = document.createElement('span'); check.className = 'tile-check'; check.innerHTML = '<svg viewBox="0 0 24 24"><path d="M5 12l5 5 9-10"/></svg>';
    const pending = document.createElement('span'); pending.className = 'tile-pending'; pending.textContent = T('fromPc');
    tile.append(pending, badge, check);
    tile.addEventListener('click', () => { if (selecting) { if (picked.has(a.Id)) picked.delete(a.Id); else picked.add(a.Id); tile.classList.toggle('picked', picked.has(a.Id)); updateBar(); } else run(() => openViewer(n, index)); });
    box.append(tile);
    get('files', a.Id).then(async encrypted => {
      if (!encrypted) return;
      let plain; try { plain = await decryptFile(encrypted, a); } catch { pending.textContent = T('cantOpen'); return; }
      const url = URL.createObjectURL(plain); noteUrls.push(url);
      noteFiles.set(a.Id, { url, file: new File([plain], a.Name, { type: a.MediaType }) });
      pending.remove();
      if (a.MediaType.startsWith('image/')) { const img = document.createElement('img'); img.src = url; img.alt = ''; tile.prepend(img); }
      else if (a.MediaType.startsWith('video/')) {
        const img = document.createElement('img'); img.alt = ''; tile.prepend(img);
        if (a.Thumb) img.src = thumbUrl(a.Thumb);
        else videoFrame(url).then(async jpeg => { if (!jpeg || !n.Attachments.includes(a)) return; a.Thumb = jpeg; img.src = thumbUrl(jpeg); if (!n.draft) { n.Revision++; await localSave(n, true); } }).catch(() => {});
      }
      else { const name = document.createElement('span'); name.className = 'tile-name'; name.textContent = a.Name; tile.prepend(name); }
    });
  });
  updateBar();
}
const thumbUrl = thumb => 'data:image/jpeg;base64,' + (typeof thumb === 'string' ? thumb : b64(thumb));
// One frame of a video as a small JPEG (base64), drawn through a canvas; the PC shows the same picture.
export async function videoFrame(url) {
  const v = document.createElement('video'); v.muted = true; v.playsInline = true; v.preload = 'auto'; v.src = url;
  await new Promise((ok, no) => { v.onloadedmetadata = ok; v.onerror = no; setTimeout(no, 8000); });
  try { await v.play(); } catch { }
  await new Promise(ok => { v.onseeked = ok; v.currentTime = Math.min(0.3, (v.duration || 1) / 2); setTimeout(ok, 3000); });
  v.pause();
  const scale = 320 / (v.videoWidth || 320), canvas = document.createElement('canvas');
  canvas.width = 320; canvas.height = Math.max(1, Math.round((v.videoHeight || 180) * scale));
  canvas.getContext('2d').drawImage(v, 0, 0, canvas.width, canvas.height);
  v.removeAttribute('src'); v.load();
  const data = canvas.toDataURL('image/jpeg', 0.7);
  return data.length > 2000 ? data.slice(data.indexOf(',') + 1) : null;
}
export function shareFiles(files) {
  if (files.length && navigator.canShare?.({ files })) navigator.share({ files }).catch(() => {});
  else for (const f of files) { const link = document.createElement('a'); link.href = URL.createObjectURL(f); link.download = f.name; link.click(); }
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
function updateBar() {
  const ready = [...picked].filter(id => noteFiles.has(id));
  $('attachAll').textContent = picked.size === noteFiles.size && noteFiles.size > 0 ? T('deselectAll') : T('selectAll');
  $('attachShare').textContent = T('saveSelected', ready.length); $('attachShare').disabled = ready.length === 0;
  $('attachRemove').textContent = T('removeSelected', picked.size); $('attachRemove').disabled = picked.size === 0;
}
$('attachAll').addEventListener('click', () => { if (picked.size === noteFiles.size) picked.clear(); else for (const id of noteFiles.keys()) picked.add(id); paintPicked(); updateBar(); });
$('attachShare').addEventListener('click', () => shareFiles([...picked].map(id => noteFiles.get(id)?.file).filter(Boolean)));
$('attachRemove').addEventListener('click', () => { const n = S.current, ids = [...picked]; sheet(T('removeManyConfirm', ids.length), [{ label: T('removeSelected', ids.length), danger: true, run: () => run(async () => { selectAttachments(false); await removeFiles(n, ids); }) }]); });
$('attachDone').addEventListener('click', () => selectAttachments(false));

// ---------- full-screen viewer: the media, a close button, and save/share and remove actions in the bar ----------
async function openViewer(n, index) {
  const a = n.Attachments[index]; const ready = noteFiles.get(a.Id);
  const l = $('lightbox'); l.replaceChildren();
  const bar = document.createElement('div'); bar.className = 'viewer-bar';
  const close = document.createElement('button'); close.className = 'icon-button'; close.setAttribute('aria-label', T('done')); close.innerHTML = '<svg viewBox="0 0 24 24"><path d="M6 6l12 12M18 6L6 18"/></svg>'; close.addEventListener('click', () => { l.hidden = true; l.replaceChildren(); });
  const name = document.createElement('span'); name.className = 'viewer-name'; name.textContent = a.Name;
  const share = document.createElement('button'); share.className = 'icon-button'; share.setAttribute('aria-label', T('saveShare')); share.innerHTML = '<svg viewBox="0 0 24 24"><path d="M12 3v13M7 8l5-5 5 5"/><path d="M5 12v8h14v-8"/></svg>';
  share.addEventListener('click', () => { if (ready) shareFiles([ready.file]); });
  bar.append(close, name, share);
  if (!n.Deleted) {
    const remove = document.createElement('button'); remove.className = 'icon-button danger'; remove.setAttribute('aria-label', T('removeAttachment')); remove.innerHTML = '<svg viewBox="0 0 24 24"><path d="M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13M10 11v6M14 11v6"/></svg>';
    remove.addEventListener('click', () => sheet(a.Name, [{ label: T('removeAttachmentConfirm'), danger: true, run: () => run(async () => { l.hidden = true; l.replaceChildren(); await removeFiles(n, [a.Id]); }) }]));
    bar.append(remove);
  }
  const stage = document.createElement('div'); stage.className = 'viewer-stage';
  if (!ready) { const w = document.createElement('p'); w.className = 'fine'; w.textContent = T('fromPc'); stage.append(w); }
  else if (a.MediaType.startsWith('image/')) { const img = document.createElement('img'); img.src = ready.url; img.alt = a.Name; stage.append(img); }
  else if (a.MediaType.startsWith('video/')) { const v = document.createElement('video'); v.src = ready.url; v.controls = true; v.playsInline = true; v.autoplay = true; stage.append(v); }
  else { const w = document.createElement('p'); w.className = 'fine'; w.textContent = T('noPreview'); stage.append(w); }
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
      await put('files', encrypted.meta.Id, encrypted.blob);
      await commitDraft(n); n.Attachments.push(encrypted.meta); touch(n); await localSave(n, true);
    }
    if (isReady()) send(await manifest());
    await renderAttachments(n); toast(T('attachmentSaved'));
  });
});
