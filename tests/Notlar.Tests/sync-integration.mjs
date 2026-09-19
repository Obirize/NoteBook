import assert from 'node:assert/strict';
import {derive,random,b64,un64,mac,verifyMac,open,seal,encryptFile,decryptFile,id} from '../../src/Notlar/Web/crypto.js';
import * as Checklist from '../../src/Notlar/Web/checklist.js';
// The phone reads and writes the same checklist markers as the PC (Checklist.cs): "○"/"●" + em space.
const O='○', D='●', G=' ', NL=String.fromCharCode(10);
assert(Checklist.isItem(O+G+'Süt')&&Checklist.isDone(D+G+'Süt')&&!Checklist.isItem('Süt'));
assert.equal(Checklist.body(D+G+'Ekmek'),'Ekmek');assert.equal(Checklist.body(O+' Ekmek'),'Ekmek');
assert.equal(Checklist.toggle(O+G+'a'),D+G+'a');assert.equal(Checklist.toggle('plain'),'plain');
assert.equal(Checklist.preview('Plan'+NL+O+G+'Süt'+NL+D+G+'Ekmek'),'Plan'+NL+'Süt'+NL+'✓ Ekmek');
const port=process.argv[2],keys=await derive(un64(process.env.NOTEBOOK_TEST_KEY));
const page=await fetch(`https://localhost:${port}/`);assert.equal(page.status,200);assert.match(await page.text(),/<title>Notlar</);
for(const path of ['start','sw.js','v2/app.js','v2/crypto.js','v2/style.css','v2/app.webmanifest','v2/icon.png','v2/icon-180.png'])assert.equal((await fetch(`https://localhost:${port}/${path}`)).status,200);
assert.match(await (await fetch(`https://localhost:${port}/start`)).text(),/\/v2\/app\.js/);
const pairWrong=await fetch(`https://localhost:${port}/pair`,{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({code:'000000'})});assert.equal(pairWrong.status,403);
const pairOk=await fetch(`https://localhost:${port}/pair`,{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({code:process.env.NOTEBOOK_TEST_CODE})});assert.equal(pairOk.status,200);
const paired=await pairOk.json();assert.deepEqual(un64(paired.k),un64(process.env.NOTEBOOK_TEST_KEY));assert.equal(typeof paired.name,'string');
assert.equal((await fetch(`https://localhost:${port}/pair`,{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({code:process.env.NOTEBOOK_TEST_CODE})})).status,403);
const wrong=await derive(new Uint8Array(32));
async function client(k){const ws=new WebSocket(`wss://localhost:${port}/sync`);ws.binaryType='arraybuffer';const messages=[],waiters=[];ws.onmessage=e=>{const data=typeof e.data==='string'?JSON.parse(e.data):e.data;const waiter=waiters.shift();if(waiter)waiter(data);else messages.push(data);};const next=()=>messages.length?Promise.resolve(messages.shift()):new Promise(r=>waiters.push(r));await new Promise((resolve,reject)=>{ws.onopen=resolve;ws.onerror=reject;});const send=m=>ws.send(JSON.stringify(m));send({t:'hello',protocol:1,device:id(),name:'Integration test'});const challenge=await next(),cn=random(32),sn=un64(challenge.nonce);send({t:'auth',nonce:b64(cn),mac:b64(new Uint8Array(await mac(k,'client',sn,cn)))});const welcome=await next();return {ws,next,send,welcome,cn,sn};}
const bad=await client(wrong);assert.equal(bad.welcome.t,'rejected');bad.ws.close();
const c=await client(keys);assert.equal(c.welcome.t,'welcome');assert(await verifyMac(keys,un64(c.welcome.mac),'server',c.cn,c.sn));
c.send({t:'manifest',notes:[],purged:[],files:[]});let desktop,baseline,attachment;
while(true){const m=await c.next();if(m.t==='note'){desktop=await open(keys,m.id,m.rev,un64(m.blob));baseline={rev:m.rev,blob:m.blob};attachment=desktop.Attachments[0];}if(m.t==='done')break;}
assert.equal(desktop.Text,'from PC');c.send({t:'want-files',ids:[attachment.Id]});let chunks=[];
while(true){const m=await c.next();if(m instanceof ArrayBuffer)chunks.push(m);else if(m.t==='file-end')break;}
assert.equal(await (await decryptFile(new Blob(chunks),attachment)).text(),'desktop attachment');
const now=new Date().toISOString(),encrypted=await encryptFile(new File(['phone attachment'],'phone.png',{type:'image/png'}));
// A "video" larger than one chunk with a known byte pattern: the desktop must receive it bit for bit.
const clipBytes=new Uint8Array(2621440+123);for(let i=0;i<clipBytes.length;i++)clipBytes[i]=(i*7+3)&255;
const clip=await encryptFile(new File([clipBytes],'tempImage1234.mov',{type:''}));
assert.match(clip.meta.Name,/^IMG .*\.mov$/);assert.equal(clip.meta.MediaType,'video/quicktime');
assert.deepEqual(new Uint8Array(await (await decryptFile(clip.blob,clip.meta)).arrayBuffer()),clipBytes);
const phone={Id:id(),Title:'Phone',Text:'from phone 🔐',Created:now,Updated:now,Revision:1,Pinned:false,Deleted:false,DeletedAt:null,Attachments:[encrypted.meta,clip.meta]};
c.send({t:'note',id:phone.Id,rev:1,blob:b64(await seal(keys,phone))});c.send({t:'flush'});
while((await c.next()).t!=='flush'){}
c.send({t:'file',id:encrypted.meta.Id,size:encrypted.blob.size});c.ws.send(await encrypted.blob.arrayBuffer());c.send({t:'file-end',id:encrypted.meta.Id});
c.send({t:'file',id:clip.meta.Id,size:clip.blob.size});for(let at=0;at<clip.blob.size;at+=262144)c.ws.send(await clip.blob.slice(at,at+262144).arrayBuffer());c.send({t:'file-end',id:clip.meta.Id});
// Another device changes the note on the PC; this phone then uploads an offline edit of the old base with a higher revision.
const other=await client(keys);assert.equal(other.welcome.t,'welcome');
const changed={...desktop,Revision:2,Text:'PC-side edit'};
other.send({t:'note',id:changed.Id,rev:2,blob:b64(await seal(keys,changed))});other.send({t:'flush'});while((await other.next()).t!=='flush'){}other.ws.close();
const conflict={...desktop,Revision:7,Text:'offline phone edit'};
c.send({t:'note',id:conflict.Id,rev:7,blob:b64(await seal(keys,conflict)),base:baseline});c.send({t:'flush'});let found=false;
for(let i=0;i<30&&!found;i++){const m=await c.next();if(m.t==='note'){const n=await open(keys,m.id,m.rev,un64(m.blob));if(n.Id===desktop.Id)baseline={rev:m.rev,blob:m.blob};if(n.Id!==desktop.Id&&n.Text==='offline phone edit')found=true;}}
while((await c.next()).t!=='flush'){}
// A phone that types faster than the PC answers is not in conflict with itself.
const fast1={...desktop,Revision:8,Text:'typing a'},fast2={...desktop,Revision:9,Text:'typing ab'};
c.send({t:'note',id:fast1.Id,rev:8,blob:b64(await seal(keys,fast1)),base:baseline});c.send({t:'flush'});
c.send({t:'note',id:fast2.Id,rev:9,blob:b64(await seal(keys,fast2)),base:baseline});c.send({t:'flush'});
let flushes=0,copies=0;while(flushes<2){const m=await c.next();if(m.t==='flush')flushes++;if(m.t==='note'){const n=await open(keys,m.id,m.rev,un64(m.blob));if(n.Id!==desktop.Id&&n.Text.startsWith('typing'))copies++;}}
assert.equal(copies,0);
assert(found);c.ws.close();console.log('PASS checklist markers match the PC, pairing code exchange (wrong code refused, one-time use), fast typing without self-conflict, HTTPS resources, wrong-key rejection, mutual HMAC, C#/WebCrypto notes and attachments both ways, offline conflict preservation');
