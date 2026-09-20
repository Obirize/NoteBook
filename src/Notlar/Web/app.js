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
    // A first-generation worker (cache "notebook-phone-v1") served the old design cache-first and could sit on a
    // phone for a long time; when its cache is around, drop every registration and cache before registering anew.
    const stale = (await caches.keys()).some(k => { const m = /^notebook-phone-v(\d+)$/.exec(k); return !m || Number(m[1]) < 11; });
    if (stale) { for (const r of await navigator.serviceWorker.getRegistrations()) await r.unregister(); for (const k of await caches.keys()) await caches.delete(k); }
    await navigator.serviceWorker.register('/sw.js');
  } catch { /* offline copy is optional */ }
  if (location.pathname !== '/') history.replaceState(null, '', '/');
});
