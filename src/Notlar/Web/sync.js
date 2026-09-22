// The link to the PC: a WebSocket to the notebook's server (see SyncService.cs / SyncSession.cs for the protocol).
// Notes travel sealed with the shared key; attachments as their encrypted files. Everything is saved locally first,
// so the app works offline and catches up when the PC is back.
import { random, b64, un64, derive, mac, verifyMac, seal, open, decryptFile } from './crypto.js';
import { T } from './lang.js';
import { S, $, MAX_FILE } from './state.js';
import { run, toast, show, setStatus, fmt } from './ui.js';
import { request, get, put, del, same, persist, savePurges, forget } from './store.js';
import { renderList } from './list.js';
import { openNote, closeEditor } from './editor.js';
import { renderAttachments } from './attachments.js';
import { createLink } from './link.js';

const SEND_DELAY = 400, STALL_WAIT = 30000;
let receiving = null, uploading = false, serverNonce, clientNonce, lastSynced = null, link = null;
const sendTimers = new Map();

export const isReady = () => !!link?.isReady();
export const send = m => { link?.send(JSON.stringify(m)); };
const offlineText = () => lastSynced ? T('offlineLast', fmt.time.format(lastSynced)) : T('offlineLocal');
// The socket's life (timeouts, probes, retries, which host to try) lives in link.js; this file speaks the protocol.
async function ensureLink() {
  if (link) return link;
  const meta = await get('meta', 'link') || {};
  link = createLink({
    now: () => Date.now(), setTimeout: (f, ms) => setTimeout(f, ms), clearTimeout: id => clearTimeout(id), setInterval: (f, ms) => setInterval(f, ms), clearInterval: id => clearInterval(id),
    makeSocket: url => { const ws = new WebSocket(url); ws.binaryType = 'arraybuffer'; return ws; },
    name: location.host, port: location.port || 443, meta,
    saveMeta: m => put('meta', 'link', m).catch(() => {}),
    visible: () => !document.hidden, online: () => navigator.onLine !== false, busy: () => receiving != null || uploading,
    onOpen: ws => ws.send(JSON.stringify({ t: 'hello', protocol: 1, device: S.device, name: T('device'), hb: true, diag: link.diag() })),
    onFrame: (ws, data) => run(async () => { if (!link.owns(ws)) return; try { await handle(ws, data); } catch (error) { link.drop('error'); link.retry(); throw error; } }),
    onStatus: (kind, host) => { if (kind === 'connecting') setStatus(T('connecting'), 'busy'); else if (kind === 'ready') setStatus(T('syncing'), 'busy'); else setStatus(offlineText(), 'offline'); },
    onDrop: () => { receiving = null; },
    onHidden: () => run(flushAll),
    catchUp: () => run(async () => { await flushAll(); send(await manifest()); }),
  });
  return link;
}

// ---------- local edits ----------
// Saved at once; sent to the PC after a short pause so a burst of keystrokes travels as one revision.
export async function localSave(n, immediate = false) {
  await persist(n); await put('pending', n.Id, true);
  clearTimeout(sendTimers.get(n.Id));
  if (immediate) await flushNote(n); else sendTimers.set(n.Id, setTimeout(() => run(() => flushNote(n)), SEND_DELAY));
}
async function flushNote(n) { sendTimers.delete(n.Id); if (!isReady()) return; send({ t: 'note', id: n.Id, rev: n.Revision, blob: b64(await seal(S.keys, n)), base: await get('base', n.Id) }); send({ t: 'flush' }); }
export async function flushAll() { for (const n of S.notes) if (await get('pending', n.Id)) await flushNote(n); }
export function cancelPendingSends() { for (const t of sendTimers.values()) clearTimeout(t); }
export async function announcePurge(n) { S.purges.push({ id: n.Id, rev: n.Revision }); await savePurges(); send({ t: 'purge', id: n.Id, rev: n.Revision }); send({ t: 'flush' }); }
export async function syncNow() { setStatus(T('syncing'), 'busy'); (await ensureLink()).decide('manual'); }

// ---------- messages ----------
export async function manifest() { const files = await request('files', 'readonly', s => s.getAllKeys()); return { t: 'manifest', notes: S.notes.map(n => ({ id: n.Id, rev: n.Revision, updated: Date.parse(n.Updated) })), purged: S.purges, files }; }
async function wantFiles() { const missing = new Set(); for (const a of S.notes.flatMap(n => n.Attachments)) if (!await get('files', a.Id)) missing.add(a.Id); if (missing.size) send({ t: 'want-files', ids: [...missing] }); }
async function receiveNote(m) {
  const n = await open(S.keys, m.id, m.rev, un64(m.blob));
  if (S.purges.some(p => p.id === n.Id && p.rev >= n.Revision)) return;
  const old = S.notes.find(x => x.Id === n.Id);
  if (m.replyRev != null) {
    // The PC's answer to something we sent: remember it as the base; keep our newer local edits if any.
    await put('base', n.Id, { rev: m.rev, blob: m.blob });
    if (old && old.Revision > m.replyRev && await get('pending', old.Id)) return;
  } else {
    if (old && old.Revision > n.Revision) return;
    if (old && await get('pending', old.Id) && !same(old, n)) return;
  }
  if (old) S.notes[S.notes.indexOf(old)] = n; else S.notes.push(n);
  await persist(n); await put('base', n.Id, { rev: m.rev, blob: m.blob }); await del('pending', n.Id);
  if (S.current?.Id === n.Id) { S.current = n; if (!$('editor').hidden && document.activeElement !== $('title') && document.activeElement !== $('body')) await openNote(n); }
  if (!$('list').hidden) renderList();
}
async function transfer(ids) {
  uploading = true;
  try {
    for (const aid of ids) {
      const b = await get('files', aid); if (!b) continue;
      send({ t: 'file', id: aid, size: b.size });
      let lastDrain = Date.now(), lastBuffered = Infinity;
      for (let at = 0; at < b.size; at += 262144) {
        // A buffer that stops draining for 30 s means nobody is reading any more.
        while (isReady() && link.buffered() > 1048576) {
          if (link.buffered() < lastBuffered) lastDrain = Date.now();
          lastBuffered = link.buffered();
          if (Date.now() - lastDrain > STALL_WAIT) { link.drop('stall'); link.retry(); return; }
          await new Promise(r => setTimeout(r, 30));
        }
        if (!link.sendBinary(await b.slice(at, at + 262144).arrayBuffer())) return;
      }
      send({ t: 'file-end', id: aid });
    }
  } finally { uploading = false; }
}
async function handle(ws, data) {
  if (typeof data !== 'string') {
    if (!receiving) throw Error(T('unexpectedFile'));
    receiving.parts.push(data); receiving.have += data.byteLength;
    if (receiving.have > receiving.size) throw Error(T('sizeExceeded'));
    return;
  }
  const m = JSON.parse(data);
  switch (m.t) {
    case 'pong': break;
    case 'challenge': serverNonce = un64(m.nonce); clientNonce = random(32); ws.send(JSON.stringify({ t: 'auth', nonce: b64(clientNonce), mac: b64(new Uint8Array(await mac(S.keys, 'client', serverNonce, clientNonce))) })); break;
    case 'welcome':
      if (!await verifyMac(S.keys, un64(m.mac), 'server', clientNonce, serverNonce)) { link.drop('bad-mac'); link.retry(); throw Error(T('pcNotVerified')); }
      link.welcomed(ws, m); send(await manifest()); break;
    case 'manifest': {
      for (const n of S.notes) {
        const peer = m.notes.find(x => x.id === n.Id);
        if (!m.purged.some(p => p.id === n.Id && p.rev >= n.Revision) && (!peer || n.Revision > peer.rev || (n.Revision === peer.rev && (Date.parse(n.Updated) !== peer.updated || await get('pending', n.Id)))))
          send({ t: 'note', id: n.Id, rev: n.Revision, blob: b64(await seal(S.keys, n)), base: await get('base', n.Id) });
      }
      for (const p of S.purges) send({ t: 'purge', ...p });
      send({ t: 'done' }); break;
    }
    case 'note': await receiveNote(m); break;
    case 'purge': {
      const n = S.notes.find(x => x.Id === m.id);
      if (n && n.Revision <= m.rev) { await forget(n); if (S.current?.Id === n.Id) closeEditor(); }
      if (!S.purges.some(p => p.id === m.id && p.rev >= m.rev)) { S.purges = S.purges.filter(p => p.id !== m.id); S.purges.push({ id: m.id, rev: m.rev }); await savePurges(); }
      if (!$('list').hidden) renderList(); break;
    }
    case 'flush': case 'done': await wantFiles(); lastSynced = new Date(); setStatus(T('updated')); break;
    case 'want-files': await transfer(m.ids); break;
    case 'file': if (m.size < 24 || m.size > MAX_FILE + 65536 || !/^[a-f0-9]{32}$/.test(m.id)) throw Error(T('invalidFile')); receiving = { id: m.id, size: m.size, have: 0, parts: [] }; break;
    case 'file-end': {
      const r = receiving; receiving = null;
      if (!r || r.id !== m.id || r.have !== r.size) throw Error(T('incompleteFile'));
      const a = S.notes.flatMap(n => n.Attachments).find(a => a.Id === r.id); if (!a) break;
      const blob = new Blob(r.parts); await decryptFile(blob, a); await put('files', a.Id, blob);
      if (S.current?.Attachments.some(x => x.Id === a.Id)) await renderAttachments(S.current); else if (!$('list').hidden) renderList();
      break;
    }
    case 'rejected': link.drop('rejected'); link.retry(); throw Error(T('rejected'));
  }
}
export async function connect() { if (!S.keys) return; (await ensureLink()).connect(); }
export function reconnectSoon() { link?.decide('online'); }

// ---------- pairing ----------
async function applyKey(raw) {
  if (raw.length !== 32) throw Error(T('pairInvalid'));
  if (S.keys && !confirm(T('pairAgain'))) return;
  S.keys = await derive(raw); raw.fill(0);
  await put('meta', 'keys', S.keys); for (const n of S.notes) await persist(n);
  history.replaceState(null, '', '/'); $('pairLink').value = ''; $('pairCode').value = '';
  show('list'); renderList(); if (link) { link.drop('rekey'); link.forget(); } await connect();
  if (!navigator.standalone) toast(T('pairedNext'));
}
export async function pair(link) { const u = new URL(link, location.href); if (u.origin !== location.origin) throw Error(T('otherPc')); await applyKey(un64(new URLSearchParams(u.hash.slice(1)).get('k') || '')); }
// The code shown on the PC fetches the sync key over TLS; wrong or stale codes are refused there.
export async function pairWithCode(code) {
  code = code.replace(/\D/g, ''); if (code.length !== 6) throw Error(T('codeHint'));
  let r; try { r = await fetch('/pair', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ code }) }); }
  catch { throw Error(T('pcUnreachable', location.host)); }
  if (!r.ok) throw Error(T('codeRefused'));
  const { k } = await r.json(); await applyKey(un64(k));
}

// Coming back to the app, or back online, is the moment to ask whether the link is still alive.
document.addEventListener('visibilitychange', () => { if (!link) return; if (document.hidden) link.pause(); else link.decide('visible'); });
for (const ev of ['pageshow', 'focus', 'online']) window.addEventListener(ev, () => link?.decide(ev));
window.addEventListener('offline', () => { if (link?.isReady()) { link.drop('offline'); link.retry(); } });
window.addEventListener('pagehide', cancelPendingSends);
