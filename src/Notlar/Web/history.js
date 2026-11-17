// Undo and redo for a note's text. The app lays out new lines itself (one block per line), which the browser's own
// undo cannot follow: undoing after a new line put the words back but left the line split. So the editor keeps its
// own list of earlier versions — the text and where the caret was — and answers the phone's undo and redo with them.
import * as Checklist from './checklist.js';

const LIMIT = 100, PAUSE = 1000;
let back = [], forward = [], lastKind = null, lastAt = 0;

export function resetHistory() { back = []; forward = []; lastKind = null; }
// The text and the caret as they are now; taken just before a change.
export function snapshot(el) { return { text: Checklist.getBody(el), caret: caretAt(el) }; }
// Keeps `state` as one step back. Typing (or deleting) without a pause is one step, as in the Notes app.
export function remember(el, kind, state = null) {
  const now = Date.now(), burst = kind !== 'other' && kind === lastKind && now - lastAt < PAUSE;
  lastKind = kind; lastAt = now;
  if (burst) return;
  back.push(state || snapshot(el)); if (back.length > LIMIT) back.shift(); forward = [];
}
export const undo = el => step(el, back, forward);
export const redo = el => step(el, forward, back);
function step(el, from, to) {
  const s = from.pop(); if (!s) return false;
  to.push(snapshot(el)); Checklist.setBody(el, s.text); placeAt(el, s.caret); lastKind = null;
  return true;
}

// The caret as [line, characters into that line], which survives the blocks being drawn again.
function caretAt(el) {
  const sel = getSelection(); if (!sel?.rangeCount || !el.contains(sel.focusNode)) return null;
  const block = Checklist.currentBlock(el); if (!block) return null;
  const r = document.createRange(); r.selectNodeContents(block); r.setEnd(sel.focusNode, sel.focusOffset);
  return [[...el.children].indexOf(block), r.toString().length];
}
function placeAt(el, caret) {
  if (!caret || !el.contains(document.activeElement)) return;
  const block = el.children[caret[0]] || el.lastElementChild; if (!block) return;
  const walker = document.createTreeWalker(block, NodeFilter.SHOW_TEXT); let left = caret[1], node;
  while ((node = walker.nextNode())) {
    if (left <= node.length) { const r = document.createRange(); r.setStart(node, left); r.collapse(true); const sel = getSelection(); sel.removeAllRanges(); sel.addRange(r); return; }
    left -= node.length;
  }
  Checklist.placeCaret(block, true);
}
