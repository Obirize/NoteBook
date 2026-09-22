// The phone's link state machine under a fake clock and fake sockets: every wait is bounded, a silent socket is
// replaced, a paused app is noticed even without events, and the PC is tried by name and by address.
import assert from 'node:assert/strict';
import { createLink, DEFAULTS } from '../../src/Notlar/Web/link.js';

function harness({ meta = {}, options = {} } = {}) {
  let now = 0, seq = 0; const timers = new Map();
  const h = { sockets: [], hellos: 0, frames: [], statuses: [], drops: [], hidden: 0, catchUps: 0, visible: true, online: true, busy: false, saved: null };
  const io = {
    now: () => now,
    setTimeout: (f, ms) => { const id = ++seq; timers.set(id, { at: now + ms, f, every: 0 }); return id; }, clearTimeout: id => timers.delete(id),
    setInterval: (f, ms) => { const id = ++seq; timers.set(id, { at: now + ms, f, every: ms }); return id; }, clearInterval: id => timers.delete(id),
    makeSocket: url => { const ws = { url, host: '', readyState: 0, sent: [], bufferedAmount: 0, closed: false, send(x) { this.sent.push(x); }, close() { this.closed = true; this.readyState = 3; } }; h.sockets.push(ws); return ws; },
    name: 'pc.local:47831', port: 47831, meta, saveMeta: m => { h.saved = m; },
    visible: () => h.visible, online: () => h.online, busy: () => h.busy,
    onOpen: () => h.hellos++, onFrame: (ws, d) => h.frames.push(d), onStatus: k => h.statuses.push(k), onDrop: r => h.drops.push(r), onHidden: () => h.hidden++, catchUp: () => h.catchUps++,
  };
  h.link = createLink(io, options);
  // Advances the clock, firing due timers in order (a jump of `ms` in one step models a suspended page).
  h.advance = (ms, jump = false) => {
    const end = now + ms;
    if (jump) { now = end; }
    while (true) {
      const due = [...timers.entries()].filter(([, t]) => t.at <= (jump ? now : end)).sort((a, b) => a[1].at - b[1].at)[0];
      if (!due) break;
      const [id, t] = due; if (!jump) now = t.at; if (t.every) t.at = now + t.every; else timers.delete(id); t.f();
    }
    now = end;
  };
  h.open = ws => { ws.readyState = 1; ws.onopen(); };
  h.welcome = ws => { h.open(ws); h.link.welcomed(ws, { addresses: ['192.168.1.8'], port: 47831, idle: 60000 }); };
  h.last = () => h.sockets[h.sockets.length - 1];
  h.pings = ws => ws.sent.filter(x => typeof x === 'string' && x.includes('"ping"'));
  h.pong = (ws, n) => { const id = JSON.parse(h.pings(ws)[n ?? h.pings(ws).length - 1]).id; ws.onmessage({ data: JSON.stringify({ t: 'pong', id }) }); };
  return h;
}

// (a) a live link pings every 20 s while on screen; a ping nobody answers within 15 s replaces the socket
{
  const h = harness(); h.link.connect(); assert.equal(h.sockets.length, 1); h.welcome(h.last());
  assert(h.link.isReady()); h.advance(20000); assert.equal(h.pings(h.sockets[0]).length, 1);
  h.advance(15000); assert.equal(h.drops[0], 'no-pong'); assert.equal(h.sockets.length, 2); assert(h.sockets[0].closed);
  // a pong in time keeps the socket and re-arms
  h.welcome(h.last()); h.advance(20000); h.pong(h.last()); h.advance(15000); assert.equal(h.sockets.length, 2); h.advance(5000); assert.equal(h.pings(h.last()).length, 2);
}
// (b) an attempt that never opens ends at 10 s, retries on the ladder (1, 2, 3, 6, 12, 20 s), and rotates to the next address after 10 straight failures goes cold at 60 s
{
  const h = harness({ meta: { addrs: ['192.168.1.8'], port: 47831 } }); h.link.connect();
  h.advance(2999); assert.equal(h.sockets.length, 1); h.advance(1); assert.equal(h.sockets.length, 2, 'the address is tried 3 s after the name');
  assert.equal(h.sockets[1].url, 'wss://192.168.1.8:47831/sync');
  h.advance(7000); assert.equal(h.drops[0], 'timeout'); assert(h.sockets[0].closed && h.sockets[1].closed);
  h.advance(1000); assert.equal(h.sockets.length, 3, 'first retry after 1 s'); assert.equal(h.sockets[2].url, 'wss://192.168.1.8:47831/sync', 'a timeout rotates to the next candidate');
  let count = h.sockets.length; h.advance(10000 + 3000 + 2000); assert(h.sockets.length > count);
  for (let i = 0; i < 12; i++) h.advance(60000);
  assert(h.link.state().failures >= 10); let before = h.statuses.length; h.advance(59000); assert(h.statuses.slice(before).filter(x => x === 'connecting').length <= 1, 'cold cadence: at most one attempt per minute');
}
// (c) the address answering first wins and the name attempt is abandoned; the winner becomes lastGood
{
  const h = harness({ meta: { addrs: ['192.168.1.8'], port: 47831 } }); h.link.connect(); h.advance(3000);
  const [byName, byAddress] = h.sockets; h.welcome(byAddress);
  assert(byName.closed && !byAddress.closed && h.link.isReady()); assert.equal(h.saved.lastGood, '192.168.1.8:47831');
  assert.equal(h.link.state().candidates[0], '192.168.1.8:47831');
}
// (d) back on screen after a short pause: the socket is asked; no answer in 4 s means a new socket
{
  const h = harness(); h.link.connect(); h.welcome(h.last());
  h.visible = false; h.link.pause(); assert.equal(h.hidden, 1); h.advance(30000, true);
  h.visible = true; h.link.decide('visible'); assert.equal(h.pings(h.sockets[0]).length, 1);
  h.advance(4000); assert.equal(h.drops[0], 'no-pong'); assert.equal(h.sockets.length, 2);
  // with the pong, the catch-up runs and nothing is dropped
  h.welcome(h.last()); h.visible = false; h.link.pause(); h.advance(20000, true); h.visible = true; h.link.decide('visible');
  h.pong(h.last()); assert.equal(h.catchUps, 1); h.advance(4000); assert.equal(h.sockets.length, 2);
}
// (e) away longer than the PC's idle limit: no probe, straight to a new socket
{
  const h = harness(); h.link.connect(); h.welcome(h.last());
  h.visible = false; h.link.pause(); h.advance(3600000, true); h.visible = true; h.link.decide('visible');
  assert.equal(h.pings(h.sockets[0]).length, 0); assert.equal(h.drops[0], 'expired'); assert.equal(h.sockets.length, 2);
}
// (f) no visibility events at all: the tick notices the jump and acts
{
  const h = harness(); h.link.connect(); h.welcome(h.last());
  h.advance(5000); h.advance(120000, true); assert.equal(h.drops[0], 'expired'); assert.equal(h.sockets.length, 2);
}
// (g) during a download the probe is lenient: any incoming frame proves life, and the deadline is 30 s
{
  const h = harness(); h.link.connect(); h.welcome(h.last()); h.busy = true;
  h.visible = false; h.link.pause(); h.advance(20000, true); h.visible = true; h.link.decide('visible');
  assert.equal(h.pings(h.last()).length, 1);
  h.advance(10000); h.last().onmessage({ data: new ArrayBuffer(8) }); h.advance(20000);
  assert.equal(h.drops.length, 0, 'frames arriving keep the socket'); assert.equal(h.catchUps, 1);
  h.busy = false; h.advance(20000); h.advance(15000); assert.equal(h.drops[0], 'no-pong', 'silence still ends it');
}
// (h) nothing happens while hidden; the next visible moment reconnects at once
{
  const h = harness(); h.link.connect(); h.advance(10000); assert.equal(h.drops[0], 'timeout');
  h.visible = false; h.link.pause(); const count = h.sockets.length; h.advance(300000); assert.equal(h.sockets.length, count);
  h.visible = true; h.link.decide('visible'); assert.equal(h.sockets.length, count + 1);
}
// (i) a socket that closes at once is retried on the same host once, then the next host
{
  const h = harness({ meta: { addrs: ['192.168.1.8'], port: 47831 } }); h.link.connect();
  h.last().onclose({ code: 1006 }); h.advance(1000); assert.equal(h.last().url, 'wss://pc.local:47831/sync');
  h.last().onclose({ code: 1006 }); h.advance(2000); assert.equal(h.last().url, 'wss://192.168.1.8:47831/sync');
  assert.deepEqual(h.link.diag().prev, 'close:1006'); assert.equal(h.link.diag().tries, 2);
}
// (j) the pong for an outstanding probe is consumed; other frames still reach the protocol handler
{
  const h = harness(); h.link.connect(); h.welcome(h.last());
  h.last().onmessage({ data: '{"t":"flush"}' }); assert.equal(h.frames.length, 1);
  h.advance(20000); h.pong(h.last()); assert.equal(h.frames.length, 1, 'the pong is not passed on');
  h.last().onmessage({ data: '{"t":"pong","id":999}' }); assert.equal(h.frames.length, 2, 'a stray pong is just a frame');
}
console.log('PASS link state machine: bounded connect, heartbeat, resume probe, expiry, tick, lenient probe while busy, hidden idling, candidate rotation, pong routing');
