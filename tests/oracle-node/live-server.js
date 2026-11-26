'use strict';

// 公式の Socket.IO サーバー（対象アプリと同じ 4.8.1）。テスト専用。
// 受けたイベントを JSON Lines で標準出力に書く。C# 側のプロキシの記録と照らし合わせるのに使う。
//
// 使い方：node live-server.js [--port 3000] [--host 0.0.0.0] [--max-http-buffer-size 32768]
//
// 振る舞い：
//   - "echo"：受け取った値をそのまま "echo" で送り返す
//   - "request-binary"：バイナリを含む "binary" を送る
//   - 確認応答付きのイベント：{ ok: true, event, count } を返す。受けた値にバイナリがあれば、バイナリも付けて返す（BINARY_ACK）
//   - "bye"：サーバー側から切断する
//   - 接続した時点で "welcome" を送る
//   - 名前空間 "/" と "/admin" の両方で同じように振る舞う

const http = require('http');
const { Server } = require('socket.io');
const { toJsonValue, isBinary } = require('./lib/binary');

function parseArgs(argv) {
  const args = { port: 3000, host: '0.0.0.0', maxHttpBufferSize: 32 * 1024 };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--port') args.port = Number(argv[++i]);
    else if (a === '--host') args.host = argv[++i];
    else if (a === '--max-http-buffer-size') args.maxHttpBufferSize = Number(argv[++i]);
    else throw new Error(`unknown argument: ${a}`);
  }
  return args;
}

function log(obj) {
  process.stdout.write(JSON.stringify({ ts: new Date().toISOString(), ...obj }) + '\n');
}

function containsBinary(value) {
  if (isBinary(value)) return true;
  if (Array.isArray(value)) return value.some(containsBinary);
  if (value !== null && typeof value === 'object') return Object.values(value).some(containsBinary);
  return false;
}

function setup(nsp) {
  nsp.on('connection', (socket) => {
    log({
      kind: 'connection',
      nsp: nsp.name,
      sid: socket.id,
      engine_sid: socket.conn.id,
      transport: socket.conn.transport.name,
      headers: socket.handshake.headers,
      query: socket.handshake.query,
    });

    // 接続した時点で1件送る（upgrade の場合、切り替えの前なのでポーリングで届く）
    socket.emit('welcome', { sid: socket.id, transport: socket.conn.transport.name });
    log({ kind: 'sent', nsp: nsp.name, sid: socket.id, event: 'welcome', transport: socket.conn.transport.name });

    socket.conn.on('upgrade', (transport) => {
      log({ kind: 'upgrade', nsp: nsp.name, sid: socket.id, transport: transport.name });
    });

    socket.onAny((event, ...args) => {
      const ack = typeof args[args.length - 1] === 'function' ? args.pop() : null;
      log({
        kind: 'received',
        nsp: nsp.name,
        sid: socket.id,
        transport: socket.conn.transport.name,
        event,
        args: toJsonValue(args),
        ack: ack !== null,
      });

      if (event === 'echo') {
        socket.emit('echo', ...args);
        log({ kind: 'sent', nsp: nsp.name, sid: socket.id, event: 'echo', args: toJsonValue(args) });
      }

      if (event === 'request-binary') {
        const payload = [Buffer.from([0xde, 0xad, 0xbe, 0xef]), { nested: Buffer.from('xyz') }];
        socket.emit('binary', ...payload);
        log({ kind: 'sent', nsp: nsp.name, sid: socket.id, event: 'binary', args: toJsonValue(payload) });
      }

      if (ack) {
        const reply = [{ ok: true, event, count: args.length }];
        if (containsBinary(args)) reply.push(Buffer.from([1, 2]));
        ack(...reply);
        log({ kind: 'acked', nsp: nsp.name, sid: socket.id, event, args: toJsonValue(reply) });
      }

      if (event === 'bye') {
        socket.disconnect(true);
      }
    });

    socket.on('disconnect', (reason) => {
      log({ kind: 'disconnect', nsp: nsp.name, sid: socket.id, reason });
    });
  });
}

function main() {
  const args = parseArgs(process.argv.slice(2));
  const server = http.createServer((req, res) => {
    res.writeHead(404, { 'Content-Type': 'text/plain' });
    res.end('not found');
  });
  const io = new Server(server, { maxHttpBufferSize: args.maxHttpBufferSize, cors: { origin: true } });
  setup(io.of('/'));
  setup(io.of('/admin'));
  server.listen(args.port, args.host, () => log({ kind: 'listening', host: args.host, port: args.port }));

  const shutdown = () => {
    io.close();
    server.close(() => process.exit(0));
    setTimeout(() => process.exit(0), 1000).unref();
  };
  process.on('SIGTERM', shutdown);
  process.on('SIGINT', shutdown);
}

main();
