// IndexedDB: notes are stored sealed with the sync key (store "notes"), attachments as the encrypted files the
// PC also keeps ("files"); "pending" marks notes not yet acknowledged by the PC, "base" remembers the version
// the PC last sent so it can tell an edit from a conflict, and "meta" holds the key, the device id and purges.
// A file that came from the PC is kept as numbered pieces in "parts" (it is written as it arrives, so a large video
// never has to fit in memory); its "files" entry then says how many pieces there are.
import { seal, open, id } from './crypto.js';
import { T } from './lang.js';
import { S } from './state.js';

export function request(store, mode, fn) {
  return new Promise((resolve, reject) => {
    const tx = S.db.transaction(store, mode), r = fn(tx.objectStore(store));
    tx.oncomplete = () => resolve(r?.result); tx.onerror = () => reject(tx.error); tx.onabort = () => reject(tx.error || Error(T('storage')));
  });
}
export const get = (s, k) => request(s, 'readonly', t => t.get(k));
export const put = (s, k, v) => request(s, 'readwrite', t => t.put(v, k));
export const del = (s, k) => request(s, 'readwrite', t => t.delete(k));

// An attachment's encrypted file as one Blob, whichever way it was stored; null if this phone does not have it.
// The pieces stay on disk: the Blob only points at them until something reads a slice.
const pieces = aid => IDBKeyRange.bound(aid + '/', aid + '0', false, true);   // '0' is the character after '/'
export const pieceKey = (aid, i) => aid + '/' + String(i).padStart(6, '0');
export async function storedFile(aid) {
  const v = await get('files', aid);
  if (!v || v instanceof Blob) return v || null;
  const whole = await piecesOf(aid);
  return whole.count === v.parts ? whole : null;
}
export async function piecesOf(aid) { const parts = await request('parts', 'readonly', s => s.getAll(pieces(aid))); return Object.assign(new Blob(parts), { count: parts.length }); }
export const storedIds = async () => new Set(await request('files', 'readonly', s => s.getAllKeys()));
export const dropPieces = aid => request('parts', 'readwrite', s => s.delete(pieces(aid)));

export function touch(n) { n.Revision++; n.Updated = new Date().toISOString(); }
export const same = (a, b) => a.Title === b.Title && a.Text === b.Text && a.Pinned === b.Pinned && !!a.Archived === !!b.Archived && a.Deleted === b.Deleted && JSON.stringify(a.Attachments) === JSON.stringify(b.Attachments);
export const persist = async n => put('notes', n.Id, { id: n.Id, rev: n.Revision, blob: await seal(S.keys, n) });
export const savePurges = () => put('meta', 'purges', S.purges);
export async function forget(n) { await del('notes', n.Id); await del('pending', n.Id); S.notes = S.notes.filter(x => x !== n); }

export async function openDatabase() {
  S.db = await new Promise((resolve, reject) => {
    const r = indexedDB.open('notebook', 3);
    r.onupgradeneeded = () => { for (const name of ['meta', 'notes', 'files', 'pending', 'base', 'parts']) if (!r.result.objectStoreNames.contains(name)) r.result.createObjectStore(name); };
    r.onsuccess = () => resolve(r.result); r.onerror = () => reject(r.error);
  });
  S.keys = await get('meta', 'keys'); S.device = await get('meta', 'device');
  if (!S.device) { S.device = id(); await put('meta', 'device', S.device); }
  S.purges = await get('meta', 'purges') || [];
  // Pieces of a download that was cut off are useless: the file is asked for again from the start.
  const have = await storedIds();
  for (const aid of new Set((await request('parts', 'readonly', s => s.getAllKeys())).map(k => k.slice(0, k.indexOf('/'))))) if (!have.has(aid)) await dropPieces(aid);
  if (S.keys) for (const row of await request('notes', 'readonly', s => s.getAll())) S.notes.push(await open(S.keys, row.id, row.rev, row.blob));
}
