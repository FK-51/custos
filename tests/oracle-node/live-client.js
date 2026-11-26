'use strict';

// 公式の Socket.IO クライアント（対象アプリと同じ 4.8.3）。テスト専用。
// 決まった一覧のイベント（バイナリと確認応答を含む）を送り、送ったもの・受け取ったものを JSON Lines で標準出力に書いて終わる。
// C# 側のプロキシの記録（指示書 タスク5、適合テスト C-4）と照らし合わせるのに使う。
//
// 使い方：
//   node live-client.js --url http://edge:13000 --transport polling|websocket|upgrade
//                       [--path /socket.io/] [--namespace /] [--timeout 10000] [--header "User-Agent=..."]
//
// トランスポート：
//   polling   ポーリングだけ（切り替えない）
//   websocket WebSocket だけ
//   upgrade   ポーリングで始めて WebSocket に切り替える（Socket.IO の既定）。切り替えの前に1件、後に残りを送る

const { io } = require('socket.io-client');
const { toJsonValue } = require('./lib/binary');

function parseArgs(argv) {
  const args = { url: null, transport: 'upgrade', path: '/socket.io/', namespace: '/', timeout: 10000, headers: {} };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--url') args.url = argv[++i];
    else if (a === '--transport') args.transport = argv[++i];
    else if (a === '--path') args.path = argv[++i];
    else if (a === '--namespace') args.namespace = argv[++i];
    else if (a === '--timeout') args.timeout = Number(argv[++i]);
    else if (a === '--header') {
      const text = argv[++i];
      const eq = text.indexOf('=');
      if (eq <= 0) throw new Error(`--header は 名前=値 の形で書く: ${text}`);
      args.headers[text.slice(0, eq)] = text.slice(eq + 1);
    } else throw new Error(`unknown argument: ${a}`);
  }
  if (!args.url) throw new Error('--url が必要です');
  if (!['polling', 'websocket', 'upgrade'].includes(args.transport)) throw new Error(`unknown transport: ${args.transport}`);
  return args;
}

function log(obj) {
  process.stdout.write(JSON.stringify({ ts: new Date().toISOString(), ...obj }) + '\n');
}

function withTimeout(promise, ms, what) {
  let timer;
  return Promise.race([
    promise,
    new Promise((_, reject) => {
      timer = setTimeout(() => reject(new Error(`timeout: ${what}`)), ms);
    }),
  ]).finally(() => clearTimeout(timer));
}

function waitEvent(socket, event, ms) {
  return withTimeout(new Promise((resolve) => socket.once(event, (...args) => resolve(args))), ms, `event ${event}`);
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const options = {
    path: args.path,
    reconnection: false,
    timeout: args.timeout,
    extraHeaders: args.headers,
  };
  if (args.transport === 'polling') {
    options.transports = ['polling'];
    options.upgrade = false;
  } else if (args.transport === 'websocket') {
    options.transports = ['websocket'];
  } else {
    options.transports = ['polling', 'websocket'];
    options.upgrade = true;
  }

  const target = args.namespace === '/' ? args.url : args.url.replace(/\/$/, '') + args.namespace;
  const socket = io(target, options);
  const transportName = () => socket.io.engine && socket.io.engine.transport ? socket.io.engine.transport.name : null;

  // 切り替えは接続の直後に一瞬で終わることがあるので、エンジンが開いた時点で待ち受けを付ける
  let markUpgraded;
  const upgraded = new Promise((resolve) => (markUpgraded = resolve));
  socket.io.on('open', () => {
    const engine = socket.io.engine;
    log({ kind: 'engine_open', engine_sid: engine.id, transport: engine.transport.name });
    if (engine.transport.name === 'websocket') markUpgraded();
    engine.on('upgrade', (transport) => {
      log({ kind: 'upgraded', transport: transport.name });
      markUpgraded();
    });
  });

  socket.onAny((event, ...received) => {
    log({ kind: 'received', event, args: toJsonValue(received), transport: transportName() });
  });

  await withTimeout(
    new Promise((resolve, reject) => {
      socket.once('connect', resolve);
      socket.once('connect_error', reject);
    }),
    args.timeout,
    'connect',
  );
  log({ kind: 'connected', sid: socket.id, engine_sid: socket.io.engine.id, transport: transportName(), requested: args.transport });

  const send = (event, ...payload) => {
    socket.emit(event, ...payload);
    log({ kind: 'sent', event, args: toJsonValue(payload), ack: false, transport: transportName() });
  };
  const sendWithAck = async (event, ...payload) => {
    log({ kind: 'sent', event, args: toJsonValue(payload), ack: true, transport: transportName() });
    const reply = await withTimeout(socket.timeout(args.timeout).emitWithAck(event, ...payload), args.timeout + 1000, `ack ${event}`);
    log({ kind: 'ack', event, args: toJsonValue([reply]), transport: transportName() });
  };

  if (args.transport === 'upgrade') {
    // 接続の直後に1件送る（切り替えの前ならポーリングで届く）。どちらで運ばれたかは transport に書く。
    // サーバーは接続した時点で "welcome" を送るので、ポーリングでのイベントは必ず1件以上ある。
    const echoed = waitEvent(socket, 'echo', args.timeout);
    send('echo', 'before-upgrade');
    await echoed;
    await withTimeout(upgraded, args.timeout, 'upgrade');
    log({ kind: 'after_upgrade', transport: transportName() });
  }

  {
    const echoed = waitEvent(socket, 'echo', args.timeout);
    send('echo', 'hello', 42, { nested: { a: [1, 2, 3] }, jp: '日本語' });
    await echoed;
  }

  await sendWithAck('ack-test', { q: 1 });
  send('binary-test', Buffer.from([1, 2, 3, 4]), { inner: Buffer.from('abc') });
  await sendWithAck('binary-ack', Buffer.from([9, 8, 7]));

  {
    const binary = waitEvent(socket, 'binary', args.timeout);
    send('request-binary');
    await binary;
  }

  send('array', [1, 'two', null, true]);

  const closed = withTimeout(new Promise((resolve) => socket.once('disconnect', resolve)), args.timeout, 'disconnect');
  send('bye');
  const reason = await closed;
  log({ kind: 'done', reason, transport: args.transport });
}

main().then(
  () => process.exit(0),
  (e) => {
    log({ kind: 'error', message: e.message });
    process.exit(1);
  },
);
