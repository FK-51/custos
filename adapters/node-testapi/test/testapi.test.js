'use strict';

// テスト用API の単体テスト（node:test）。node:20 のコンテナで実行する（run-tests.sh）。
// 対象のプロセスは test/fixtures/target-app.js（小さな Socket.IO サーバー）。

const test = require('node:test');
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const http = require('node:http');
const net = require('node:net');
const path = require('node:path');
const { io } = require('socket.io-client');

const ROOT = path.resolve(__dirname, '..');
const INDEX = path.join(ROOT, 'index.js');
const TARGET = path.join(__dirname, 'fixtures', 'target-app.js');
const FAULTS = path.join(__dirname, 'fixtures', 'faults-sample.js');

function freePort() {
  return new Promise((resolve, reject) => {
    const srv = net.createServer();
    srv.listen(0, '127.0.0.1', () => {
      const { port } = srv.address();
      srv.close(() => resolve(port));
    });
    srv.on('error', reject);
  });
}

function request(port, method, pathname, body) {
  return new Promise((resolve, reject) => {
    const data = body === undefined ? null : Buffer.from(JSON.stringify(body));
    const req = http.request(
      { host: '127.0.0.1', port, method, path: pathname, headers: data ? { 'Content-Type': 'application/json', 'Content-Length': data.length } : {} },
      (res) => {
        const chunks = [];
        res.on('data', (c) => chunks.push(c));
        res.on('end', () => {
          const text = Buffer.concat(chunks).toString('utf8');
          let json = null;
          try {
            json = JSON.parse(text);
          } catch {
            json = null;
          }
          resolve({ status: res.statusCode, json, text });
        });
      },
    );
    req.on('error', reject);
    if (data) req.write(data);
    req.end();
  });
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function startTarget({ enable = true, match = 'target-app\\.js', extraEnv = {} } = {}) {
  const appPort = await freePort();
  const apiPort = await freePort();
  const env = {
    PATH: process.env.PATH,
    APP_PORT: String(appPort),
    VERIFY_TEST_API_PORT: String(apiPort),
    VERIFY_TEST_API_HOST: '127.0.0.1',
    VERIFY_TEST_API_PROCESS_MATCH: match,
    VERIFY_TEST_API_FAULTS: FAULTS,
    NODE_OPTIONS: `--require ${INDEX}`,
    ...extraEnv,
  };
  if (enable) env.VERIFY_TEST_API = '1';
  const child = spawn(process.execPath, [TARGET], { env, stdio: ['ignore', 'pipe', 'pipe'] });
  let stdout = '';
  let stderr = '';
  child.stdout.on('data', (d) => (stdout += d));
  child.stderr.on('data', (d) => (stderr += d));
  const exited = new Promise((resolve) => child.on('exit', (code, signal) => resolve({ code, signal })));
  const deadline = Date.now() + 10000;
  while (!stdout.includes('listening') || (enable && match === 'target-app\\.js' && !stderr.includes('[verify-testapi] listening'))) {
    if (Date.now() > deadline) throw new Error(`target did not start\nstdout:${stdout}\nstderr:${stderr}`);
    if (child.exitCode !== null) throw new Error(`target exited\nstdout:${stdout}\nstderr:${stderr}`);
    await sleep(20);
  }
  const target = {
    appPort,
    apiPort,
    child,
    exited,
    get stderr() {
      return stderr;
    },
    api: (method, p, body) => request(apiPort, method, p, body),
    app: async (p) => (await request(appPort, 'GET', p)).json,
    connect: () =>
      new Promise((resolve, reject) => {
        const socket = io(`http://127.0.0.1:${appPort}`, { transports: ['websocket'], reconnection: false });
        socket.once('connect', () => resolve(socket));
        socket.once('connect_error', reject);
      }),
    stop: async () => {
      if (child.exitCode === null) child.kill('SIGKILL');
      await exited;
    },
  };
  return target;
}

test('VERIFY_TEST_API がなければ有効にならない', async () => {
  const t = await startTarget({ enable: false });
  try {
    await assert.rejects(t.api('GET', '/__verify/capabilities'), /ECONNREFUSED/);
    const state = await t.app('/state');
    assert.equal(state.compute, 20);
  } finally {
    await t.stop();
  }
});

test('引数が合わないプロセスでは有効にならない', async () => {
  const t = await startTarget({ match: 'no-such-process' });
  try {
    await assert.rejects(t.api('GET', '/__verify/capabilities'), /ECONNREFUSED/);
  } finally {
    await t.stop();
  }
});

test('能力の宣言を返し、テスト用APIの目印を環境変数から消す', async () => {
  const t = await startTarget();
  try {
    const caps = await t.api('GET', '/__verify/capabilities');
    assert.equal(caps.status, 200);
    assert.equal(caps.json.protocol_version, '0.1');
    assert.equal(caps.json.role, 'server');
    for (const name of ['settle', 'clock', 'seed', 'reset', 'faults']) assert.equal(caps.json.capabilities[name], '1.0', name);
    assert.deepEqual(caps.json.faults.sort(), ['compute-off-by-one', 'never-loaded']);
    const state = await t.app('/state');
    assert.deepEqual(state.verifyEnv, []);
    assert.equal(state.nodeOptions, null);
  } finally {
    await t.stop();
  }
});

test('環境変数を落とす実行器の下でも、焼き込んだ設定ファイルで有効になり、目印を消す', async () => {
  const fs = require('node:fs');
  const os = require('node:os');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'verify-activation-'));
  const appPort = await freePort();
  const apiPort = await freePort();
  const config = path.join(dir, 'activation.json');
  fs.writeFileSync(config, JSON.stringify({
    VERIFY_TEST_API: '1',
    VERIFY_TEST_API_PORT: String(apiPort),
    VERIFY_TEST_API_HOST: '127.0.0.1',
    VERIFY_TEST_API_PROCESS_MATCH: 'target-app\\.js',
    VERIFY_TEST_API_FAULTS: FAULTS,
  }));
  // VERIFY_TEST_API_* の環境変数は渡さない（turbo の strict env mode と同じ状況）
  const env = { PATH: process.env.PATH, APP_PORT: String(appPort), NODE_OPTIONS: `--require ${INDEX}`, VERIFY_TEST_API_CONFIG: config };
  const child = spawn(process.execPath, [TARGET], { env, stdio: ['ignore', 'pipe', 'pipe'] });
  let stderr = '';
  let stdout = '';
  child.stdout.on('data', (d) => (stdout += d));
  child.stderr.on('data', (d) => (stderr += d));
  const exited = new Promise((resolve) => child.on('exit', resolve));
  try {
    const deadline = Date.now() + 10000;
    while (!stdout.includes('listening') || !stderr.includes('[verify-testapi] listening')) {
      if (Date.now() > deadline) throw new Error(`target did not start\nstdout:${stdout}\nstderr:${stderr}`);
      await sleep(20);
    }
    const caps = await request(apiPort, 'GET', '/__verify/capabilities');
    assert.equal(caps.status, 200);
    const state = (await request(appPort, 'GET', '/state')).json;
    assert.deepEqual(state.verifyEnv, []);
    assert.equal(state.nodeOptions, null);
  } finally {
    if (child.exitCode === null) child.kill('SIGKILL');
    await exited;
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('設定ファイルが有効にしていなければ動かない', async () => {
  const fs = require('node:fs');
  const os = require('node:os');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'verify-activation-'));
  const config = path.join(dir, 'activation.json');
  fs.writeFileSync(config, JSON.stringify({ VERIFY_TEST_API: '0' }));
  const t = await startTarget({ enable: false, extraEnv: { VERIFY_TEST_API_CONFIG: config } });
  try {
    const caps = await t.api('GET', '/__verify/capabilities').catch((e) => ({ error: e }));
    assert.ok(caps.error, 'テスト用APIが応答してはいけない');
  } finally {
    await t.stop();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('知らないパスは 404', async () => {
  const t = await startTarget();
  try {
    assert.equal((await t.api('GET', '/__verify/nope')).status, 404);
    assert.equal((await t.api('GET', '/other')).status, 404);
    assert.equal((await t.api('POST', '/__verify/clock', { action: 'jump' })).status, 400);
  } finally {
    await t.stop();
  }
});

test('同じシードなら同じ乱数になり、null で元に戻る', async () => {
  const a = await startTarget();
  const b = await startTarget();
  try {
    assert.equal((await a.api('POST', '/__verify/seed', { seed: 'abc' })).status, 200);
    assert.equal((await b.api('POST', '/__verify/seed', { seed: 'abc' })).status, 200);
    const ra = await a.app('/random');
    const rb = await b.app('/random');
    assert.deepEqual(ra, rb);
    assert.match(ra.uuid, /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
    assert.ok(ra.int2 >= 5 && ra.int2 < 10);

    // シードを入れ直すと、同じ列を最初から返す
    await a.api('POST', '/__verify/seed', { seed: 'abc' });
    assert.deepEqual(await a.app('/random'), ra);

    await a.api('POST', '/__verify/seed', { seed: null });
    const u1 = await a.app('/random');
    const u2 = await a.app('/random');
    assert.notDeepEqual(u1, u2);
    assert.equal((await a.api('GET', '/__verify/seed')).json.installed, false);
  } finally {
    await a.stop();
    await b.stop();
  }
});

test('時計を進めるとアプリの長いタイマーが発火し、接続は切れない', async () => {
  const t = await startTarget();
  const socket = await t.connect();
  try {
    const before = (await t.app('/state')).now;
    socket.emit('wait-choice');
    await sleep(100);
    const advanced = await t.api('POST', '/__verify/clock', { action: 'advance', ms: 66000 });
    assert.equal(advanced.status, 200);
    const state = await t.app('/state');
    assert.equal(state.timerFired, 1);
    assert.ok(state.now - before >= 66000, `now advanced by ${state.now - before}`);

    // 生存確認（ping）は本物の時間で動くので、さらに進めても接続は残る
    await t.api('POST', '/__verify/clock', { action: 'advance', ms: 120000 });
    await sleep(300);
    assert.equal(socket.connected, true);
    const reply = await socket.timeout(5000).emitWithAck('work', 10);
    assert.deepEqual(reply, { ok: true });
  } finally {
    socket.close();
    await t.stop();
  }
});

test('時計を止めると Date が進まず、再開すると進む', async () => {
  const t = await startTarget();
  try {
    await t.api('POST', '/__verify/clock', { action: 'freeze' });
    const n1 = (await t.app('/state')).now;
    await sleep(200);
    const n2 = (await t.app('/state')).now;
    assert.equal(n2, n1);
    await t.api('POST', '/__verify/clock', { action: 'set', epoch_ms: 1700000000000 });
    assert.equal((await t.app('/state')).now, 1700000000000);
    await t.api('POST', '/__verify/clock', { action: 'resume' });
    await sleep(200);
    assert.ok((await t.app('/state')).now > 1700000000000);
  } finally {
    await t.stop();
  }
});

test('完了待ちは、イベントから始まった I/O と短いタイマーの処理が終わるまで待つ', async () => {
  const t = await startTarget();
  const socket = await t.connect();
  try {
    socket.emit('work', 300);
    await sleep(20);
    const settled = await t.api('POST', '/__verify/settle', { quiet_ms: 50, timeout_ms: 5000 });
    assert.equal(settled.status, 200, JSON.stringify(settled.json));
    assert.equal(settled.json.settled, true);
    assert.equal((await t.app('/state')).workDone, 1);
    assert.ok(settled.json.waited_ms >= 200, `waited ${settled.json.waited_ms}`);
  } finally {
    socket.close();
    await t.stop();
  }
});

test('完了待ちは、仕事の根（prisma-client-request）の中の Promise が解決するまで待つ', async () => {
  const t = await startTarget();
  const socket = await t.connect();
  try {
    socket.emit('worker-work', 400);
    await sleep(20);
    const settled = await t.api('POST', '/__verify/settle', { quiet_ms: 50, timeout_ms: 5000 });
    assert.equal(settled.json.settled, true, JSON.stringify(settled.json));
    assert.equal((await t.app('/state')).workerDone, 1);
  } finally {
    socket.close();
    await t.stop();
  }
});

test('完了待ちは、利用者の入力を待つ長いタイマーを待たない', async () => {
  const t = await startTarget();
  const socket = await t.connect();
  try {
    socket.emit('wait-choice');
    await sleep(20);
    const started = Date.now();
    const settled = await t.api('POST', '/__verify/settle', { quiet_ms: 50, timeout_ms: 5000 });
    assert.equal(settled.json.settled, true, JSON.stringify(settled.json));
    assert.ok(Date.now() - started < 2000);
    assert.equal((await t.app('/state')).timerFired, 0);
  } finally {
    socket.close();
    await t.stop();
  }
});

test('完了待ちは、短いタイマーを待つ。長めのタイマーは時計を進めて片付けられる', async () => {
  const t = await startTarget();
  const socket = await t.connect();
  try {
    socket.emit('short-timer', 300);
    await sleep(20);
    let settled = await t.api('POST', '/__verify/settle', { quiet_ms: 50, timeout_ms: 5000 });
    assert.equal(settled.json.settled, true);
    assert.equal((await t.app('/state')).shortTimerFired, 1);

    socket.emit('short-timer', 5000);
    await sleep(20);
    const started = Date.now();
    settled = await t.api('POST', '/__verify/settle', { quiet_ms: 50, timeout_ms: 5000, flush_timers_under_ms: 6000 });
    assert.equal(settled.json.settled, true);
    assert.ok(settled.json.flushed_timers >= 1);
    assert.ok(Date.now() - started < 3000);
    assert.equal((await t.app('/state')).shortTimerFired, 2);
  } finally {
    socket.close();
    await t.stop();
  }
});

test('動き続けている間は、完了待ちが上限時間で失敗を返す', async () => {
  const t = await startTarget();
  const socket = await t.connect();
  try {
    socket.emit('busy', 3000);
    await sleep(50);
    const settled = await t.api('POST', '/__verify/settle', { quiet_ms: 100, timeout_ms: 500 });
    assert.equal(settled.status, 504);
    assert.equal(settled.json.settled, false);
    assert.equal(settled.json.reason, 'timeout');
  } finally {
    socket.close();
    await t.stop();
  }
});

test('異常を切り替えると挙動が変わり、リセットは登録された処理を呼ぶ', async () => {
  const t = await startTarget();
  try {
    assert.equal((await t.app('/state')).compute, 20);
    const list = await t.api('GET', '/__verify/faults');
    const fault = list.json.faults.find((f) => f.name === 'compute-off-by-one');
    assert.equal(fault.loaded, true);
    assert.equal(fault.enabled, false);
    assert.equal(list.json.faults.find((f) => f.name === 'never-loaded').loaded, false);

    await t.api('POST', '/__verify/faults', { name: 'compute-off-by-one', enabled: true });
    assert.equal((await t.app('/state')).compute, 21);
    await t.api('POST', '/__verify/faults', { disable: ['compute-off-by-one'] });
    assert.equal((await t.app('/state')).compute, 20);
    assert.equal((await t.api('POST', '/__verify/faults', { name: 'unknown' })).status, 400);

    const reset = await t.api('POST', '/__verify/reset', {});
    assert.equal(reset.status, 200);
    assert.equal(reset.json.hooks, 1);
  } finally {
    await t.stop();
  }
});

test('偽装した時計のタイマーの中の例外は、本物と同じくプロセスを落とす', async () => {
  const t = await startTarget();
  const socket = await t.connect();
  try {
    socket.emit('throw-in-timer');
    const result = await Promise.race([t.exited, sleep(5000).then(() => null)]);
    assert.notEqual(result, null, 'process did not exit');
    assert.notEqual(result.code, 0);
    assert.match(t.stderr, /boom-in-timer/);
  } finally {
    socket.close();
    await t.stop();
  }
});
