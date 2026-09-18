// The phone side of NoteBook: notes live encrypted in IndexedDB, travel sealed over a WebSocket to the PC
// (see SyncService.cs for the protocol) and are shown in a layout that follows Apple's Notes app.
import { id, random, b64, un64, derive, mac, verifyMac, seal, open, encryptFile, decryptFile } from './crypto.js';

const $ = x => document.getElementById(x);
const MAX_FILE = 256 * 1024 * 1024, SEND_DELAY = 400, RETRY_MIN = 3000, RETRY_MAX = 20000;
let db, keys, device, notes = [], purges = [], current = null, trash = false;
let socket = null, ready = false, receiving = null, serverNonce, clientNonce, retry, retryDelay = RETRY_MIN, lastSynced = null;
let queue = Promise.resolve();
const sendTimers = new Map(), thumbUrls = new Map(), noteUrls = [];

// ---------- small helpers ----------
function run(fn) { queue = queue.then(fn).catch(e => { console.error(e); toast(e.message || 'İşlem tamamlanamadı'); }); return queue; }
let toastTimer;
function toast(text) { const t = $('toast'); t.textContent = text; t.hidden = false; clearTimeout(toastTimer); toastTimer = setTimeout(() => t.hidden = true, 3200); }
function request(store, mode, fn) { return new Promise((resolve, reject) => { const tx = db.transaction(store, mode), r = fn(tx.objectStore(store)); tx.oncomplete = () => resolve(r?.result); tx.onerror = () => reject(tx.error); tx.onabort = () => reject(tx.error || Error('Depolama başarısız')); }); }
const get = (s, k) => request(s, 'readonly', t => t.get(k)), put = (s, k, v) => request(s, 'readwrite', t => t.put(v, k)), del = (s, k) => request(s, 'readwrite', t => t.delete(k));
const same = (a, b) => a.Title === b.Title && a.Text === b.Text && a.Pinned === b.Pinned && a.Deleted === b.Deleted && JSON.stringify(a.Attachments) === JSON.stringify(b.Attachments);
const persist = async n => put('notes', n.Id, { id: n.Id, rev: n.Revision, blob: await seal(keys, n) });
const savePurges = () => put('meta', 'purges', purges);
const send = m => { if (ready && socket?.readyState === 1) socket.send(JSON.stringify(m)); };
const fmt = { time: new Intl.DateTimeFormat('tr-TR', { hour: '2-digit', minute: '2-digit' }), day: new Intl.DateTimeFormat('tr-TR', { weekday: 'long' }), date: new Intl.DateTimeFormat('tr-TR', { day: 'numeric', month: 'short', year: 'numeric' }), month: new Intl.DateTimeFormat('tr-TR', { month: 'long', year: 'numeric' }), full: new Intl.DateTimeFormat('tr-TR', { day: 'numeric', month: 'long', year: 'numeric', hour: '2-digit', minute: '2-digit' }) };
function startOfDay(d) { const x = new Date(d); x.setHours(0, 0, 0, 0); return x; }
function dateLabel(iso) { const d = new Date(iso), today = startOfDay(new Date()), day = startOfDay(d); const diff = (today - day) / 86400000; if (diff < 1) return fmt.time.format(d); if (diff < 7) return fmt.day.format(d); return fmt.date.format(d); }
function sectionOf(n) { if (n.Pinned && !n.Deleted) return 'Sabitlenmiş'; const d = new Date(n.Updated), today = startOfDay(new Date()), diff = (today - startOfDay(d)) / 86400000; if (diff < 1) return 'Bugün'; if (diff < 2) return 'Dün'; if (diff < 7) return 'Önceki 7 Gün'; if (diff < 30) return 'Önceki 30 Gün'; return fmt.month.format(d); }
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
  const q = $('search').value.trim().toLocaleLowerCase('tr');
  const shown = notes.filter(n => n.Deleted === trash && (!q || (n.Title + ' ' + n.Text + ' ' + n.Attachments.map(a => a.Name).join(' ')).toLocaleLowerCase('tr').includes(q)))
    .sort((a, b) => Number(b.Pinned) - Number(a.Pinned) || Date.parse(b.Updated) - Date.parse(a.Updated));
  $('listTitle').textContent = trash ? 'Son Silinenler' : 'Notlar';
  $('folderBack').hidden = !trash; $('trashButton').hidden = trash; $('compose').hidden = trash;
  $('count').textContent = shown.length === 0 ? (trash ? 'Not yok' : 'Not Yok') : shown.length + ' Not';
  $('empty').hidden = shown.length > 0; $('empty').textContent = q ? 'Sonuç yok' : trash ? 'Silinen not yok' : 'Not yok';
  const sections = $('sections'); sections.replaceChildren();
  let group = null, lastSection = null;
  for (const n of shown) {
    const section = trash ? null : sectionOf(n);
    if (section !== lastSection) { if (section) { const h = document.createElement('div'); h.className = 'section-title'; h.textContent = section; sections.append(h); } group = document.createElement('div'); group.className = 'group'; sections.append(group); lastSection = section; }
    group.append(row(n));
  }
}
function row(n) {
  const el = document.createElement('div'); el.className = 'row';
  const action = document.createElement('div'); action.className = 'row-action' + (trash ? ' restore' : ''); action.textContent = trash ? 'Geri Yükle' : 'Sil';
  const inner = document.createElement('div'); inner.className = 'row-inner';
  const text = document.createElement('div'); text.className = 'row-text';
  const title = document.createElement('div'); title.className = 'row-title'; title.textContent = n.Title.trim() || 'Yeni Not';
  const sub = document.createElement('div'); sub.className = 'row-sub';
  const when = document.createElement('b'); when.textContent = dateLabel(n.Updated);
  sub.append(when, document.createTextNode(n.Text.replace(/\s+/g, ' ').trim() || (n.Attachments.length ? n.Attachments.length + ' ek' : 'Ek metin yok')));
  text.append(title, sub); inner.append(text);
  if (n.Pinned && trash) { const pin = pinIcon(); inner.append(pin); }
  const image = n.Attachments.find(a => a.MediaType.startsWith('image/'));
  if (image) { const img = document.createElement('img'); img.className = 'row-thumb'; img.alt = ''; inner.append(img); thumbnail(image).then(url => { if (url) img.src = url; else img.remove(); }); }
  el.append(action, inner);
  swipe(el, inner, () => run(() => trash ? restoreNote(n) : deleteNote(n)));
  inner.addEventListener('click', () => { if (!el.dataset.swiped) run(() => openNote(n)); });
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
function show(screen) { for (const s of ['pair', 'list', 'editor']) $(s).hidden = s !== screen; }
async function openNote(n) {
  current = n; show('editor');
  $('backLabel').textContent = trash ? 'Son Silinenler' : 'Notlar';
  $('noteDate').textContent = fmt.full.format(new Date(n.Updated));
  $('title').value = n.Title; $('body').value = n.Text; autosize($('title')); autosize($('body'));
  $('title').readOnly = $('body').readOnly = n.Deleted;
  $('pin').hidden = $('attach').hidden = n.Deleted; $('noteMore').hidden = n.Deleted; $('trashBar').hidden = !n.Deleted; $('trashNotice').hidden = !n.Deleted;
  $('pin').style.color = n.Pinned ? 'var(--accent)' : 'var(--muted)';
  await renderAttachments(n);
  $('editor').querySelector('.page').scrollTop = 0;
}
async function renderAttachments(n) {
  for (const url of noteUrls) URL.revokeObjectURL(url); noteUrls.length = 0;
  const box = $('attachments'); box.replaceChildren();
  for (const a of n.Attachments) {
    const item = document.createElement('div'); item.className = 'attachment';
    const encrypted = await get('files', a.Id);
    if (!encrypted) { const w = document.createElement('div'); w.className = 'file'; w.innerHTML = '<div><b></b>Bilgisayardan aktarılıyor…</div>'; w.querySelector('b').textContent = a.Name; item.append(w); }
    else {
      let url; try { url = URL.createObjectURL(await decryptFile(encrypted, a)); noteUrls.push(url); } catch { url = null; }
      if (url && a.MediaType.startsWith('image/')) { const img = document.createElement('img'); img.src = url; img.alt = a.Name; img.addEventListener('click', () => lightbox('img', url)); item.append(img); }
      else if (url && a.MediaType.startsWith('video/')) { const v = document.createElement('video'); v.src = url; v.controls = true; v.playsInline = true; v.preload = 'metadata'; item.append(v); }
      else { const w = document.createElement('div'); w.className = 'file'; w.innerHTML = '<div><b></b>' + (url ? 'Önizleme yok' : 'Dosya açılamadı') + '</div>'; w.querySelector('b').textContent = a.Name; item.append(w); }
    }
    if (!n.Deleted) {
      const remove = document.createElement('button'); remove.className = 'remove'; remove.setAttribute('aria-label', 'Eki kaldır');
      remove.innerHTML = '<svg viewBox="0 0 24 24"><path d="M6 6l12 12M18 6L6 18"/></svg>';
      remove.addEventListener('click', () => sheet(a.Name, [{ label: 'Eki Kaldır', danger: true, run: () => run(async () => { n.Attachments = n.Attachments.filter(x => x.Id !== a.Id); touch(n); await localSave(n, true); await renderAttachments(n); }) }]));
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

async function newNote() {
  const now = new Date().toISOString();
  const n = { Id: id(), Title: '', Text: '', Created: now, Updated: now, Pinned: false, Deleted: false, DeletedAt: null, Revision: 1, Attachments: [] };
  notes.push(n); trash = false; await localSave(n); await openNote(n); $('title').focus();
}
async function deleteNote(n) { n.Deleted = true; n.DeletedAt = new Date().toISOString(); touch(n); await localSave(n, true); if (current === n) closeEditor(); else renderList(); toast('Not Son Silinenler\'e taşındı'); }
async function restoreNote(n) { n.Deleted = false; n.DeletedAt = null; touch(n); await localSave(n, true); if (current === n) closeEditor(); else renderList(); toast('Not geri yüklendi'); }
async function purgeNote(n) { purges.push({ id: n.Id, rev: n.Revision }); await savePurges(); await del('notes', n.Id); await del('pending', n.Id); notes = notes.filter(x => x !== n); send({ t: 'purge', id: n.Id, rev: n.Revision }); send({ t: 'flush' }); closeEditor(); }
function closeEditor() { current = null; show('list'); renderList(); }

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
  if (current?.Id === n.Id) { current = n; if (!$('editor').hidden && document.activeElement !== $('title') && document.activeElement !== $('body')) await openNote(n); }
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
    if (!receiving) throw Error('Beklenmeyen dosya');
    receiving.parts.push(data); receiving.have += data.byteLength;
    if (receiving.have > receiving.size) throw Error('Dosya boyutu aşıldı');
    return;
  }
  const m = JSON.parse(data);
  switch (m.t) {
    case 'challenge': serverNonce = un64(m.nonce); clientNonce = random(32); socket.send(JSON.stringify({ t: 'auth', nonce: b64(clientNonce), mac: b64(new Uint8Array(await mac(keys, 'client', serverNonce, clientNonce))) })); break;
    case 'welcome':
      if (!await verifyMac(keys, un64(m.mac), 'server', clientNonce, serverNonce)) { socket.close(); throw Error('Bilgisayar doğrulanamadı'); }
      ready = true; retryDelay = RETRY_MIN; setStatus('Eşitleniyor…', true); send(await manifest()); break;
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
    case 'flush': case 'done': await wantFiles(); lastSynced = new Date(); setStatus('Şimdi güncellendi'); break;
    case 'want-files': await transfer(m.ids); break;
    case 'file': if (m.size < 24 || m.size > MAX_FILE + 65536 || !/^[a-f0-9]{32}$/.test(m.id)) throw Error('Geçersiz dosya'); receiving = { id: m.id, size: m.size, have: 0, parts: [] }; break;
    case 'file-end': {
      const r = receiving; receiving = null;
      if (!r || r.id !== m.id || r.have !== r.size) throw Error('Eksik dosya');
      const a = notes.flatMap(n => n.Attachments).find(a => a.Id === r.id); if (!a) break;
      const blob = new Blob(r.parts); await decryptFile(blob, a); await put('files', a.Id, blob);
      if (current?.Attachments.some(x => x.Id === a.Id)) await renderAttachments(current); else if (!$('list').hidden) renderList();
      break;
    }
    case 'rejected': socket.close(); throw Error('Eşleştirme kabul edilmedi. Bilgisayardaki kodla yeniden eşleştirin.');
  }
}
function connect() {
  clearTimeout(retry);
  if (!keys || (socket && socket.readyState < 2)) return;
  ready = false; setStatus('Bağlanıyor…', true);
  const ws = new WebSocket('wss://' + location.host + '/sync'); socket = ws; ws.binaryType = 'arraybuffer';
  ws.onopen = () => ws.send(JSON.stringify({ t: 'hello', protocol: 1, device, name: 'iPhone' }));
  ws.onmessage = e => run(async () => { if (socket !== ws) return; try { await handle(e.data); } catch (error) { ws.close(); throw error; } });
  ws.onclose = () => {
    if (socket !== ws) return;
    ready = false; receiving = null;
    setStatus(lastSynced ? 'Çevrimdışı · son eşitleme ' + fmt.time.format(lastSynced) : 'Çevrimdışı · notlar bu telefonda');
    retry = setTimeout(connect, retryDelay); retryDelay = Math.min(RETRY_MAX, retryDelay * 2);
  };
  ws.onerror = () => {};
}

// ---------- pairing ----------
async function applyKey(raw) {
  if (raw.length !== 32) throw Error('Eşleştirme bilgisi geçersiz');
  if (keys && !confirm('Eşleştirmeyi yenilemek mevcut notları yeni anahtarla şifreler. Devam edilsin mi?')) return;
  keys = await derive(raw); raw.fill(0);
  await put('meta', 'keys', keys); for (const n of notes) await persist(n);
  history.replaceState(null, '', '/'); $('pairLink').value = ''; $('pairCode').value = '';
  show('list'); renderList(); socket?.close(); socket = null; connect();
  if (!navigator.standalone) toast('Eşleşti. Şimdi Paylaş → Ana Ekrana Ekle; ana ekrandan açınca kodu bir kez daha girin.');
}
async function pair(link) { const u = new URL(link, location.href); if (u.origin !== location.origin) throw Error('Bu bağlantı farklı bir bilgisayara ait.'); await applyKey(un64(new URLSearchParams(u.hash.slice(1)).get('k') || '')); }
// The code shown on the PC fetches the sync key over TLS; wrong or stale codes are refused there.
async function pairWithCode(code) {
  code = code.replace(/\D/g, ''); if (code.length !== 6) throw Error('Bilgisayarda görünen 6 haneli kodu yazın.');
  const r = await fetch('/pair', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ code }) });
  if (!r.ok) throw Error('Kod kabul edilmedi. Bilgisayardaki eşitleme penceresi açık mı? Yeni kodla tekrar deneyin.');
  const { k } = await r.json(); await applyKey(un64(k));
}

// ---------- wiring ----------
$('pairButton').addEventListener('click', () => run(() => pairWithCode($('pairCode').value)));
$('pairCode').addEventListener('keydown', e => { if (e.key === 'Enter') $('pairButton').click(); });
$('pairCode').addEventListener('input', () => { const v = $('pairCode').value.replace(/\D/g, '').slice(0, 6); $('pairCode').value = v.length > 3 ? v.slice(0, 3) + ' ' + v.slice(3) : v; });
$('pairLinkButton').addEventListener('click', () => run(() => pair($('pairLink').value)));
$('search').addEventListener('input', renderList);
$('compose').addEventListener('click', () => run(newNote));
$('trashButton').addEventListener('click', () => { trash = true; renderList(); });
$('folderBack').addEventListener('click', () => { trash = false; renderList(); });
$('back').addEventListener('click', () => run(async () => { if (current) { for (const t of sendTimers.keys()) { clearTimeout(sendTimers.get(t)); } await flushAll(); } closeEditor(); }));
$('done').addEventListener('click', () => document.activeElement?.blur());
$('more').addEventListener('click', () => sheet(null, [
  { label: 'Şimdi Eşitle', run: () => run(async () => { if (ready) { setStatus('Eşitleniyor…', true); await flushAll(); send(await manifest()); } else connect(); }) },
  { label: 'Eşleştirmeyi Yenile', run: () => { show('pair'); $('pairCode').focus(); } },
]));
$('noteMore').addEventListener('click', () => { const n = current; if (!n) return; sheet(null, [
  { label: n.Pinned ? 'Sabitlemeyi Kaldır' : 'Sabitle', run: () => $('pin').click() },
  { label: 'Sil', danger: true, run: () => run(() => deleteNote(n)) },
]); });
$('pin').addEventListener('click', () => run(async () => { const n = current; if (!n || n.Deleted) return; n.Pinned = !n.Pinned; touch(n); await localSave(n, true); $('pin').style.color = n.Pinned ? 'var(--accent)' : 'var(--muted)'; }));
$('restore').addEventListener('click', () => run(() => restoreNote(current)));
$('purge').addEventListener('click', () => { const n = current; sheet('Bu not kalıcı olarak silinecek. Bu işlem geri alınamaz.', [{ label: 'Kalıcı Olarak Sil', danger: true, run: () => run(() => purgeNote(n)) }]); });
for (const [key, prop] of [['title', 'Title'], ['body', 'Text']]) {
  const el = $(key);
  el.addEventListener('input', () => { autosize(el); const n = current, value = el.value; run(async () => { if (!n || n.Deleted) return; n[prop] = value; touch(n); $('noteDate').textContent = fmt.full.format(new Date(n.Updated)); await localSave(n); }); });
  el.addEventListener('focus', () => $('done').hidden = false);
  el.addEventListener('blur', () => setTimeout(() => { if (document.activeElement !== $('title') && document.activeElement !== $('body')) $('done').hidden = true; }, 50));
}
$('title').addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); $('body').focus(); } });
$('attach').addEventListener('click', () => $('files').click());
$('files').addEventListener('change', () => {
  const files = [...$('files').files], n = current; $('files').value = '';
  run(async () => {
    if (!n || n.Deleted) return;
    for (const file of files) {
      if (file.size > MAX_FILE) throw Error('Telefonda ek başına sınır 256 MB.');
      if (!/^(image|video)\//.test(file.type) && !/\.(heic|heif|mov|mp4|m4v|jpe?g|png|gif|webp)$/i.test(file.name)) throw Error('Fotoğraf veya video seçin.');
      toast('Şifreleniyor… ' + (file.size / 1048576).toFixed(1) + ' MB, olduğu gibi');
      const encrypted = await encryptFile(file);
      await put('files', encrypted.meta.Id, encrypted.blob);
      n.Attachments.push(encrypted.meta); touch(n); await localSave(n, true);
    }
    if (ready) send(await manifest());
    await renderAttachments(n); toast('Ek şifreli olarak kaydedildi');
  });
});
for (const page of document.querySelectorAll('.page')) page.addEventListener('scroll', () => page.previousElementSibling.classList.toggle('scrolled', page.scrollTop > 4), { passive: true });
document.addEventListener('visibilitychange', () => { if (!document.hidden) run(async () => { if (ready) { await flushAll(); send(await manifest()); } else { retryDelay = RETRY_MIN; connect(); } }); });
window.addEventListener('online', () => { retryDelay = RETRY_MIN; connect(); });
window.addEventListener('pagehide', () => { for (const [, t] of sendTimers) clearTimeout(t); });

// ---------- start ----------
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
  if (keys) { show('list'); renderList(); connect(); navigator.storage?.persist?.().catch(() => {}); }
  else { show('pair'); }
  if ('serviceWorker' in navigator) await navigator.serviceWorker.register('/sw.js');
  if (keys && !navigator.standalone) toast('Safari → Paylaş → Ana Ekrana Ekle; sonra Notlar\'ı ana ekrandan açın.');
});
