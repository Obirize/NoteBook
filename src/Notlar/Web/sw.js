const CACHE='notebook-phone-v21';
const FILES=['/','/index.html','/start','/v3/app.js','/v3/state.js','/v3/ui.js','/v3/store.js','/v3/sync.js','/v3/link.js','/v3/list.js','/v3/editor.js','/v3/attachments.js','/v3/crypto.js','/v3/lang.js','/v3/checklist.js','/v3/style.css','/v3/app.webmanifest','/v3/icon.png','/v3/icon-180.png'];
self.addEventListener('install',e=>{self.skipWaiting();e.waitUntil(caches.open(CACHE).then(c=>c.addAll(FILES)));});
// Old workers served the previous design cache-first; once this one takes over, open pages reload into the new one.
self.addEventListener('activate',e=>e.waitUntil(caches.keys().then(async keys=>{const old=keys.filter(k=>k!==CACHE);await Promise.all(old.map(k=>caches.delete(k)));await self.clients.claim();if(old.length)for(const c of await self.clients.matchAll({type:'window'}))c.navigate(c.url).catch(()=>{});})));
// The PC is often off when the phone opens the app, and reaching a switched-off host can take long to fail, so the
// app shell always comes from the cache. New versions arrive only as a whole: a new worker with a new cache name
// installs every file, then takes over (never file by file into a running cache, which could leave a half-updated
// copy behind if the PC went away halfway).
self.addEventListener('fetch',e=>{
  const u=new URL(e.request.url);
  if(u.origin!==location.origin||e.request.method!=='GET'||!FILES.includes(u.pathname))return;
  e.respondWith(caches.match(u.pathname).then(cached=>cached||fetch(e.request).catch(()=>new Response('Bilgisayara ulaşılamıyor ve bu sayfa henüz telefona alınmamış.',{status:503,headers:{'content-type':'text/plain; charset=utf-8'}}))));
});
