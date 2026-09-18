const CACHE='notebook-phone-v4';
const FILES=['/','/index.html','/app.js','/crypto.js','/style.css','/app.webmanifest','/icon.png','/icon-180.png'];
self.addEventListener('install',e=>{self.skipWaiting();e.waitUntil(caches.open(CACHE).then(c=>c.addAll(FILES)));});
self.addEventListener('activate',e=>e.waitUntil(caches.keys().then(keys=>Promise.all(keys.filter(k=>k!==CACHE).map(k=>caches.delete(k)))).then(()=>self.clients.claim())));
// Network first so an updated app arrives on the next open; the cache keeps it working offline.
self.addEventListener('fetch',e=>{const u=new URL(e.request.url);if(u.origin!==location.origin||e.request.method!=='GET'||!FILES.includes(u.pathname))return;e.respondWith(fetch(e.request).then(r=>{if(r.ok)caches.open(CACHE).then(c=>c.put(u.pathname,r.clone()));return r;}).catch(()=>caches.match(u.pathname)));});
