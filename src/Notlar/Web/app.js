// The phone side of NoteBook: notes live encrypted in IndexedDB, travel sealed over a WebSocket to the PC
// (see SyncService.cs for the protocol) and are shown in a layout that follows Apple's Notes app.
import { id, random, b64, un64, derive, mac, verifyMac, seal, open, encryptFile, decryptFile } from './crypto.js';
import { T, locale, applyLang } from './lang.js';
import * as Checklist from './checklist.js';

const $ = x => document.getElementById(x);
const MAX_FILE = 256 * 1024 * 1024, SEND_DELAY = 400, RETRY_MIN = 3000, RETRY_MAX = 20000;
let db, keys, device, notes = [], purges = [], current = null, trash = false, selecting = false;
const selected = new Set();
let socket = null, ready = false, receiving = null, serverNonce, clientNonce, retry, retryDelay = RETRY_MIN, lastSynced = null;
let queue = Promise.resolve();
const sendTimers = new Map(), thumbUrls = new Map(), noteUrls = [];

// ---------- small helpers ----------
function run(fn) { queue = queue.then(fn).catch(e => { console.error(e); toast(e.message || T('failed')); }); return queue; }
let toastTimer;
function toast(text) { const t = $('toast'); t.textContent = text; t.hidden = false; clearTimeout(toastTimer); toastTimer = setTimeout(() => t.hidden = true, 3200); }
function request(store, mode, fn) { return new Promise((resolve, reject) => { const tx = db.transaction(store, mode), r = fn(tx.objectStore(store)); tx.oncomplete = () => resolve(r?.result); tx.onerror = () => reject(tx.error); tx.onabort = () => reject(tx.error || Error(T('storage'))); }); }
const get = (s, k) => request(s, 'readonly', t => t.get(k)), put = (s, k, v) => request(s, 'readwrite', t => t.put(v, k)), del = (s, k) => request(s, 'readwrite', t => t.delete(k));
const same = (a, b) => a.Title === b.Title && a.Text === b.Text && a.Pinned === b.Pinned && a.Deleted === b.Deleted && JSON.stringify(a.Attachments) === JSON.stringify(b.Attachments);
const persist = async n => put('notes', n.Id, { id: n.Id, rev: n.Revision, blob: await seal(keys, n) });
const savePurges = () => put('meta', 'purges', purges);
const send = m => { if (ready && socket?.readyState === 1) socket.send(JSON.stringify(m)); };
const fmt = { time: new Intl.DateTimeFormat(locale, { hour: '2-digit', minute: '2-digit' }), day: new Intl.DateTimeFormat(locale, { weekday: 'long' }), date: new Intl.DateTimeFormat(locale, { day: 'numeric', month: 'short', year: 'numeric' }), month: new Intl.DateTimeFormat(locale, { month: 'long', year: 'numeric' }), full: new Intl.DateTimeFormat(locale, { day: 'numeric', month: 'long', year: 'numeric', hour: '2-digit', minute: '2-digit' }) };
function startOfDay(d) { const x = new Date(d); x.setHours(0, 0, 0, 0); return x; }
function dateLabel(iso) { const d = new Date(iso), today = startOfDay(new Date()), day = startOfDay(d); const diff = (today - day) / 86400000; if (diff < 1) return fmt.time.format(d); if (diff < 7) return fmt.day.format(d); return fmt.date.format(d); }
function sectionOf(n) { if (n.Pinned && !n.Deleted) return T('pinned'); const d = new Date(n.Updated), today = startOfDay(new Date()), diff = (today - startOfDay(d)) / 86400000; if (diff < 1) return T('today'); if (diff < 2) return T('yesterday'); if (diff < 7) return T('previous7'); if (diff < 30) return T('previous30'); return fmt.month.format(d); }
function autosize(el) { el.style.height = 'auto'; el.style.height = el.scrollHeight + 'px'; }
function setStatus(text, busy = false) { const s = $('syncStatus'); s.textContent = text; s.className = 'status' + (busy ? ' busy' : ''); }

// ---------- local edits ----------
function touch(n) { n.Revision++; n.Updated = new Date().toISOString(); }
// Saved at once; sent to the PC after a short pause so a burst of keystrokes travels as one revision.
async function localSave(n, immediate = false) {
  await persist(n); await put('pending', n.Id, true);
  clearTimeout(sendTimers.get(n.Id));
  if (immediate) await flushNote(n); else sendTimers.set(n.Id, setTimeout(() => run(() => flushNote(n)), SEND_DELAY));
}
async function flushNote(n) { sendTimers.delete(n.Id); if (!ready) return; send({ t: 'note', id: n.Id, rev: n.Revision, blob: b64(await seal(keys, n)), base: await get('base', n.Id) }); send({ t: 'flush' }); }
async function flushAll() { for (const n of notes) if (await get('pending', n.Id)) await flushNote(n); }

// ---------- list ----------
function renderList() {
  const q = $('search').value.trim().toLocaleLowerCase(locale);
  const shown = notes.filter(n => n.Deleted === trash && (!q || (n.Title + ' ' + n.Text + ' ' + n.Attachments.map(a => a.Name).join(' ')).toLocaleLowerCase(locale).includes(q)))
    .sort((a, b) => Number(b.Pinned) - Number(a.Pinned) || Date.parse(b.Updated) - Date.parse(a.Updated));
  $('listTitle').textContent = trash ? T('recentlyDeleted') : T('notes');
  $('folderBack').hidden = !trash || selecting; $('trashLink').hidden = trash || selecting; $('compose').hidden = trash;
  $('select').hidden = selecting || shown.length === 0; $('selectDone').hidden = !selecting; $('more').hidden = selecting;
  $('list').classList.toggle('selecting', selecting);
  $('selectBar').hidden = !selecting; $('list').querySelector('.toolbar:not(#selectBar)').hidden = selecting;
  for (const id of [...selected]) if (!shown.some(n => n.Id === id)) selected.delete(id);
  $('selectAll').textContent = selected.size === shown.length && shown.length > 0 ? T('deselectAll') : T('selectAll');
  $('selectDelete').disabled = selected.size === 0; $('selectRestore').disabled = selected.size === 0; $('selectRestore').hidden = !trash;
  $('selectDelete').textContent = trash ? T('deletePermanently') : T('delete');
  $('count').textContent = selecting ? (selected.size === 0 ? T('selectPrompt') : T('selectedCount', selected.size)) : shown.length === 0 ? T('countNone') : T('countMany', shown.length);
  $('empty').hidden = shown.length > 0; $('empty').textContent = q ? T('noResults') : trash ? T('noDeleted') : T('noNotes');
  const sections = $('sections'); sections.replaceChildren();
  let group = null, lastSection;
  for (const n of shown) {
    const section = trash ? null : sectionOf(n);
    if (section !== lastSection) { if (section) { const h = document.createElement('div'); h.className = 'section-title'; h.textContent = section; sections.append(h); } group = document.createElement('div'); group.className = 'group'; sections.append(group); lastSection = section; }
    group.append(row(n));
  }
}
function row(n) {
  const el = document.createElement('div'); el.className = 'row';
  const action = document.createElement('div'); action.className = 'row-action' + (trash ? ' restore' : ''); action.textContent = trash ? T('restore') : T('delete');
  const inner = document.createElement('div'); inner.className = 'row-inner';
  const check = document.createElement('div'); check.className = 'row-check'; check.innerHTML = '<svg viewBox="0 0 24 24"><path d="M5 12l5 5 9-10"/></svg>'; inner.append(check);
  if (selected.has(n.Id)) el.classList.add('checked');
  const text = document.createElement('div'); text.className = 'row-text';
  const title = document.createElement('div'); title.className = 'row-title'; title.textContent = n.Title.trim() || T('newNote');
  const sub = document.createElement('div'); sub.className = 'row-sub';
  const when = document.createElement('b'); when.textContent = dateLabel(n.Updated);
  sub.append(when, document.createTextNode(Checklist.preview(n.Text).replace(/\s+/g, ' ').trim() || (n.Attachments.length ? T('attachmentsCount', n.Attachments.length) : T('noText'))));
  text.append(title, sub); inner.append(text);
  if (n.Pinned && trash) { const pin = pinIcon(); inner.append(pin); }
  const image = n.Attachments.find(a => a.MediaType.startsWith('image/'));
  if (image) { const img = document.createElement('img'); img.className = 'row-thumb'; img.alt = ''; inner.append(img); thumbnail(image).then(url => { if (url) img.src = url; else img.remove(); }); }
  el.append(action, inner);
  if (!selecting) swipe(el, inner, () => run(() => trash ? restoreNote(n) : deleteNote(n)));
  inner.addEventListener('click', () => {
    if (el.dataset.swiped) return;
    if (selecting) { if (selected.has(n.Id)) selected.delete(n.Id); else selected.add(n.Id); renderList(); return; }
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

// ---------- editor ----------
function show(screen) { for (const s of ['install', 'pair', 'list', 'editor']) $(s).hidden = s !== screen; }
// Opened in Safari rather than from the Home Screen: explain the two taps that make it an app.
let installSkipped = false; try { installSkipped = sessionStorage.getItem('skipInstall') === '1'; } catch { }
const needsInstall = () => !navigator.standalone && !installSkipped && /iPhone|iPad|iPod/.test(navigator.userAgent) && !window.matchMedia('(display-mode: standalone)').matches;
async function openNote(n) {
  current = n; show('editor');
  $('backLabel').textContent = trash ? T('recentlyDeleted') : T('notes');
  $('noteDate').textContent = fmt.full.format(new Date(n.Updated));
  $('title').value = n.Title; Checklist.setBody($('body'), n.Text); autosize($('title'));
  $('title').readOnly = n.Deleted; $('body').contentEditable = n.Deleted ? 'false' : 'true';
  $('pin').hidden = $('attach').hidden = $('checklist').hidden = n.Deleted; $('noteMore').hidden = n.Deleted; $('trashBar').hidden = !n.Deleted; $('trashNotice').hidden = !n.Deleted;
  $('pin').style.color = n.Pinned ? 'var(--accent)' : 'var(--muted)';
  $('pin').setAttribute('aria-label', n.Pinned ? T('unpin') : T('pin'));
  await renderAttachments(n);
  $('editor').querySelector('.page').scrollTop = 0;
}
async function renderAttachments(n) {
  for (const url of noteUrls) URL.revokeObjectURL(url); noteUrls.length = 0;
  const box = $('attachments'); box.replaceChildren();
  for (const a of n.Attachments) {
    const item = document.createElement('div'); item.className = 'attachment';
    const encrypted = await get('files', a.Id);
    if (!encrypted) { const w = document.createElement('div'); w.className = 'file'; w.innerHTML = '<div><b></b></div>'; w.firstChild.append(T('fromPc')); w.querySelector('b').textContent = a.Name; item.append(w); }
    else {
      let url, plain = null; try { plain = await decryptFile(encrypted, a); url = URL.createObjectURL(plain); noteUrls.push(url); } catch { url = null; }
      // "Save to Photos" on iOS goes through the share sheet; the decrypted file is kept ready so share() runs
      // straight from the tap (Safari only allows it within the tap).
      if (plain) {
        const share = document.createElement('button'); share.className = 'remove share'; share.setAttribute('aria-label', T('saveShare'));
        share.innerHTML = '<svg viewBox="0 0 24 24"><path d="M12 3v13M7 8l5-5 5 5"/><path d="M5 12v8h14v-8"/></svg>';
        const file = new File([plain], a.Name, { type: a.MediaType });
        share.addEventListener('click', () => {
          if (navigator.canShare?.({ files: [file] })) navigator.share({ files: [file] }).catch(() => {});
          else { const link = document.createElement('a'); link.href = url; link.download = a.Name; link.click(); }
        });
        item.append(share);
      }
      if (url && a.MediaType.startsWith('image/')) { const img = document.createElement('img'); img.src = url; img.alt = a.Name; img.addEventListener('click', () => lightbox('img', url)); item.append(img); }
      else if (url && a.MediaType.startsWith('video/')) { const v = document.createElement('video'); v.src = url; v.controls = true; v.playsInline = true; v.preload = 'metadata'; item.append(v); }
      else { const w = document.createElement('div'); w.className = 'file'; w.innerHTML = '<div><b></b></div>'; w.firstChild.append(url ? T('noPreview') : T('cantOpen')); w.querySelector('b').textContent = a.Name; item.append(w); }
    }
    if (!n.Deleted) {
      const remove = document.createElement('button'); remove.className = 'remove'; remove.setAttribute('aria-label', T('removeAttachment'));
      remove.innerHTML = '<svg viewBox="0 0 24 24"><path d="M6 6l12 12M18 6L6 18"/></svg>';
      remove.addEventListener('click', () => sheet(a.Name, [{ label: T('removeAttachmentConfirm'), danger: true, run: () => run(async () => { n.Attachments = n.Attachments.filter(x => x.Id !== a.Id); touch(n); await localSave(n, true); await renderAttachments(n); }) }]));
      item.append(remove);
    }
    box.append(item);
  }
}
function lightbox(kind, url) { const l = $('lightbox'); l.replaceChildren(); const el = document.createElement(kind); el.src = url; if (kind === 'video') { el.controls = true; el.playsInline = true; } l.append(el); l.hidden = false; l.onclick = e => { if (e.target === l || kind === 'img') l.hidden = true; }; }
function sheet(title, actions) {
  const s = $('sheet'), body = s.querySelector('.sheet-body'); body.replaceChildren();
  if (title) { const t = document.createElement('div'); t.className = 'sheet-title'; t.textContent = title; body.append(t); }
  for (const a of actions) { const b = document.createElement('button'); b.textContent = a.label; if (a.danger) b.className = 'danger'; b.addEventListener('click', () => { s.hidden = true; a.run(); }); body.append(b); }
  s.hidden = false;
}
$('sheet').querySelector('.sheet-cancel').addEventListener('click', () => $('sheet').hidden = true);
$('sheet').addEventListener('click', e => { if (e.target === $('sheet')) $('sheet').hidden = true; });

// A new note is only a draft until something is typed or attached; an untouched draft simply disappears on the way back.
async function newNote() {
  const now = new Date().toISOString();
  const n = { Id: id(), Title: '', Text: '', Created: now, Updated: now, Pinned: false, Deleted: false, DeletedAt: null, Revision: 0, Attachments: [], draft: true };
  trash = false; await openNote(n); $('title').focus();
}
const isEmpty = n => !n.Title.trim() && !n.Text.trim() && n.Attachments.length === 0;
async function commitDraft(n) { if (!n.draft) return; delete n.draft; notes.push(n); }
async function leaveEditor() {
  const n = current;
  if (n) {
    for (const t of sendTimers.values()) clearTimeout(t);
    if (n.draft) current = null;
    else if (!n.Deleted && isEmpty(n)) { await purgeNote(n, true); return; }
    await flushAll();
  }
  closeEditor();
}
async function deleteNote(n) { n.Deleted = true; n.DeletedAt = new Date().toISOString(); touch(n); await localSave(n, true); if (current === n) closeEditor(); else renderList(); toast(T('movedToTrash')); }
async function restoreNote(n) { n.Deleted = false; n.DeletedAt = null; touch(n); await localSave(n, true); if (current === n) closeEditor(); else renderList(); toast(T('restored')); }
async function purgeNote(n, quiet = false) { purges.push({ id: n.Id, rev: n.Revision }); await savePurges(); await del('notes', n.Id); await del('pending', n.Id); notes = notes.filter(x => x !== n); send({ t: 'purge', id: n.Id, rev: n.Revision }); send({ t: 'flush' }); if (current === n) closeEditor(); else renderList(); if (!quiet) toast(T('purged')); }
function closeEditor() { current = null; $('editor').style.transform = ''; show('list'); renderList(); }
async function bulk(action) {
  const targets = notes.filter(n => selected.has(n.Id)); if (targets.length === 0) return;
  for (const n of targets) {
    if (action === 'delete') { n.Deleted = true; n.DeletedAt = new Date().toISOString(); touch(n); await localSave(n, true); }
    else if (action === 'restore') { n.Deleted = false; n.DeletedAt = null; touch(n); await localSave(n, true); }
    else if (action === 'purge') await purgeNote(n, true);
  }
  selected.clear(); selecting = false; renderList();
  toast(action === 'delete' ? T('movedManyToTrash', targets.length) : action === 'restore' ? T('restoredMany', targets.length) : T('purgedMany', targets.length));
}

// ---------- sync ----------
async function manifest() { const files = await request('files', 'readonly', s => s.getAllKeys()); return { t: 'manifest', notes: notes.map(n => ({ id: n.Id, rev: n.Revision, updated: Date.parse(n.Updated) })), purged: purges, files }; }
async function wantFiles() { const missing = new Set(); for (const a of notes.flatMap(n => n.Attachments)) if (!await get('files', a.Id)) missing.add(a.Id); if (missing.size) send({ t: 'want-files', ids: [...missing] }); }
async function receiveNote(m) {
  const n = await open(keys, m.id, m.rev, un64(m.blob));
  if (purges.some(p => p.id === n.Id && p.rev >= n.Revision)) return;
  const old = notes.find(x => x.Id === n.Id);
  if (m.replyRev != null) {
    // The PC's answer to something we sent: remember it as the base; keep our newer local edits if any.
    await put('base', n.Id, { rev: m.rev, blob: m.blob });
    if (old && old.Revision > m.replyRev && await get('pending', old.Id)) return;
  } else {
    if (old && old.Revision > n.Revision) return;
    if (old && await get('pending', old.Id) && !same(old, n)) return;
  }
  if (old) notes[notes.indexOf(old)] = n; else notes.push(n);
  await persist(n); await put('base', n.Id, { rev: m.rev, blob: m.blob }); await del('pending', n.Id);
  if (current?.Id === n.Id) { current = n; if (!$('editor').hidden && document.activeElement !== $('title') && document.activeElement !== bodyEl) await openNote(n); }
  if (!$('list').hidden) renderList();
}
async function transfer(ids) {
  for (const aid of ids) {
    const b = await get('files', aid); if (!b) continue;
    send({ t: 'file', id: aid, size: b.size });
    for (let at = 0; at < b.size; at += 262144) {
      while (socket?.readyState === 1 && socket.bufferedAmount > 1048576) await new Promise(r => setTimeout(r, 30));
      if (!ready || socket?.readyState !== 1) return;
      socket.send(await b.slice(at, at + 262144).arrayBuffer());
    }
    send({ t: 'file-end', id: aid });
  }
}
async function handle(data) {
  if (typeof data !== 'string') {
    if (!receiving) throw Error(T('unexpectedFile'));
    receiving.parts.push(data); receiving.have += data.byteLength;
    if (receiving.have > receiving.size) throw Error(T('sizeExceeded'));
    return;
  }
  const m = JSON.parse(data);
  switch (m.t) {
    case 'challenge': serverNonce = un64(m.nonce); clientNonce = random(32); socket.send(JSON.stringify({ t: 'auth', nonce: b64(clientNonce), mac: b64(new Uint8Array(await mac(keys, 'client', serverNonce, clientNonce))) })); break;
    case 'welcome':
      if (!await verifyMac(keys, un64(m.mac), 'server', clientNonce, serverNonce)) { socket.close(); throw Error(T('pcNotVerified')); }
      ready = true; retryDelay = RETRY_MIN; setStatus(T('syncing'), true); send(await manifest()); break;
    case 'manifest': {
      for (const n of notes) {
        const peer = m.notes.find(x => x.id === n.Id);
        if (!m.purged.some(p => p.id === n.Id && p.rev >= n.Revision) && (!peer || n.Revision > peer.rev || (n.Revision === peer.rev && (Date.parse(n.Updated) !== peer.updated || await get('pending', n.Id)))))
          send({ t: 'note', id: n.Id, rev: n.Revision, blob: b64(await seal(keys, n)), base: await get('base', n.Id) });
      }
      for (const p of purges) send({ t: 'purge', ...p });
      send({ t: 'done' }); break;
    }
    case 'note': await receiveNote(m); break;
    case 'purge': {
      const n = notes.find(x => x.Id === m.id);
      if (n && n.Revision <= m.rev) { notes = notes.filter(x => x !== n); await del('notes', n.Id); await del('pending', n.Id); if (current?.Id === n.Id) closeEditor(); }
      if (!purges.some(p => p.id === m.id && p.rev >= m.rev)) { purges = purges.filter(p => p.id !== m.id); purges.push({ id: m.id, rev: m.rev }); await savePurges(); }
      if (!$('list').hidden) renderList(); break;
    }
    case 'flush': case 'done': await wantFiles(); lastSynced = new Date(); setStatus(T('updated')); break;
    case 'want-files': await transfer(m.ids); break;
    case 'file': if (m.size < 24 || m.size > MAX_FILE + 65536 || !/^[a-f0-9]{32}$/.test(m.id)) throw Error(T('invalidFile')); receiving = { id: m.id, size: m.size, have: 0, parts: [] }; break;
    case 'file-end': {
      const r = receiving; receiving = null;
      if (!r || r.id !== m.id || r.have !== r.size) throw Error(T('incompleteFile'));
      const a = notes.flatMap(n => n.Attachments).find(a => a.Id === r.id); if (!a) break;
      const blob = new Blob(r.parts); await decryptFile(blob, a); await put('files', a.Id, blob);
      if (current?.Attachments.some(x => x.Id === a.Id)) await renderAttachments(current); else if (!$('list').hidden) renderList();
      break;
    }
    case 'rejected': socket.close(); throw Error(T('rejected'));
  }
}
function connect() {
  clearTimeout(retry);
  if (!keys || (socket && socket.readyState < 2)) return;
  ready = false; setStatus(T('connecting'), true);
  const ws = new WebSocket('wss://' + location.host + '/sync'); socket = ws; ws.binaryType = 'arraybuffer';
  ws.onopen = () => ws.send(JSON.stringify({ t: 'hello', protocol: 1, device, name: T('device') }));
  ws.onmessage = e => run(async () => { if (socket !== ws) return; try { await handle(e.data); } catch (error) { ws.close(); throw error; } });
  ws.onclose = () => {
    if (socket !== ws) return;
    ready = false; receiving = null;
    setStatus(lastSynced ? T('offlineLast', fmt.time.format(lastSynced)) : T('offlineLocal'));
    retry = setTimeout(connect, retryDelay); retryDelay = Math.min(RETRY_MAX, retryDelay * 2);
  };
  ws.onerror = () => {};
}

// ---------- pairing ----------
async function applyKey(raw) {
  if (raw.length !== 32) throw Error(T('pairInvalid'));
  if (keys && !confirm(T('pairAgain'))) return;
  keys = await derive(raw); raw.fill(0);
  await put('meta', 'keys', keys); for (const n of notes) await persist(n);
  history.replaceState(null, '', '/'); $('pairLink').value = ''; $('pairCode').value = '';
  show('list'); renderList(); socket?.close(); socket = null; connect();
  if (!navigator.standalone) toast(T('pairedNext'));
}
async function pair(link) { const u = new URL(link, location.href); if (u.origin !== location.origin) throw Error(T('otherPc')); await applyKey(un64(new URLSearchParams(u.hash.slice(1)).get('k') || '')); }
// The code shown on the PC fetches the sync key over TLS; wrong or stale codes are refused there.
async function pairWithCode(code) {
  code = code.replace(/\D/g, ''); if (code.length !== 6) throw Error(T('codeHint'));
  const r = await fetch('/pair', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ code }) });
  if (!r.ok) throw Error(T('codeRefused'));
  const { k } = await r.json(); await applyKey(un64(k));
}

// ---------- wiring ----------
$('pairButton').addEventListener('click', () => run(() => pairWithCode($('pairCode').value)));
$('pairCode').addEventListener('keydown', e => { if (e.key === 'Enter') $('pairButton').click(); });
$('pairCode').addEventListener('input', () => { const v = $('pairCode').value.replace(/\D/g, '').slice(0, 6); $('pairCode').value = v.length > 3 ? v.slice(0, 3) + ' ' + v.slice(3) : v; });
$('pairLinkButton').addEventListener('click', () => run(() => pair($('pairLink').value)));
$('search').addEventListener('input', renderList);
$('compose').addEventListener('click', () => run(newNote));
$('trashLink').addEventListener('click', () => { trash = true; selected.clear(); renderList(); });
$('folderBack').addEventListener('click', () => { trash = false; selected.clear(); renderList(); });
$('select').addEventListener('click', () => { selecting = true; selected.clear(); renderList(); });
$('selectDone').addEventListener('click', () => { selecting = false; selected.clear(); renderList(); });
$('selectAll').addEventListener('click', () => { const ids = notes.filter(n => n.Deleted === trash).map(n => n.Id); if (selected.size === ids.length && ids.length > 0) selected.clear(); else for (const id of ids) selected.add(id); renderList(); });
$('selectDelete').addEventListener('click', () => { if (trash) sheet(T('purgeManyConfirm', selected.size), [{ label: T('deletePermanently'), danger: true, run: () => run(() => bulk('purge')) }]); else run(() => bulk('delete')); });
$('selectRestore').addEventListener('click', () => run(() => bulk('restore')));
$('back').addEventListener('click', () => run(leaveEditor));
// Swiping in from the left edge of a note goes back, as in the Notes app.
{
  const editor = $('editor'); let startX = 0, startY = 0, dragging = false, dx = 0;
  editor.addEventListener('touchstart', e => { const t = e.touches[0]; dragging = t.clientX < 28; startX = t.clientX; startY = t.clientY; dx = 0; }, { passive: true });
  editor.addEventListener('touchmove', e => { if (!dragging) return; const t = e.touches[0]; dx = Math.max(0, t.clientX - startX); if (Math.abs(t.clientY - startY) > 60 && dx < 30) { dragging = false; editor.classList.remove('dragging'); editor.style.transform = ''; return; } editor.classList.add('dragging'); editor.style.transform = `translateX(${dx}px)`; }, { passive: true });
  editor.addEventListener('touchend', () => { if (!dragging) return; dragging = false; editor.classList.remove('dragging'); if (dx > 90) { editor.style.transform = 'translateX(100%)'; setTimeout(() => run(leaveEditor), 120); } else editor.style.transform = ''; });
}
$('done').addEventListener('click', () => document.activeElement?.blur());
$('more').addEventListener('click', () => sheet(null, [
  { label: T('syncNow'), run: () => run(async () => { if (ready) { setStatus(T('syncing'), true); await flushAll(); send(await manifest()); } else connect(); }) },
  { label: T('repair'), run: () => { show('pair'); $('pairCode').focus(); } },
]));
$('noteMore').addEventListener('click', () => { const n = current; if (!n) return; sheet(null, [
  { label: n.Pinned ? T('unpin') : T('pin'), run: () => $('pin').click() },
  { label: T('delete'), danger: true, run: () => run(async () => { if (n.draft) { current = null; closeEditor(); } else await deleteNote(n); }) },
]); });
$('pin').addEventListener('click', () => run(async () => { const n = current; if (!n || n.Deleted) return; await commitDraft(n); n.Pinned = !n.Pinned; touch(n); await localSave(n, true); $('pin').style.color = n.Pinned ? 'var(--accent)' : 'var(--muted)'; $('pin').setAttribute('aria-label', n.Pinned ? T('unpin') : T('pin')); }));
$('restore').addEventListener('click', () => run(() => restoreNote(current)));
$('purge').addEventListener('click', () => { const n = current; sheet(T('purgeConfirm'), [{ label: T('deletePermanently'), danger: true, run: () => run(() => purgeNote(n)) }]); });
function edited(prop, value) { const n = current; run(async () => { if (!n || n.Deleted) return; await commitDraft(n); n[prop] = value; touch(n); $('noteDate').textContent = fmt.full.format(new Date(n.Updated)); await localSave(n); }); }
$('title').addEventListener('input', () => { autosize($('title')); edited('Title', $('title').value); });
const bodyEl = $('body');
bodyEl.addEventListener('input', () => edited('Text', Checklist.getBody(bodyEl)));
// Only plain text comes in; iOS would otherwise paste styled fragments into the note.
bodyEl.addEventListener('paste', e => { e.preventDefault(); document.execCommand('insertText', false, e.clipboardData.getData('text/plain')); });
bodyEl.addEventListener('keydown', e => {
  if (current?.Deleted) return;
  if (e.key === 'Enter' && !e.shiftKey && Checklist.enter(bodyEl)) { e.preventDefault(); edited('Text', Checklist.getBody(bodyEl)); }
  else if (e.key === 'Backspace' && Checklist.backspace(bodyEl)) { e.preventDefault(); edited('Text', Checklist.getBody(bodyEl)); }
});
// A tap on an item's circle flips it without opening the keyboard.
bodyEl.addEventListener('pointerdown', e => { if (!current?.Deleted && Checklist.tapToggle(bodyEl, e)) { e.preventDefault(); edited('Text', Checklist.getBody(bodyEl)); } });
$('checklist').addEventListener('click', () => { if (!current || current.Deleted) return; bodyEl.focus(); Checklist.toggleSelection(bodyEl); edited('Text', Checklist.getBody(bodyEl)); });
for (const el of [$('title'), bodyEl]) {
  el.addEventListener('focus', () => $('done').hidden = false);
  el.addEventListener('blur', () => setTimeout(() => { if (document.activeElement !== $('title') && document.activeElement !== bodyEl) $('done').hidden = true; }, 50));
}
$('title').addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); bodyEl.focus(); if (bodyEl.firstChild) Checklist.placeCaret(bodyEl.firstChild); } });
$('attach').addEventListener('click', () => $('files').click());
$('files').addEventListener('change', () => {
  const files = [...$('files').files], n = current; $('files').value = '';
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
    if (ready) send(await manifest());
    await renderAttachments(n); toast(T('attachmentSaved'));
  });
});
for (const page of document.querySelectorAll('.page')) page.addEventListener('scroll', () => page.previousElementSibling.classList.toggle('scrolled', page.scrollTop > 4), { passive: true });
document.addEventListener('visibilitychange', () => { if (!document.hidden) run(async () => { if (ready) { await flushAll(); send(await manifest()); } else { retryDelay = RETRY_MIN; connect(); } }); });
window.addEventListener('online', () => { retryDelay = RETRY_MIN; connect(); });
window.addEventListener('pagehide', () => { for (const [, t] of sendTimers) clearTimeout(t); });

// ---------- start ----------
applyLang();
run(async () => {
  db = await new Promise((resolve, reject) => {
    const r = indexedDB.open('notebook', 2);
    r.onupgradeneeded = () => { for (const name of ['meta', 'notes', 'files', 'pending', 'base']) if (!r.result.objectStoreNames.contains(name)) r.result.createObjectStore(name); };
    r.onsuccess = () => resolve(r.result); r.onerror = () => reject(r.error);
  });
  keys = await get('meta', 'keys'); device = await get('meta', 'device');
  if (!device) { device = id(); await put('meta', 'device', device); }
  purges = await get('meta', 'purges') || [];
  if (keys) for (const row of await request('notes', 'readonly', s => s.getAll())) notes.push(await open(keys, row.id, row.rev, row.blob));
  if (location.hash.includes('k=')) await pair(location.href);
  if (needsInstall()) show('install');
  else if (keys) { show('list'); renderList(); connect(); navigator.storage?.persist?.().catch(() => {}); }
  else show('pair');
  if ('serviceWorker' in navigator) try {
    // A first-generation worker (cache "notebook-phone-v1") served the old design cache-first and could sit on a
    // phone for a long time; when its cache is around, drop every registration and cache before registering anew.
    const stale = (await caches.keys()).some(k => { const m = /^notebook-phone-v(\d+)$/.exec(k); return !m || Number(m[1]) < 10; });
    if (stale) { for (const r of await navigator.serviceWorker.getRegistrations()) await r.unregister(); for (const k of await caches.keys()) await caches.delete(k); }
    await navigator.serviceWorker.register('/sw.js');
  } catch { /* offline copy is optional */ }
  if (location.pathname !== '/') history.replaceState(null, '', '/');
  $('skipInstall').addEventListener('click', () => { installSkipped = true; try { sessionStorage.setItem('skipInstall', '1'); } catch { } if (keys) { show('list'); renderList(); connect(); } else show('pair'); });
});
