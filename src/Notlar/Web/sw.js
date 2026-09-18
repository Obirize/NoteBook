const CACHE='notebook-phone-v1';
const FILES=['/','/index.html','/app.js','/crypto.js','/style.css','/app.webmanifest','/icon.png'];
self.addEventListener('install',e=>e.waitUntil(caches.open(CACHE).then(c=>c.addAll(FILES))));
self.addEventListener('activate',e=>e.waitUntil(caches.keys().then(keys=>Promise.all(keys.filter(k=>k!==CACHE).map(k=>caches.delete(k)))).then(()=>self.clients.claim())));
self.addEventListener('fetch',e=>{const u=new URL(e.request.url);if(u.origin!==location.origin||e.request.method!=='GET'||!FILES.includes(u.pathname))return;e.respondWith(caches.match(u.pathname).then(c=>c||fetch(e.request)));});
