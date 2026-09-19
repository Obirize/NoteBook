const CACHE='notebook-phone-v9';
const FILES=['/','/index.html','/start','/v2/app.js','/v2/crypto.js','/v2/style.css','/v2/app.webmanifest','/v2/icon.png','/v2/icon-180.png'];
self.addEventListener('install',e=>{self.skipWaiting();e.waitUntil(caches.open(CACHE).then(c=>c.addAll(FILES)));});
// Old workers served the previous design cache-first; once this one takes over, open pages reload into the new one.
self.addEventListener('activate',e=>e.waitUntil(caches.keys().then(async keys=>{const old=keys.filter(k=>k!==CACHE);await Promise.all(old.map(k=>caches.delete(k)));await self.clients.claim();if(old.length)for(const c of await self.clients.matchAll({type:'window'}))c.navigate(c.url).catch(()=>{});})));
// The PC is often off when the phone opens the app, and reaching a switched-off host can take long to fail.
// So the app shell comes from the cache at once and the network only refreshes it in the background;
// a newer version is then in place the next time the app opens.
self.addEventListener('fetch',e=>{
  const u=new URL(e.request.url);
  if(u.origin!==location.origin||e.request.method!=='GET'||!FILES.includes(u.pathname))return;
  e.respondWith(caches.match(u.pathname).then(cached=>{
    const refresh=fetch(e.request).then(r=>{if(r.ok)return caches.open(CACHE).then(c=>c.put(u.pathname,r.clone())).then(()=>r);return r;}).catch(()=>null);
    if(cached){e.waitUntil(refresh);return cached;}
    return refresh.then(r=>r||new Response('Bilgisayara ulaşılamıyor ve bu sayfa henüz telefona alınmamış.',{status:503,headers:{'content-type':'text/plain; charset=utf-8'}}));
  }));
});
