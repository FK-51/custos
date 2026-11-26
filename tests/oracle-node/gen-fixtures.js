'use strict';

// Socket.IO / Engine.IO の読み解きの正解を、公式の実装（対象アプリと同じ版）で作る。
// 使い方：node gen-fixtures.js > socketio-fixtures.json
// C# 側（Custos.SocketIO.Wire）の読み解きの結果が、ここで作った正解と一致することをテストで確かめる。

const fs = require('fs');
const path = require('path');
const { Encoder, Decoder, PacketType } = require('socket.io-parser');
const eio = require('engine.io-parser');
const { toJsonValue, isBinary, toBuffer } = require('./lib/binary');

const SIO_TYPE_NAMES = {
  [PacketType.CONNECT]: 'connect',
  [PacketType.DISCONNECT]: 'disconnect',
  [PacketType.EVENT]: 'event',
  [PacketType.ACK]: 'ack',
  [PacketType.CONNECT_ERROR]: 'connect_error',
  [PacketType.BINARY_EVENT]: 'binary_event',
  [PacketType.BINARY_ACK]: 'binary_ack',
};

function version(pkg) {
  // exports で package.json を公開していない版があるので、ファイルを直接読む
  const file = path.join(__dirname, 'node_modules', pkg, 'package.json');
  return JSON.parse(fs.readFileSync(file, 'utf8')).version;
}

// ---------------------------------------------------------------------------
// Socket.IO のパケット
// ---------------------------------------------------------------------------

const B = (...bytes) => Buffer.from(bytes);

/** 正しいパケット。packet は socket.io-parser の Encoder に渡す形。 */
const socketioCases = [
  { name: 'event-string', packet: { type: PacketType.EVENT, nsp: '/', data: ['hello', 'world'] } },
  { name: 'event-name-only', packet: { type: PacketType.EVENT, nsp: '/', data: ['ping-like'] } },
  { name: 'event-number', packet: { type: PacketType.EVENT, nsp: '/', data: ['num', 42, -1.5, 0] } },
  { name: 'event-large-numbers', packet: { type: PacketType.EVENT, nsp: '/', data: ['n', 9007199254740991, 1e-7, 1.7976931348623157e308, -0] } },
  { name: 'event-boolean-null', packet: { type: PacketType.EVENT, nsp: '/', data: ['flags', true, false, null] } },
  { name: 'event-nested-object', packet: { type: PacketType.EVENT, nsp: '/', data: ['obj', { a: { b: [1, 2, { c: 'd' }] }, e: null }] } },
  { name: 'event-array', packet: { type: PacketType.EVENT, nsp: '/', data: ['arr', [1, 'two', [3, [4]]]] } },
  { name: 'event-japanese', packet: { type: PacketType.EVENT, nsp: '/', data: ['メッセージ', { 名前: 'テスト🎴', 説明: '全角　スペース' }] } },
  { name: 'event-empty-containers', packet: { type: PacketType.EVENT, nsp: '/', data: ['empty', [], {}, ''] } },
  { name: 'event-escapes', packet: { type: PacketType.EVENT, nsp: '/', data: ['s', 'quote" backslash\\ newline\n tab\t ctrl\u0001 rs\u001e end'] } },
  { name: 'event-proto-keys', packet: { type: PacketType.EVENT, nsp: '/', data: ['p', JSON.parse('{"__proto__":{"polluted":true},"constructor":{"prototype":{}}}')] } },
  { name: 'event-halpal-player-choice', packet: { type: PacketType.EVENT, nsp: '/', data: ['playerChoice', { roomId: 'room-AbCdEf123', choice: 3 }] } },
  { name: 'event-with-ack-id', packet: { type: PacketType.EVENT, nsp: '/', id: 12, data: ['ask', { q: 1 }] } },
  { name: 'event-with-ack-id-0', packet: { type: PacketType.EVENT, nsp: '/', id: 0, data: ['registPlayer', { name: 'alice' }] } },
  { name: 'event-with-large-ack-id', packet: { type: PacketType.EVENT, nsp: '/', id: 123456789, data: ['x'] } },
  { name: 'ack', packet: { type: PacketType.ACK, nsp: '/', id: 12, data: [{ ok: true }] } },
  { name: 'ack-empty', packet: { type: PacketType.ACK, nsp: '/', id: 5, data: [] } },
  { name: 'ack-multiple-values', packet: { type: PacketType.ACK, nsp: '/', id: 2, data: ['a', 1, null, { b: [] }] } },
  { name: 'namespace-event', packet: { type: PacketType.EVENT, nsp: '/admin', data: ['x', 1] } },
  { name: 'namespace-event-with-ack', packet: { type: PacketType.EVENT, nsp: '/admin', id: 3, data: ['y'] } },
  { name: 'namespace-ack', packet: { type: PacketType.ACK, nsp: '/admin', id: 3, data: ['done'] } },
  { name: 'connect-default', packet: { type: PacketType.CONNECT, nsp: '/' } },
  { name: 'connect-with-auth', packet: { type: PacketType.CONNECT, nsp: '/', data: { token: 't-123' } } },
  { name: 'connect-namespace', packet: { type: PacketType.CONNECT, nsp: '/admin' } },
  { name: 'connect-namespace-with-auth', packet: { type: PacketType.CONNECT, nsp: '/admin', data: { token: 'x' } } },
  { name: 'connect-server-response', packet: { type: PacketType.CONNECT, nsp: '/', data: { sid: 'Xy1-AbC_dEf23' } } },
  { name: 'disconnect-default', packet: { type: PacketType.DISCONNECT, nsp: '/' } },
  { name: 'disconnect-namespace', packet: { type: PacketType.DISCONNECT, nsp: '/admin' } },
  { name: 'connect-error', packet: { type: PacketType.CONNECT_ERROR, nsp: '/', data: { message: 'Not authorized' } } },
  { name: 'connect-error-namespace', packet: { type: PacketType.CONNECT_ERROR, nsp: '/admin', data: { message: 'Invalid namespace', data: { code: 1 } } } },
  { name: 'binary-event', packet: { type: PacketType.EVENT, nsp: '/', data: ['file', B(1, 2, 3)] } },
  { name: 'binary-event-empty-buffer', packet: { type: PacketType.EVENT, nsp: '/', data: ['zero', Buffer.alloc(0)] } },
  { name: 'binary-event-nested', packet: { type: PacketType.EVENT, nsp: '/', data: ['files', { meta: { name: 'a.bin' }, chunks: [B(0, 255), { inner: B(10, 20, 30) }] }] } },
  { name: 'binary-event-multiple', packet: { type: PacketType.EVENT, nsp: '/', data: ['multi', B(1), B(2, 2), B(3, 3, 3)] } },
  { name: 'binary-event-with-ack-id', packet: { type: PacketType.EVENT, nsp: '/', id: 7, data: ['upload', { bytes: B(9, 8, 7) }] } },
  { name: 'binary-event-namespace', packet: { type: PacketType.EVENT, nsp: '/admin', data: ['bin', B(4, 5)] } },
  { name: 'binary-event-namespace-ack-id', packet: { type: PacketType.EVENT, nsp: '/admin', id: 9, data: ['bin', B(6)] } },
  { name: 'binary-ack', packet: { type: PacketType.ACK, nsp: '/', id: 7, data: [B(1, 1, 2, 3, 5, 8)] } },
  { name: 'binary-ack-namespace', packet: { type: PacketType.ACK, nsp: '/admin', id: 4, data: [{ b: B(13) }] } },
];

/**
 * 境界の文字列。公式の Decoder の振る舞い（誤りにするか、どう読むか）をそのまま記録する。
 * C# 側も同じ振る舞いになることを確かめる（ファジングの判定で、公式と解釈がずれないようにするため）。
 */
const edgeSocketioTexts = [
  { name: 'invalid-unknown-type', text: '9' },
  { name: 'invalid-event-without-payload', text: '2' },
  { name: 'invalid-event-bad-json', text: '2[' },
  { name: 'invalid-event-object-payload', text: '2{"a":1}' },
  { name: 'invalid-event-number-payload', text: '242' },
  { name: 'invalid-event-empty-array', text: '2[]' },
  { name: 'invalid-event-name-not-string', text: '2[1,2]' },
  { name: 'invalid-connect-array-payload', text: '0[]' },
  { name: 'invalid-ack-object-payload', text: '3{"a":1}' },
  { name: 'invalid-connect-error-string-payload', text: '4"x"' },
  { name: 'invalid-binary-attachments-not-number', text: '5x-["a"]' },
  { name: 'invalid-reserved-event-name', text: '2["connect"]' },
  { name: 'edge-event-numeric-name', text: '2[1,"x"]' },
  { name: 'edge-ack-id-without-data', text: '312' },
  { name: 'edge-namespace-without-comma', text: '2/admin' },
  { name: 'edge-whitespace-payload', text: '2 ["a"]' },
  { name: 'edge-binary-header-waiting', text: '51-["file",{"_placeholder":true,"num":0}]' },
  { name: 'edge-connect-error-string', text: '4"oops"' },
  { name: 'edge-disconnect-with-payload', text: '1{}' },
];

function encodeSocketIo(packet) {
  const encoder = new Encoder();
  return encoder.encode(packet).map((part) =>
    typeof part === 'string' ? { text: part } : { binary_b64: toBuffer(part).toString('base64') },
  );
}

function describeDecoded(p, attachments) {
  return {
    type: SIO_TYPE_NAMES[p.type],
    nsp: p.nsp,
    id: p.id === undefined ? null : p.id,
    data: p.data === undefined ? null : toJsonValue(p.data),
    attachments,
  };
}

function decodeSocketIo(encodedParts) {
  const decoder = new Decoder();
  const decoded = [];
  decoder.on('decoded', (packet) => decoded.push(packet));
  for (const part of encodedParts) {
    decoder.add(part.text !== undefined ? part.text : Buffer.from(part.binary_b64, 'base64'));
  }
  decoder.destroy();
  if (decoded.length !== 1) throw new Error(`expected 1 decoded packet, got ${decoded.length}`);
  return describeDecoded(decoded[0], encodedParts.filter((part) => part.binary_b64 !== undefined).length);
}

function decodeEdge(text) {
  const decoder = new Decoder();
  let decoded = null;
  decoder.on('decoded', (packet) => (decoded = packet));
  try {
    decoder.add(text);
  } catch (e) {
    return { error: true, message: e.message, decoded: null, waiting_for_attachments: false };
  }
  // 添付を待っている（BINARY_* の頭だけ受け取った）場合は、まだ decoded が来ない
  const waiting = decoded === null;
  decoder.destroy();
  return { error: false, message: null, decoded: decoded ? describeDecoded(decoded, 0) : null, waiting_for_attachments: waiting };
}

// ---------------------------------------------------------------------------
// Engine.IO のパケットとペイロード（EIO=4）
// ---------------------------------------------------------------------------

const openData = JSON.stringify({
  sid: 'lv_VI97HAXpY6yYWAAAC',
  upgrades: ['websocket'],
  pingInterval: 25000,
  pingTimeout: 20000,
  maxPayload: 1000000,
});

const engineioCases = [
  { name: 'open', packet: { type: 'open', data: openData } },
  { name: 'close', packet: { type: 'close' } },
  { name: 'ping', packet: { type: 'ping' } },
  { name: 'pong', packet: { type: 'pong' } },
  { name: 'ping-probe', packet: { type: 'ping', data: 'probe' } },
  { name: 'pong-probe', packet: { type: 'pong', data: 'probe' } },
  { name: 'message-text', packet: { type: 'message', data: 'hello' } },
  { name: 'message-socketio-event', packet: { type: 'message', data: '2["ev",1]' } },
  { name: 'message-japanese', packet: { type: 'message', data: '2["メッセージ","テスト🎴"]' } },
  { name: 'message-empty', packet: { type: 'message', data: '' } },
  { name: 'upgrade', packet: { type: 'upgrade' } },
  { name: 'noop', packet: { type: 'noop' } },
  { name: 'message-binary-websocket', packet: { type: 'message', data: B(1, 2, 3, 4) }, supportsBinary: true },
  { name: 'message-binary-polling', packet: { type: 'message', data: B(1, 2, 3, 4) }, supportsBinary: false },
  { name: 'message-binary-empty-polling', packet: { type: 'message', data: Buffer.alloc(0) }, supportsBinary: false },
];

function encodeEngineIo(packet, supportsBinary) {
  let result;
  eio.encodePacket(packet, supportsBinary, (encoded) => (result = encoded));
  return result;
}

function describeEngineIo(packet) {
  const binary = isBinary(packet.data);
  return {
    type: packet.type,
    data: packet.data === undefined || packet.data === null ? null : binary ? { _binary_b64: toBuffer(packet.data).toString('base64') } : packet.data,
    binary,
  };
}

const pollingCases = [
  { name: 'single-message', packets: [{ type: 'message', data: 'hello' }] },
  { name: 'message-and-ping', packets: [{ type: 'message', data: 'hello' }, { type: 'ping' }] },
  { name: 'open-only', packets: [{ type: 'open', data: openData }] },
  { name: 'open-and-connect', packets: [{ type: 'open', data: openData }, { type: 'message', data: '0{"sid":"abc"}' }] },
  { name: 'binary-and-text', packets: [{ type: 'message', data: B(7, 7, 7) }, { type: 'message', data: 'text' }] },
  {
    name: 'socketio-binary-event-in-polling',
    packets: [
      { type: 'message', data: '51-["file",{"_placeholder":true,"num":0}]' },
      { type: 'message', data: B(1, 2, 3) },
    ],
  },
  { name: 'noop', packets: [{ type: 'noop' }] },
  { name: 'close', packets: [{ type: 'close' }] },
  {
    name: 'many-events',
    packets: [
      { type: 'message', data: '2["a",1]' },
      { type: 'message', data: '2["b",{"x":"y"}]' },
      { type: 'message', data: '30[{"ok":true}]' },
      { type: 'message', data: '2["日本語","テスト"]' },
    ],
  },
];

function encodePayload(packets) {
  let result;
  eio.encodePayload(packets, (encoded) => (result = encoded));
  return result;
}

// ---------------------------------------------------------------------------

function main() {
  const out = {
    generator: {
      node: process.version,
      'socket.io-parser': version('socket.io-parser'),
      'engine.io-parser': version('engine.io-parser'),
      'engine.io-protocol': eio.protocol,
    },
    socketio_packets: [],
    socketio_edge_cases: [],
    engineio_packets: [],
    polling_payloads: [],
  };

  for (const c of socketioCases) {
    const encoded = encodeSocketIo(c.packet);
    out.socketio_packets.push({ name: c.name, encoded, decoded: decodeSocketIo(encoded) });
  }

  for (const c of edgeSocketioTexts) {
    out.socketio_edge_cases.push({ name: c.name, text: c.text, ...decodeEdge(c.text) });
  }

  for (const c of engineioCases) {
    const supportsBinary = c.supportsBinary !== undefined ? c.supportsBinary : false;
    const encoded = encodeEngineIo(c.packet, supportsBinary);
    const textual = typeof encoded === 'string';
    const decodedPacket = eio.decodePacket(encoded, 'nodebuffer');
    out.engineio_packets.push({
      name: c.name,
      supports_binary: supportsBinary,
      encoded_text: textual ? encoded : null,
      encoded_binary_b64: textual ? null : toBuffer(encoded).toString('base64'),
      decoded: describeEngineIo(decodedPacket),
    });
  }

  for (const c of pollingCases) {
    const payload = encodePayload(c.packets);
    const decoded = eio.decodePayload(payload, 'nodebuffer').map(describeEngineIo);
    out.polling_payloads.push({ name: c.name, payload, decoded });
  }

  process.stdout.write(JSON.stringify(out, null, 2) + '\n');
}

main();
