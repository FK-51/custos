'use strict';

// 管理用の HTTP サーバー。アプリの公開ポートとは別のポートで待ち受ける（記録用のプロキシは転送しないので、利用者側からは届かない）。
// エンドポイントは設計書 第14.6節のとおり。

const http = require('http');
const { now } = require('./activity');

const PROTOCOL_VERSION = '0.1';
const MAX_BODY = 64 * 1024;

function send(res, status, body) {
  const text = JSON.stringify(body);
  res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Content-Length': Buffer.byteLength(text), 'Cache-Control': 'no-store' });
  res.end(text);
}

function readJson(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0;
    req.on('data', (chunk) => {
      size += chunk.length;
      if (size > MAX_BODY) {
        reject(new HttpError(413, 'request body too large'));
        req.destroy();
        return;
      }
      chunks.push(chunk);
    });
    req.on('end', () => {
      const text = Buffer.concat(chunks).toString('utf8').trim();
      if (text === '') return resolve({});
      try {
        const value = JSON.parse(text);
        if (value === null || typeof value !== 'object' || Array.isArray(value)) return reject(new HttpError(400, 'body must be a JSON object'));
        resolve(value);
      } catch (e) {
        reject(new HttpError(400, `invalid JSON: ${e.message}`));
      }
    });
    req.on('error', reject);
  });
}

class HttpError extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
  }
}

function number(body, key, fallback, { min = 0, max = Number.MAX_SAFE_INTEGER } = {}) {
  const value = body[key];
  if (value === undefined || value === null) return fallback;
  if (typeof value !== 'number' || !Number.isFinite(value) || value < min || value > max) {
    throw new HttpError(400, `${key} must be a number between ${min} and ${max}`);
  }
  return value;
}

function createHandlers(ctx) {
  const { activity, clock, seed, faults, info, real } = ctx;

  const sleep = (ms) => new Promise((resolve) => real.setTimeout(resolve, ms));

  async function settle(body) {
    const quietMs = number(body, 'quiet_ms', 100, { max: 60000 });
    const timeoutMs = number(body, 'timeout_ms', 10000, { min: 1, max: 600000 });
    const shortTimerMs = number(body, 'short_timer_ms', 1000, { max: 600000 });
    const flushUnderMs = number(body, 'flush_timers_under_ms', 0, { max: 3600000 });
    const started = now();
    let flushed = 0;
    for (;;) {
      if (flushUnderMs > 0 && clock.installed) {
        const due = clock.nextDueWithin(flushUnderMs);
        if (due !== null) {
          clock.advance(due + 1);
          flushed++;
          await sleep(1);
          continue;
        }
      }

      const pending = activity.pending({ shortTimerMs });
      const fakeTimers = clock.installed ? clock.pendingTimers(shortTimerMs) : [];
      // 時計を止めている間は、偽装したタイマーは進まない。待っても終わらないので、待つ対象から外して報告だけする
      const frozenTimers = clock.installed && clock.frozen ? fakeTimers : [];
      const timers = pending.timers.concat(clock.installed && clock.frozen ? [] : fakeTimers);
      const busy = pending.requests.length + pending.work.length + timers.length;
      const quietFor = activity.quietForMs();
      const waited = Math.round(now() - started);
      if (busy === 0 && quietFor >= quietMs) {
        return {
          status: 200,
          body: {
            settled: true,
            waited_ms: waited,
            quiet_for_ms: Math.round(quietFor),
            flushed_timers: flushed,
            frozen_timers: frozenTimers,
            stale_work: pending.stale_work,
          },
        };
      }
      if (now() - started >= timeoutMs) {
        return {
          status: 504,
          body: {
            settled: false,
            reason: 'timeout',
            waited_ms: waited,
            quiet_for_ms: Math.round(quietFor),
            last_activity_type: activity.lastActivityType,
            pending: { requests: pending.requests, work: pending.work, timers },
            frozen_timers: frozenTimers,
            stale_work: pending.stale_work,
          },
        };
      }
      await sleep(Math.max(2, Math.min(10, quietMs / 4)));
    }
  }

  function capabilities() {
    const caps = { settle: '1.0', seed: '1.0', reset: '1.0' };
    if (clock.installed) caps.clock = '1.0';
    if (faults.modulePath) caps.faults = '1.0';
    return {
      protocol_version: PROTOCOL_VERSION,
      role: 'server',
      implementation: 'custos-node-testapi',
      implementation_version: info.version,
      node: process.version,
      pid: process.pid,
      capabilities: caps,
      faults: faults.list().map((f) => f.name),
      faults_error: faults.loadError,
    };
  }

  async function handle(req, res) {
    const url = new URL(req.url, 'http://localhost');
    const route = `${req.method} ${url.pathname}`;
    switch (route) {
      case 'GET /__verify/capabilities':
        return send(res, 200, capabilities());

      case 'POST /__verify/settle': {
        const result = await settle(await readJson(req));
        return send(res, result.status, result.body);
      }

      case 'GET /__verify/clock':
        return send(res, 200, clock.status());

      case 'POST /__verify/clock': {
        if (!clock.installed) throw new HttpError(409, 'clock is not installed (VERIFY_TEST_API_CLOCK=0)');
        const body = await readJson(req);
        switch (body.action) {
          case 'freeze':
            clock.freeze();
            break;
          case 'resume':
            clock.resume();
            break;
          case 'advance':
            clock.advance(number(body, 'ms', 0, { max: 365 * 24 * 3600 * 1000 }));
            break;
          case 'set':
            clock.setSystemTime(number(body, 'epoch_ms', Date.now(), { min: 0 }));
            break;
          default:
            throw new HttpError(400, 'action must be one of freeze, resume, advance, set');
        }
        return send(res, 200, clock.status());
      }

      case 'GET /__verify/seed':
        return send(res, 200, seed.status());

      case 'POST /__verify/seed': {
        const body = await readJson(req);
        if (!('seed' in body)) throw new HttpError(400, 'seed is required (null to restore the real random source)');
        if (body.seed !== null && typeof body.seed !== 'string' && typeof body.seed !== 'number') {
          throw new HttpError(400, 'seed must be a string, a number, or null');
        }
        seed.install(body.seed);
        const status = seed.status();
        // 確認用の乱数の見本（適合テスト B-3）：決めたシードで出し、出した後にもう一度同じシードを入れ直す（アプリに渡る乱数の列は変えない）
        const count = Number(body.sample || 0);
        if (body.seed !== null && Number.isInteger(count) && count > 0 && count <= 100) {
          const crypto = require('crypto');
          status.samples = Array.from({ length: count }, () => crypto.randomInt(0, 1000000));
          seed.install(body.seed);
        }
        return send(res, 200, status);
      }

      case 'POST /__verify/reset': {
        const hooks = await faults.runResetHooks();
        return send(res, 200, { ok: true, hooks });
      }

      case 'GET /__verify/faults':
        return send(res, 200, { faults: faults.list(), module: faults.modulePath, error: faults.loadError });

      case 'POST /__verify/faults': {
        const body = await readJson(req);
        const setFault = (name, on) => {
          try {
            faults.set(name, on);
          } catch (e) {
            throw new HttpError(400, e.message);
          }
        };
        if (typeof body.name === 'string') setFault(body.name, body.enabled !== false);
        for (const name of Array.isArray(body.enable) ? body.enable : []) setFault(String(name), true);
        for (const name of Array.isArray(body.disable) ? body.disable : []) setFault(String(name), false);
        if (body.disable_all === true) for (const f of faults.list()) setFault(f.name, false);
        return send(res, 200, { faults: faults.list() });
      }

      default:
        if (url.pathname.startsWith('/__verify/')) throw new HttpError(404, `unknown endpoint: ${route}`);
        throw new HttpError(404, 'not found');
    }
  }

  return { handle, settle, capabilities };
}

function createServer(ctx) {
  const { activity, port, host } = ctx;
  const { handle } = createHandlers(ctx);
  return activity.runOurs(() => {
    const server = http.createServer((req, res) => {
      handle(req, res).catch((e) => {
        const status = e instanceof HttpError ? e.status : 500;
        if (!res.headersSent) send(res, status, { error: e.message });
        else res.destroy();
      });
    });
    server.on('error', (e) => {
      process.stderr.write(`[verify-testapi] server error: ${e.message}\n`);
    });
    server.listen(port, host, () => {
      process.stderr.write(`[verify-testapi] listening on ${host}:${port} (pid ${process.pid})\n`);
    });
    // テスト用APIだけが残ってプロセスが終わらなくなることを避ける
    server.unref();
    return server;
  });
}

module.exports = { createServer, createHandlers, PROTOCOL_VERSION };
