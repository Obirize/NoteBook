// What every part of the phone app shares: the notes in memory, the open note, which folder the list shows, and
// the selection. Modules import this object and change it in place; nothing else holds app state.
import { T } from './lang.js';

export const $ = x => document.getElementById(x);
export const MAX_FILE = 256 * 1024 * 1024;

export const S = {
  db: null, keys: null, device: null,
  notes: [], purges: [],
  current: null,                 // the note open in the editor
  folder: 'all',                 // 'all' | 'archive' | 'trash'
  selecting: false, selected: new Set(),
  status: null,                  // { text, kind: 'ok' | 'busy' | 'offline' } from the link
  shown: 0,                      // how many notes the list currently shows
};
export const inTrash = () => S.folder === 'trash';
export const inArchive = () => S.folder === 'archive';
// Which of the three lists a note belongs to: the main list, the archive, or Recently Deleted.
export const inFolder = n => inTrash() ? n.Deleted : !n.Deleted && !!n.Archived === inArchive();
export const folderTitle = () => inTrash() ? T('recentlyDeleted') : inArchive() ? T('archive') : T('notes');
export const isEmpty = n => !n.Title.trim() && !n.Text.trim() && n.Attachments.length === 0;
