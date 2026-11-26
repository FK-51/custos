'use strict';

// テスト用の対象プロセス（Socket.IO サーバー）。halpal に似た非同期の処理をする。
//   work         イベントから始まる非同期の処理（ファイルの I/O と短いタイマー）
//   wait-choice  利用者の入力を待つ長いタイマー（65秒。完了待ちはこれを待たない）
//   short-timer  短いタイマー
//   worker-work  Prisma のように別のスレッドで処理して Promise で返す（"prisma-client-request" の資源の中で）
//   busy         しばらく動き続ける
//   throw-in-timer タイマーの中で例外を投げる（クラッシュの検知を偽装で隠さないことの確認）

const http = require('http');
const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');
const { AsyncResource } = require('async_hooks');
const { Worker } = require('worker_threads');
const { Server } = require('socket.io');
const target = require('./target-module');

const state = { workDone: 0, timerFired: 0, shortTimerFired: 0, workerDone: 0 };

const server = http.createServer((req, res) => {
  if (req.url === '/state') {
    res.end(
      JSON.stringify({
        ...state,
        now: Date.now(),
        compute: target.compute(2),
        verifyEnv: Object.keys(process.env).filter((k) => k.startsWith('VERIFY')),
        nodeOptions: process.env.NODE_OPTIONS || null,
      }),
    );
    return;
  }
  if (req.url === '/random') {
    res.end(
      JSON.stringify({
        int: crypto.randomInt(1000000),
        int2: crypto.randomInt(5, 10),
        uuid: crypto.randomUUID(),
        bytes: crypto.randomBytes(8).toString('hex'),
        math: Math.random(),
        webcrypto: Array.from(globalThis.crypto.getRandomValues(new Uint8Array(4))),
      }),
    );
    return;
  }
  res.writeHead(404);
  res.end('not found');
});

const io = new Server(server);
io.on('connection', (socket) => {
  socket.on('work', async (ms, cb) => {
    const file = path.join(os.tmpdir(), `work-${process.pid}-${Math.floor(Math.random() * 1e9)}`);
    await fs.promises.writeFile(file, 'x'.repeat(1000));
    await new Promise((resolve) => setTimeout(resolve, ms));
    await fs.promises.readFile(file);
    await fs.promises.unlink(file);
    state.workDone++;
    if (typeof cb === 'function') cb({ ok: true });
  });

  socket.on('wait-choice', () => {
    new Promise((resolve) =>
      setTimeout(() => {
        state.timerFired++;
        resolve();
      }, 65000),
    );
  });

  socket.on('short-timer', (ms) => {
    setTimeout(() => {
      state.shortTimerFired++;
    }, ms);
  });

  socket.on('worker-work', (ms) => {
    const resource = new AsyncResource('prisma-client-request');
    resource.runInAsyncScope(() =>
      new Promise((resolve) => {
        const worker = new Worker(
          "const { parentPort, workerData } = require('worker_threads'); setTimeout(() => parentPort.postMessage('done'), workerData);",
          { eval: true, workerData: ms },
        );
        worker.once('message', () => {
          worker.terminate();
          resolve();
        });
      }).then(() => {
        state.workerDone++;
      }),
    );
  });

  socket.on('busy', (ms) => {
    const end = Date.now() + ms;
    const tick = () => {
      if (Date.now() < end) setImmediate(tick);
    };
    tick();
  });

  socket.on('throw-in-timer', () => {
    setTimeout(() => {
      throw new Error('boom-in-timer');
    }, 10);
  });
});

server.listen(Number(process.env.APP_PORT || 3000), '0.0.0.0', () => {
  process.stdout.write(`listening ${server.address().port}\n`);
});
