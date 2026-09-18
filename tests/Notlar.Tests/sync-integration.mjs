import assert from 'node:assert/strict';
import {derive,random,b64,un64,mac,verifyMac,open,seal,encryptFile,decryptFile,id} from '../../src/Notlar/Web/crypto.js';
const port=process.argv[2],keys=await derive(un64(process.env.NOTEBOOK_TEST_KEY));
const page=await fetch(`https://localhost:${port}/`);assert.equal(page.status,200);assert.match(await page.text(),/NoteBook/);
for(const path of ['app.js','crypto.js','sw.js','style.css','app.webmanifest','icon.png'])assert.equal((await fetch(`https://localhost:${port}/${path}`)).status,200);
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
const phone={Id:id(),Title:'Phone',Text:'from phone 🔐',Created:now,Updated:now,Revision:1,Pinned:false,Deleted:false,DeletedAt:null,Attachments:[encrypted.meta]};
c.send({t:'note',id:phone.Id,rev:1,blob:b64(await seal(keys,phone))});c.send({t:'flush'});
while((await c.next()).t!=='flush'){}
c.send({t:'file',id:encrypted.meta.Id,size:encrypted.blob.size});c.ws.send(await encrypted.blob.arrayBuffer());c.send({t:'file-end',id:encrypted.meta.Id});
// First update the PC, then upload an independently edited higher revision with the old base.
const changed={...desktop,Revision:2,Text:'PC-side edit'};
c.send({t:'note',id:changed.Id,rev:2,blob:b64(await seal(keys,changed))});c.send({t:'flush'});while((await c.next()).t!=='flush'){}
const conflict={...desktop,Revision:7,Text:'offline phone edit'};
c.send({t:'note',id:conflict.Id,rev:7,blob:b64(await seal(keys,conflict)),base:baseline});c.send({t:'flush'});let found=false;
while(true){const m=await c.next();if(m.t==='note'){const n=await open(keys,m.id,m.rev,un64(m.blob));if(n.Id!==desktop.Id&&n.Text==='offline phone edit')found=true;}if(m.t==='flush')break;}
assert(found);c.ws.close();console.log('PASS pairing code exchange (wrong code refused, one-time use), HTTPS resources, wrong-key rejection, mutual HMAC, C#/WebCrypto notes and attachments both ways, offline conflict preservation');
