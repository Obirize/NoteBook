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

const SEND_DELAY = 400, RETRY_MIN = 3000, RETRY_MAX = 20000;
let socket = null, ready = false, receiving = null, serverNonce, clientNonce, retry, retryDelay = RETRY_MIN, lastSynced = null;
const sendTimers = new Map();

export const isReady = () => ready;
export const send = m => { if (ready && socket?.readyState === 1) socket.send(JSON.stringify(m)); };

// ---------- local edits ----------
// Saved at once; sent to the PC after a short pause so a burst of keystrokes travels as one revision.
export async function localSave(n, immediate = false) {
  await persist(n); await put('pending', n.Id, true);
  clearTimeout(sendTimers.get(n.Id));
  if (immediate) await flushNote(n); else sendTimers.set(n.Id, setTimeout(() => run(() => flushNote(n)), SEND_DELAY));
}
async function flushNote(n) { sendTimers.delete(n.Id); if (!ready) return; send({ t: 'note', id: n.Id, rev: n.Revision, blob: b64(await seal(S.keys, n)), base: await get('base', n.Id) }); send({ t: 'flush' }); }
export async function flushAll() { for (const n of S.notes) if (await get('pending', n.Id)) await flushNote(n); }
export function cancelPendingSends() { for (const t of sendTimers.values()) clearTimeout(t); }
export async function announcePurge(n) { S.purges.push({ id: n.Id, rev: n.Revision }); await savePurges(); send({ t: 'purge', id: n.Id, rev: n.Revision }); send({ t: 'flush' }); }
export async function syncNow() { if (ready) { setStatus(T('syncing'), true); await flushAll(); send(await manifest()); } else connect(); }

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
    case 'challenge': serverNonce = un64(m.nonce); clientNonce = random(32); socket.send(JSON.stringify({ t: 'auth', nonce: b64(clientNonce), mac: b64(new Uint8Array(await mac(S.keys, 'client', serverNonce, clientNonce))) })); break;
    case 'welcome':
      if (!await verifyMac(S.keys, un64(m.mac), 'server', clientNonce, serverNonce)) { socket.close(); throw Error(T('pcNotVerified')); }
      ready = true; retryDelay = RETRY_MIN; setStatus(T('syncing'), true); send(await manifest()); break;
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
    case 'rejected': socket.close(); throw Error(T('rejected'));
  }
}
export function connect() {
  clearTimeout(retry);
  if (!S.keys || (socket && socket.readyState < 2)) return;
  ready = false; setStatus(T('connecting'), true);
  const ws = new WebSocket('wss://' + location.host + '/sync'); socket = ws; ws.binaryType = 'arraybuffer';
  ws.onopen = () => ws.send(JSON.stringify({ t: 'hello', protocol: 1, device: S.device, name: T('device') }));
  ws.onmessage = e => run(async () => { if (socket !== ws) return; try { await handle(e.data); } catch (error) { ws.close(); throw error; } });
  ws.onclose = () => {
    if (socket !== ws) return;
    ready = false; receiving = null;
    setStatus(lastSynced ? T('offlineLast', fmt.time.format(lastSynced)) : T('offlineLocal'));
    retry = setTimeout(connect, retryDelay); retryDelay = Math.min(RETRY_MAX, retryDelay * 2);
  };
  ws.onerror = () => {};
}
export function reconnectSoon() { retryDelay = RETRY_MIN; connect(); }

// ---------- pairing ----------
async function applyKey(raw) {
  if (raw.length !== 32) throw Error(T('pairInvalid'));
  if (S.keys && !confirm(T('pairAgain'))) return;
  S.keys = await derive(raw); raw.fill(0);
  await put('meta', 'keys', S.keys); for (const n of S.notes) await persist(n);
  history.replaceState(null, '', '/'); $('pairLink').value = ''; $('pairCode').value = '';
  show('list'); renderList(); socket?.close(); socket = null; connect();
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

// Coming back to the app, or back online, is the moment to catch up.
document.addEventListener('visibilitychange', () => { if (!document.hidden) run(async () => { if (ready) { await flushAll(); send(await manifest()); } else reconnectSoon(); }); });
window.addEventListener('online', reconnectSoon);
window.addEventListener('pagehide', cancelPendingSends);
