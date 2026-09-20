// The phone side of NoteBook: notes live encrypted in IndexedDB, travel sealed over a WebSocket to the PC
// (see SyncService.cs for the protocol) and are shown in a layout that follows Apple's Notes app.
// This file only starts things up; list.js, editor.js, attachments.js and sync.js do the work.
import { applyLang } from './lang.js';
import { S, $ } from './state.js';
import { run, show, needsInstall, skipInstall } from './ui.js';
import { openDatabase } from './store.js';
import { connect, pair, pairWithCode } from './sync.js';
import { renderList } from './list.js';

function start() { show('list'); renderList(); connect(); navigator.storage?.persist?.().catch(() => {}); }

$('pairButton').addEventListener('click', () => run(() => pairWithCode($('pairCode').value)));
$('pairCode').addEventListener('input', () => { const v = $('pairCode').value.replace(/\D/g, '').slice(0, 6); $('pairCode').value = v.length > 3 ? v.slice(0, 3) + ' ' + v.slice(3) : v; });
$('pairLinkButton').addEventListener('click', () => run(() => pair($('pairLink').value)));
$('skipInstall').addEventListener('click', () => { skipInstall(); if (S.keys) start(); else show('pair'); });
for (const page of document.querySelectorAll('.page')) page.addEventListener('scroll', () => page.previousElementSibling.classList.toggle('scrolled', page.scrollTop > 4), { passive: true });

applyLang();
run(async () => {
  await openDatabase();
  if (location.hash.includes('k=')) await pair(location.href);
  if (needsInstall()) show('install');
  else if (S.keys) start();
  else show('pair');
  if ('serviceWorker' in navigator) try {
    await navigator.serviceWorker.register('/sw.js');
  } catch { /* offline copy is optional */ }
  if (location.pathname !== '/') history.replaceState(null, '', '/');
  window.notlarReady?.();
});
