const CACHE='notebook-phone-v7';
const FILES=['/','/index.html','/start','/v2/app.js','/v2/crypto.js','/v2/style.css','/v2/app.webmanifest','/v2/icon.png','/v2/icon-180.png'];
self.addEventListener('install',e=>{self.skipWaiting();e.waitUntil(caches.open(CACHE).then(c=>c.addAll(FILES)));});
// Old workers served the previous design cache-first; once this one takes over, open pages reload into the new one.
self.addEventListener('activate',e=>e.waitUntil(caches.keys().then(async keys=>{const old=keys.filter(k=>k!==CACHE);await Promise.all(old.map(k=>caches.delete(k)));await self.clients.claim();if(old.length)for(const c of await self.clients.matchAll({type:'window'}))c.navigate(c.url).catch(()=>{});})));
// Network first so an updated app arrives on the next open; the cache keeps it working offline.
self.addEventListener('fetch',e=>{const u=new URL(e.request.url);if(u.origin!==location.origin||e.request.method!=='GET'||!FILES.includes(u.pathname))return;e.respondWith(fetch(e.request).then(r=>{if(r.ok)caches.open(CACHE).then(c=>c.put(u.pathname,r.clone()));return r;}).catch(()=>caches.match(u.pathname)));});
