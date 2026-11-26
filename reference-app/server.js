'use strict';

// Custos の参照アプリ（設計書 第12.5節）。2人で、1〜5の札を3枚ずつ持ち、交互に1枚ずつ出して、大きい方が勝負に勝つ。3回の勝負で終わる。
// 既知のバグを埋め込んである（一覧は known-bugs.json にだけ書く）。フレームワーク自体の回帰テストに使う。本番では使わない。
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { DatabaseSync } = require('node:sqlite');
const { Server } = require('socket.io');

const PORT = Number(process.env.PORT || 3000);
const DB_PATH = process.env.DB_PATH || '/app/data/ref.db';
fs.mkdirSync(path.dirname(DB_PATH), { recursive: true });
const db = new DatabaseSync(DB_PATH);
db.exec(`
  PRAGMA journal_mode = WAL;
  CREATE TABLE IF NOT EXISTS Player (id TEXT PRIMARY KEY, name TEXT NOT NULL, socketId TEXT, reconnectToken TEXT);
  CREATE TABLE IF NOT EXISTS Room (id TEXT PRIMARY KEY, name TEXT NOT NULL, ownerId TEXT NOT NULL, playing INTEGER NOT NULL DEFAULT 0);
  CREATE TABLE IF NOT EXISTS RoomPlayer (roomId TEXT NOT NULL, playerId TEXT NOT NULL, PRIMARY KEY (roomId, playerId));
  CREATE TABLE IF NOT EXISTS GameSession (id TEXT PRIMARY KEY, roomId TEXT NOT NULL, endedAt INTEGER, winner TEXT);
  CREATE TABLE IF NOT EXISTS GameLogAction (id INTEGER PRIMARY KEY AUTOINCREMENT, gameSessionId TEXT NOT NULL, sequenceNumber INTEGER NOT NULL,
    actionType TEXT NOT NULL, playerIndex INTEGER, changesJson TEXT NOT NULL, stateSnapshotJson TEXT);
`);

let tokenCounter = 1000;
const games = new Map(); // roomId -> game

const html = fs.readFileSync(path.join(__dirname, 'public', 'index.html'));
const server = http.createServer((req, res) => {
  if (req.url === '/' || req.url === '/index.html') {
    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
    res.end(html);
    return;
  }
  res.writeHead(404, { 'Content-Type': 'text/plain' });
  res.end('not found');
});
const io = new Server(server, { maxHttpBufferSize: 32 * 1024 });

function view(game, index) {
  const other = 1 - index;
  return {
    myTurnNumber: index + 1,
    myHands: game.hands[index].slice(),
    myPlayed: game.played[index].slice(),
    otherPlayed: game.played[other].slice(),
    cardNumber: game.deck.length,
    wins: game.wins.slice(),
    trick: game.trick,
    opponentHand: game.hands[other].slice(),
  };
}

function snapshot(game) {
  return JSON.stringify({ deck: game.deck, hands: game.hands, played: game.played, trick: game.trick, current: game.current, wins: game.wins });
}

function log(game, type, playerIndex, changes) {
  db.prepare('INSERT INTO GameLogAction (gameSessionId, sequenceNumber, actionType, playerIndex, changesJson, stateSnapshotJson) VALUES (?, ?, ?, ?, ?, ?)')
    .run(game.id, game.seq, type, playerIndex, JSON.stringify(changes), snapshot(game));
  game.seq += 1;
}

function sendTurn(game) {
  const current = game.current;
  const sockets = game.players.map((p) => io.sockets.sockets.get(p.socketId));
  if (sockets[current]) sockets[current].emit('yourTurn', { now: view(game, current), choices: game.hands[current].slice(), kind: 'play_card', deadline: Date.now() + 60000 });
  const other = 1 - current;
  if (sockets[other]) sockets[other].emit('anotherTurn', { now: view(game, other) });
}

function startGame(roomId) {
  const members = db.prepare('SELECT p.id, p.socketId FROM RoomPlayer rp JOIN Player p ON p.id = rp.playerId WHERE rp.roomId = ? ORDER BY p.rowid').all(roomId);
  if (members.length !== 2) return;
  const deck = [1, 1, 2, 2, 3, 3, 4, 4, 5, 5];
  for (let i = deck.length - 1; i > 0; i--) {
    const j = crypto.randomInt(i + 1);
    [deck[i], deck[j]] = [deck[j], deck[i]];
  }
  const game = { id: crypto.randomUUID(), roomId, players: members, deck, hands: [deck.splice(0, 3), deck.splice(0, 3)], played: [[], []], trick: 1, current: 0, wins: [0, 0], seq: 0, plays: 0 };
  games.set(roomId, game);
  db.prepare('UPDATE Room SET playing = 1 WHERE id = ?').run(roomId);
  db.prepare('INSERT INTO GameSession (id, roomId) VALUES (?, ?)').run(game.id, roomId);
  log(game, 'initial_draw', null, [{ type: 'DEAL', hands: game.hands }]);
  sendTurn(game);
}

function finishTrick(game) {
  const a = game.played[0][game.played[0].length - 1];
  const b = game.played[1][game.played[1].length - 1];
  if (a > b) game.wins[0] += 1;
  else if (b > a) game.wins[1] += 1;
  if (game.trick === 3) {
    game.seq += 1;
  }
  log(game, 'trick_end', null, [{ type: 'TRICK', trick: game.trick, cards: [a, b] }]);
  game.trick += 1;
  if (game.trick > 3) {
    const winner = game.wins[0] === game.wins[1] ? 'draw' : game.players[game.wins[0] > game.wins[1] ? 0 : 1].id;
    log(game, 'game_end', null, [{ type: 'END', winner }]);
    db.prepare('UPDATE GameSession SET endedAt = ?, winner = ? WHERE id = ?').run(Date.now(), winner, game.id);
    db.prepare('UPDATE Room SET playing = 0 WHERE id = ?').run(game.roomId);
    games.delete(game.roomId);
    for (const p of game.players) {
      const s = io.sockets.sockets.get(p.socketId);
      if (s) s.emit('redirectToResult', { url: `/result?room=${game.roomId}` });
    }
    return;
  }
  sendTurn(game);
}

io.on('connection', (socket) => {
  socket.on('registPlayer', (data, ack) => {
    const name = typeof data?.name === 'string' ? data.name.slice(0, 64) : '';
    if (!name) return typeof ack === 'function' && ack({ success: false });
    const token = String(tokenCounter++).padStart(32, '0');
    db.prepare('INSERT OR REPLACE INTO Player (id, name, socketId, reconnectToken) VALUES (?, ?, ?, ?)').run(socket.id, name, socket.id, token);
    if (typeof ack === 'function') ack({ playerId: socket.id, reconnectToken: token });
  });

  socket.on('createRoom', (data, ack) => {
    const player = db.prepare('SELECT id FROM Player WHERE id = ?').get(socket.id);
    if (!player) return typeof ack === 'function' && ack({ success: false });
    const roomId = 'room-' + crypto.randomBytes(6).toString('hex');
    const name = typeof data?.roomName === 'string' && data.roomName ? data.roomName.slice(0, 64) : roomId;
    db.prepare('INSERT INTO Room (id, name, ownerId) VALUES (?, ?, ?)').run(roomId, name, socket.id);
    if (typeof ack === 'function') ack({ roomId, roomName: name });
  });

  socket.on('joinRoom', (roomId, ack) => {
    const room = typeof roomId === 'string' ? db.prepare('SELECT id, playing FROM Room WHERE id = ?').get(roomId) : undefined;
    const player = db.prepare('SELECT id FROM Player WHERE id = ?').get(socket.id);
    const count = room ? db.prepare('SELECT COUNT(*) AS n FROM RoomPlayer WHERE roomId = ?').get(roomId).n : 0;
    if (!room || !player || room.playing || count >= 2) return typeof ack === 'function' && ack({ success: false });
    db.prepare('INSERT OR IGNORE INTO RoomPlayer (roomId, playerId) VALUES (?, ?)').run(roomId, socket.id);
    socket.join(roomId);
    if (typeof ack === 'function') ack({ success: true, roomId });
  });

  socket.on('showRooms', (_data, ack) => {
    const rooms = db.prepare('SELECT r.id, r.name, COUNT(rp.playerId) AS players FROM Room r LEFT JOIN RoomPlayer rp ON rp.roomId = r.id GROUP BY r.id').all();
    if (typeof ack === 'function') ack({ rooms });
  });

  socket.on('ready', (data) => {
    const roomId = typeof data?.roomId === 'string' ? data.roomId : '';
    const member = db.prepare('SELECT 1 FROM RoomPlayer WHERE roomId = ? AND playerId = ?').get(roomId, socket.id);
    if (!member || games.has(roomId)) return;
    socket.data.ready = roomId;
    const readyCount = [...io.sockets.sockets.values()].filter((s) => s.data.ready === roomId).length;
    if (readyCount === 2) startGame(roomId);
  });

  socket.on('playerChoice', (data) => {
    const game = games.get(data.roomId);
    if (!game) return;
    const index = game.players.findIndex((p) => p.socketId === socket.id);
    if (index !== game.current || typeof data.choice !== 'number') return;
    const at = game.hands[index].indexOf(data.choice);
    if (at >= 0) game.hands[index].splice(at, 1);
    game.played[index].push(data.choice);
    game.plays += 1;
    log(game, 'play_card', index, [{ type: 'PLAY', player: index, card: data.choice }]);
    game.current = 1 - index;
    if (game.plays % 2 === 0) finishTrick(game);
    else sendTurn(game);
  });

  socket.on('disconnect', () => {
    delete socket.data.ready;
  });
});

server.listen(PORT, () => console.log(`reference app listening on ${PORT}`));
