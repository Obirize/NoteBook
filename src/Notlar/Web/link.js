// The life of the connection to the PC, as a small state machine with every wait bounded. Nothing in here touches
// the DOM or the network directly: the caller injects clocks, sockets and callbacks, so the same code runs under
// node in the tests with a fake clock and fake sockets.
//
// What it guarantees: a connection attempt never hangs (10 s to open, 25 s to be welcomed); a socket that looks
// open is not trusted after a pause — it is asked and, if silent, replaced; while the app is on screen the PC hears
// from the phone at least every 20 s; the PC is tried by name and, three seconds later, by address, and whichever
// answered last time is tried first; the app coming back to the foreground is noticed even when the browser
// delivers no event (a 5 s tick that jumps by 20 s means the page was suspended).
export const DEFAULTS = {
  CONNECT_TIMEOUT: 10000, HELLO_WAIT: 25000, STAGGER: 3000, LADDER: [1000, 2000, 3000, 6000, 12000, 20000], COLD: 60000, COLD_AFTER: 10,
  PING_EVERY: 20000, PONG_WAIT: 15000, RESUME_WAIT: 4000, RESUME_WAIT_OFFLINE: 10000, RESUME_MARGIN: 15000, STALL_WAIT: 30000,
  TICK: 5000, GAP: 20000, MAX_CANDIDATES: 5, IDLE: 60000,
};
export function createLink(io, options = {}) {
  const C = { ...DEFAULTS, ...options };
  const t = {};                                   // timers: retry, connect, stagger, hello, ping, tick
  let socket = null, second = null, ready = false, probe = null;
  let connectStarted = 0, hiddenAt = null, lastOut = 0, lastIn = 0, lastTick = io.now();
  let pingId = 0, failures = 0, step = 0, idx = 0, sameFails = 0, prev = '', everConnected = false, idle = C.IDLE;
  const meta = { lastGood: null, addrs: [], port: null, ...(io.meta || {}) };

  const clear = k => { if (t[k] != null) { io.clearTimeout(t[k]); t[k] = null; } };
  const clearAll = () => ['retry', 'connect', 'stagger', 'hello', 'ping'].forEach(clear);
  const abandon = ws => { if (!ws) return; ws.onopen = ws.onmessage = ws.onclose = ws.onerror = null; try { ws.close(); } catch { } };
  const candidates = () => [...new Set([meta.lastGood, io.name, ...meta.addrs.map(a => a + ':' + (meta.port || io.port))].filter(Boolean))].slice(0, C.MAX_CANDIDATES);
  const young = ws => ws && ((ws.readyState === 0 && io.now() - connectStarted < C.CONNECT_TIMEOUT) || (ws.readyState === 1 && !ready && io.now() - connectStarted < C.CONNECT_TIMEOUT + C.HELLO_WAIT));

  function open(host) {
    const ws = io.makeSocket('wss://' + host + '/sync'); ws.host = host;
    ws.onopen = () => opened(ws); ws.onmessage = e => message(ws, e.data); ws.onclose = e => closed(ws, e); ws.onerror = () => { };
    return ws;
  }
  function connect() {
    clear('retry');
    if (!io.visible()) return;
    if (socket && (young(socket) || ready)) return;
    if (socket) drop('stale');
    const list = candidates(); const host = list[idx % list.length];
    connectStarted = io.now(); socket = open(host); io.onStatus('connecting', host);
    const ws = socket;
    t.connect = io.setTimeout(() => { if (socket === ws && ws.readyState !== 1) fail('timeout', ws); }, C.CONNECT_TIMEOUT);
    // A name that does not resolve costs the full timeout; the address gets its chance three seconds in.
    if (list.length > 1) t.stagger = io.setTimeout(() => { if (socket === ws && ws.readyState === 0 && !second) second = open(list[(idx + 1) % list.length]); }, C.STAGGER);
  }
  function opened(ws) {
    if (ws === second) { const loser = socket; socket = ws; second = null; abandon(loser); }
    else if (ws === socket) { abandon(second); second = null; }
    else return;
    clear('connect'); clear('stagger');
    t.hello = io.setTimeout(() => { if (socket === ws && !ready) fail('hello', ws); }, C.HELLO_WAIT);
    io.onOpen(ws);
  }
  // The PC verified us: this host is the one to try first next time.
  function welcomed(ws, info) {
    if (ws !== socket) return;
    clear('hello'); ready = true; everConnected = true; failures = 0; step = 0; sameFails = 0; prev = '';
    meta.lastGood = ws.host; idx = 0;
    if (Array.isArray(info.addresses)) meta.addrs = info.addresses.filter(a => typeof a === 'string').slice(0, C.MAX_CANDIDATES);
    if (info.port) meta.port = info.port; if (info.idle > 0) idle = info.idle;
    io.saveMeta({ ...meta });
    lastOut = lastIn = io.now(); armPing(); io.onStatus('ready', ws.host);
  }
  function message(ws, data) {
    if (ws !== socket) return;
    lastIn = io.now();
    if (probe && typeof data === 'string' && data.length < 64 && data.includes('"pong"')) {
      try { if (JSON.parse(data).id === probe.id) { satisfied(); return; } } catch { }
    }
    io.onFrame(ws, data);
  }
  function closed(ws, e) {
    if (ws === second) { second = null; return; }
    if (ws !== socket) return;
    socket = null; fail('close:' + (e && e.code != null ? e.code : '?'), null);
  }
  // One attempt is over without a welcome: note why, pick the next host if this one timed out, and try again later.
  function fail(reason, ws) {
    if (ws) { if (ws === socket) socket = null; abandon(ws); }
    abandon(second); second = null;
    ready = false; prev = reason; failures++; clearAll();
    if (reason === 'timeout' || reason === 'hello' || ++sameFails >= 2) { idx++; sameFails = 0; }
    if (probe) { io.clearTimeout(probe.timer); probe = null; }
    io.onStatus('offline'); io.onDrop(reason);
    retry();
  }
  function retry() {
    clear('retry'); if (!io.visible()) return;
    const delay = failures >= C.COLD_AFTER ? C.COLD : C.LADDER[Math.min(step, C.LADDER.length - 1)]; step++;
    t.retry = io.setTimeout(connect, delay);
  }
  // Give up on the current socket without waiting for it to say anything.
  function drop(reason) {
    const ws = socket; socket = null; ready = false; prev = reason; clearAll();
    if (probe) { io.clearTimeout(probe.timer); probe = null; }
    abandon(second); second = null; abandon(ws);
    io.onStatus('offline'); io.onDrop(reason);
  }
  function armPing() {
    clear('ping'); if (!ready || !io.visible() || !C.PING_EVERY) return;
    t.ping = io.setTimeout(() => { if (ready && !probe) ping({ strict: false, wait: C.PONG_WAIT }); }, Math.max(1000, C.PING_EVERY - (io.now() - lastOut)));
  }
  // "Are you there?" A strict probe needs the matching pong; a lenient one is content with any frame that arrives
  // afterwards (a download in progress is proof enough that the PC is alive).
  function ping({ strict, wait, cb }) {
    if (!ready || probe) return;
    const id = ++pingId, at = io.now();
    probe = { id, cb, timer: io.setTimeout(() => {
      const p = probe; probe = null;
      if (!strict && lastIn > at) { p.cb?.(); armPing(); return; }
      drop('no-pong'); step = 0; connect();
    }, wait) };
    send(JSON.stringify({ t: 'ping', id }));
  }
  function satisfied() { const p = probe; if (!p) return; io.clearTimeout(p.timer); probe = null; p.cb?.(); armPing(); }
  function send(text) { if (!socket || !ready) return false; socket.send(text); lastOut = io.now(); armPing(); return true; }
  function sendBinary(bytes) { if (!socket || !ready) return false; socket.send(bytes); lastOut = io.now(); return true; }
  // The screen went dark: keep a healthy socket, stop the timers, remember when.
  function pause() {
    hiddenAt = io.now(); clear('ping'); clear('retry');
    if (probe) { io.clearTimeout(probe.timer); probe = null; }
    io.onHidden();
  }
  // Back on screen, or the tick noticed a suspension: decide what the socket is worth.
  function decide(source) {
    if (!io.visible()) return;
    const away = hiddenAt != null ? io.now() - hiddenAt : 0; hiddenAt = null;
    if (socket && young(socket)) return;
    if (socket && ready) {
      if (away > idle - C.RESUME_MARGIN) { drop('expired'); step = 0; connect(); return; }
      if (probe) { if (source === 'manual' && !io.busy()) { drop('manual'); step = 0; connect(); } return; }
      const busy = io.busy();
      ping({ strict: !busy, wait: busy ? C.STALL_WAIT : io.online() ? C.RESUME_WAIT : C.RESUME_WAIT_OFFLINE, cb: io.catchUp });
      return;
    }
    step = 0; connect();
  }
  t.tick = io.setInterval(() => { const now = io.now(); if (now - lastTick > C.GAP) { hiddenAt ??= lastTick; decide('resume'); } lastTick = now; }, C.TICK);

  return {
    connect, decide, pause, drop, send, sendBinary, welcomed,
    retry: () => { failures++; retry(); },
    forget: () => { meta.lastGood = null; idx = 0; },
    owns: ws => ws === socket, isReady: () => ready, buffered: () => socket ? socket.bufferedAmount : 0,
    diag: () => ({ prev, hidden: hiddenAt != null ? io.now() - hiddenAt : null, tries: failures, boot: !everConnected }),
    state: () => ({ ready, socket: !!socket, second: !!second, probe: !!probe, failures, step, idx, prev, candidates: candidates() }),
    stop: () => { clearAll(); io.clearInterval(t.tick); abandon(second); abandon(socket); socket = second = null; ready = false; },
  };
}
