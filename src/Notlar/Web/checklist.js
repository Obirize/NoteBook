// Checklist items are plain lines that start with "○" (open) or "●" (done) and a space — the same text the PC
// writes, so it syncs and exports as it is. The note body is an editable area where each line is a block;
// item blocks get a round check box and, when done, a line through the text.
export const OPEN = '○', DONE = '●', GAP = ' ';
const MARK = /^([○●])[  ]?/;
export const isItem = line => MARK.test(line);
export const isDone = line => line.startsWith(DONE);
export const body = line => line.replace(MARK, '');
export const toggle = line => isItem(line) ? (isDone(line) ? OPEN : DONE) + GAP + body(line) : line;
export const preview = text => text.replace(/●[  ]/g, '✓ ').replace(/○[  ]/g, '');

// ---------- the editable body ----------
export function setBody(el, text) {
  el.replaceChildren();
  for (const line of text.split('\n')) el.append(block(line));
}
function block(line) {
  const div = document.createElement('div');
  if (isItem(line)) { div.className = 'item' + (isDone(line) ? ' done' : ''); }
  const t = body(line);
  if (t) div.textContent = t; else div.append(document.createElement('br'));
  return div;
}
// Reads the lines back. Safari may leave a bare text node or a nested block behind; both are handled.
export function getBody(el) {
  const lines = [];
  for (const node of el.childNodes) {
    if (node.nodeType === Node.TEXT_NODE) { lines.push(node.textContent); continue; }
    if (node.nodeType !== Node.ELEMENT_NODE) continue;
    if (node.tagName === 'BR') { lines.push(''); continue; }
    const text = blockText(node).split('\n');
    const prefix = node.classList.contains('item') ? (node.classList.contains('done') ? DONE : OPEN) + GAP : '';
    lines.push(prefix + text[0]);
    for (const extra of text.slice(1)) lines.push(extra);
  }
  return lines.join('\n');
}
function blockText(node) {
  let out = '';
  for (const child of node.childNodes) {
    if (child.nodeType === Node.TEXT_NODE) out += child.textContent;
    else if (child.tagName === 'BR') out += '\n';
    else if (child.nodeType === Node.ELEMENT_NODE) out += (/^(DIV|P)$/.test(child.tagName) && out && !out.endsWith('\n') ? '\n' : '') + blockText(child);
  }
  return out.replace(/\n$/, '');
}
// The block that holds the caret (a direct child of the editor), or null.
export function currentBlock(el) {
  const sel = window.getSelection(); if (!sel || sel.rangeCount === 0) return null;
  let node = sel.getRangeAt(0).startContainer;
  if (node === el) node = el.childNodes[sel.getRangeAt(0).startOffset] || el.lastChild;
  while (node && node.parentNode !== el) node = node.parentNode;
  return node && node.nodeType === Node.ELEMENT_NODE ? node : null;
}
export function caretAtStart(el) {
  const sel = window.getSelection(); if (!sel || sel.rangeCount === 0) return false;
  const r = sel.getRangeAt(0); if (!r.collapsed) return false;
  const b = currentBlock(el); if (!b) return false;
  const probe = r.cloneRange(); probe.selectNodeContents(b); probe.setEnd(r.startContainer, r.startOffset);
  return probe.toString().length === 0;
}
export function placeCaret(node, atEnd = false) {
  const r = document.createRange(); r.selectNodeContents(node); r.collapse(!atEnd);
  const sel = window.getSelection(); sel.removeAllRanges(); sel.addRange(r);
}
// Enter inside an item: a new item after it (with whatever text followed the caret); on an empty item the list ends.
export function enter(el) {
  const b = currentBlock(el); if (!b || !b.classList.contains('item')) return false;
  if (!blockText(b).trim()) { b.className = ''; return true; }
  const sel = window.getSelection(); const r = sel.getRangeAt(0);
  const tail = r.cloneRange(); tail.selectNodeContents(b); tail.setStart(r.endContainer, r.endOffset);
  const rest = tail.toString(); tail.deleteContents();
  if (!blockText(b)) { b.replaceChildren(document.createElement('br')); }
  const next = document.createElement('div'); next.className = 'item';
  if (rest) next.textContent = rest; else next.append(document.createElement('br'));
  b.after(next); placeCaret(next);
  return true;
}
// Backspace at the start of an item turns it back into plain text.
export function backspace(el) {
  const b = currentBlock(el); if (!b || !b.classList.contains('item') || !caretAtStart(el)) return false;
  b.className = ''; return true;
}
// The checklist button: every block in the selection becomes an item; if all already are, they stop being items.
export function toggleSelection(el) {
  const sel = window.getSelection();
  let blocks = [];
  if (sel && sel.rangeCount) {
    const r = sel.getRangeAt(0);
    for (const child of el.children) if (r.intersectsNode(child)) blocks.push(child);
  }
  if (blocks.length === 0) {
    if (!el.firstElementChild) { const d = document.createElement('div'); d.append(document.createElement('br')); el.append(d); }
    blocks = [currentBlock(el) || el.lastElementChild];
  }
  const all = blocks.every(b => b.classList.contains('item'));
  for (const b of blocks) { if (all) b.className = ''; else if (!b.classList.contains('item')) b.className = 'item'; }
  if (blocks.length === 1 && !caretInside(el)) placeCaret(blocks[0], true);
}
function caretInside(el) { const sel = window.getSelection(); return !!(sel && sel.rangeCount && el.contains(sel.getRangeAt(0).startContainer)); }
// A tap on the circle (left edge of an item) flips it without opening the keyboard.
export function tapToggle(el, event) {
  const target = event.target.closest?.('.item'); if (!target || target.parentNode !== el) return false;
  const rect = target.getBoundingClientRect();
  const x = (event.touches ? event.touches[0].clientX : event.clientX) - rect.left;
  const rtl = getComputedStyle(el).direction === 'rtl';
  if (rtl ? x < rect.width - 36 : x > 36) return false;
  target.classList.toggle('done'); return true;
}
