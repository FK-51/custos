'use strict';

// テスト用API の本体の組み立て。index.js から、有効にすると決めたプロセスでだけ呼ばれる。

const path = require('path');
const { ActivityTracker } = require('./activity');
const { Clock, DEFAULT_REAL_TIMER_CALLERS } = require('./clock');
const { SeedControl } = require('./seed');
const { FaultRegistry } = require('./faults');
const { createServer } = require('./server');

const VERSION = require('../package.json').version;

function readOptions(env) {
  const list = (text, fallback) =>
    text === undefined ? fallback : text.split(',').map((s) => s.trim()).filter(Boolean);
  let realTimerCallers = DEFAULT_REAL_TIMER_CALLERS;
  if (env.VERIFY_TEST_API_REAL_TIMERS) realTimerCallers = new RegExp(env.VERIFY_TEST_API_REAL_TIMERS);
  return {
    port: Number(env.VERIFY_TEST_API_PORT || 3999),
    host: env.VERIFY_TEST_API_HOST || '0.0.0.0',
    clock: env.VERIFY_TEST_API_CLOCK !== '0',
    realTimerCallers,
    workTypes: list(env.VERIFY_TEST_API_WORK_TYPES, ['prisma-client-request']),
    rootMaxAgeMs: Number(env.VERIFY_TEST_API_ROOT_MAX_AGE_MS || 30000),
    faultsModule: env.VERIFY_TEST_API_FAULTS ? path.resolve(env.VERIFY_TEST_API_FAULTS) : null,
    initialSeed: env.VERIFY_TEST_API_SEED,
    keepEnv: env.VERIFY_TEST_API_KEEP_ENV === '1',
  };
}

/**
 * 有効にした後は、このプロセスの環境変数からテスト用APIの目印を消す。
 * アプリが環境変数を見て「検証中だけ」振る舞いを変える余地を減らすため（設計書 第12.6節）。
 */
function scrubEnvironment(env) {
  for (const key of Object.keys(env)) {
    if (key.startsWith('VERIFY_TEST_API')) delete env[key];
  }
  if (typeof env.NODE_OPTIONS === 'string') {
    const self = path.resolve(__dirname, '..');
    const kept = env.NODE_OPTIONS.split(/\s+/).filter(Boolean);
    const out = [];
    for (let i = 0; i < kept.length; i++) {
      const part = kept[i];
      if ((part === '--require' || part === '-r') && kept[i + 1] && path.resolve(kept[i + 1]).startsWith(self)) {
        i++;
        continue;
      }
      if (part.startsWith('--require=') && path.resolve(part.slice('--require='.length)).startsWith(self)) continue;
      out.push(part);
    }
    if (out.length === 0) delete env.NODE_OPTIONS;
    else env.NODE_OPTIONS = out.join(' ');
  }
}

let started = null;

function start(env = process.env) {
  if (started) return started;
  // 時計を偽装する前に、本物のタイマーを控えておく（テスト用API自身はこれを使う）
  const real = { setTimeout, clearTimeout, setInterval, clearInterval };
  const options = readOptions(env);

  const activity = new ActivityTracker({ workTypes: options.workTypes, rootMaxAgeMs: options.rootMaxAgeMs });
  activity.enable();

  const faults = new FaultRegistry();
  if (options.faultsModule) faults.load(options.faultsModule);

  const seed = new SeedControl();
  // engine.io・socket.io が ID の乱数をためておく base64id を、読み込まれる前に包む（シードを入れている間の ID を固定するため）
  seed.hookPooledSources();
  if (options.initialSeed !== undefined && options.initialSeed !== '') seed.install(options.initialSeed);

  const clock = new Clock({ activity, real, realTimerCallers: options.realTimerCallers });
  if (options.clock) clock.install();

  if (!options.keepEnv) scrubEnvironment(env);

  const server = createServer({ activity, clock, seed, faults, real, port: options.port, host: options.host, info: { version: VERSION } });
  started = { activity, clock, seed, faults, server, options };
  return started;
}

module.exports = { start, readOptions, scrubEnvironment, VERSION };
