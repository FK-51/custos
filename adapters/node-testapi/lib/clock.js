'use strict';

// 時刻の制御（POST /__verify/clock）。
//
// @sinonjs/fake-timers で Date と setTimeout/clearTimeout/setInterval/clearInterval を偽装する。
// ふだんは本物の時間と同じ速さで進め（shouldAdvanceTime と同じ振る舞いを自前の刻みで行う。止められるようにするため）、
// advance で先へ進める（制限時間の検査で、時間切れを今すぐ起こす。設計書 第25.8節）。
//
// 通信の層（engine.io、socket.io、ws）のタイマーは偽装しない（globalThis と timers モジュールの両方）。時刻を一気に進めたときに、
// 生存確認（ping）の待ち時間が切れて、接続が切れてしまうのを避けるため。
// どの呼び出し元を本物のタイマーにするかは、setTimeout を直接呼んだファイルの場所で決める。

const path = require('path');
const FakeTimers = require('@sinonjs/fake-timers');
const { performance } = require('perf_hooks');

const OWN_DIR = path.resolve(__dirname, '..');
// このパッケージ自身のフレーム（lib/、index.js）と、時計を偽装するライブラリのフレーム。
// node_modules の下（テスト用の依存など）は自分のフレームに含めない。
const OWN_FRAMES = [path.join(OWN_DIR, 'lib') + path.sep, path.join(OWN_DIR, 'index.js'), `${path.sep}@sinonjs${path.sep}fake-timers${path.sep}`];

/**
 * setTimeout を直接呼んだ場所（スタックの、このパッケージの外の最初のフレーム）を返す。
 * スタック全体で判定すると、socket.io から呼ばれたアプリのイベント処理の中のタイマーまで
 * 「通信の層のもの」と誤って判定するので、直接の呼び出し元だけを見る。
 */
function immediateCaller() {
  const limit = Error.stackTraceLimit;
  Error.stackTraceLimit = 8;
  const stack = new Error().stack || '';
  Error.stackTraceLimit = limit;
  const lines = stack.split('\n').slice(1);
  for (const line of lines) {
    if (OWN_FRAMES.some((frame) => line.includes(frame))) continue;
    return line;
  }
  return '';
}

const DEFAULT_REAL_TIMER_CALLERS =
  /[\\/]node_modules[\\/](engine\.io|engine\.io-client|socket\.io|socket\.io-client|socket\.io-adapter|ws)[\\/]/;

class Clock {
  constructor({ activity, real, tickMs = 10, realTimerCallers = DEFAULT_REAL_TIMER_CALLERS }) {
    this.activity = activity;
    this.real = real; // 偽装の前に控えた本物のタイマー
    this.tickMs = tickMs;
    this.realTimerCallers = realTimerCallers;
    this.clock = null;
    this.frozen = false;
    this.lastReal = 0;
    this.pump = null;
    this.realTimers = new WeakSet();
    this.realTimerIds = new Set();
    this.installedAt = null;
  }

  get installed() {
    return this.clock !== null;
  }

  install() {
    if (this.clock) return;
    this.clock = FakeTimers.withGlobal(globalThis).install({
      now: Date.now(),
      toFake: ['Date', 'setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'],
      shouldAdvanceTime: false,
      loopLimit: 100000,
    });
    this.installedAt = new Date(this.clock.now).toISOString();
    this._routeTransportTimers();
    this.lastReal = performance.now();
    // 刻みはテスト用API自身の処理（動きとして数えない）
    this.pump = this.activity.runOurs(() => this.real.setInterval(() => this._onPump(), this.tickMs));
    if (typeof this.pump.unref === 'function') this.pump.unref();
  }

  _routeTransportTimers() {
    // fake-timers は globalThis だけでなく Node の timers モジュールも偽装する（engine.io は timers モジュールを使う）。
    // 両方を「直接の呼び出し元で、本物か偽装かを選ぶ」関数で包む。
    const real = this.real;
    const self = this;
    const useReal = () => self.realTimerCallers.test(immediateCaller());
    const remember = (timer) => {
      self.realTimers.add(timer);
      try {
        self.realTimerIds.add(Number(timer));
      } catch {
        // 数にできないタイマーは、オブジェクトでだけ覚える
      }
      return timer;
    };
    const isReal = (timer) => {
      if (timer === null || timer === undefined) return false;
      if (typeof timer === 'object') return self.realTimers.has(timer);
      return self.realTimerIds.has(Number(timer));
    };

    const wrap = (target) => {
      const fake = {
        setTimeout: target.setTimeout,
        clearTimeout: target.clearTimeout,
        setInterval: target.setInterval,
        clearInterval: target.clearInterval,
      };
      target.setTimeout = function setTimeout(fn, ms, ...args) {
        if (useReal()) return remember(real.setTimeout(fn, ms, ...args));
        return fake.setTimeout(fn, ms, ...args);
      };
      target.clearTimeout = function clearTimeout(timer) {
        if (isReal(timer)) return real.clearTimeout(timer);
        return fake.clearTimeout(timer);
      };
      target.setInterval = function setInterval(fn, ms, ...args) {
        if (useReal()) return remember(real.setInterval(fn, ms, ...args));
        return fake.setInterval(fn, ms, ...args);
      };
      target.clearInterval = function clearInterval(timer) {
        if (isReal(timer)) return real.clearInterval(timer);
        return fake.clearInterval(timer);
      };
    };

    wrap(globalThis);
    const timersModule = require('timers');
    if (timersModule.setTimeout !== real.setTimeout) wrap(timersModule);
  }

  _onPump() {
    const t = performance.now();
    const elapsed = t - this.lastReal;
    this.lastReal = t;
    if (!this.frozen && elapsed > 0) this._tickApp(elapsed);
  }

  /** 偽装したタイマーの一覧（fake-timers v15 は Map、それより前は object で持つ）。 */
  _timerList() {
    const timers = this.clock ? this.clock.timers : null;
    if (!timers) return [];
    return timers instanceof Map ? [...timers.values()] : Object.values(timers);
  }

  _dueWithin(ms) {
    let count = 0;
    for (const timer of this._timerList()) {
      if (timer.callAt <= this.clock.now + ms) count++;
    }
    return count;
  }

  /** 偽装した時計を進め、期限の来たアプリのタイマーを発火させる。 */
  _tickApp(ms) {
    const due = this._dueWithin(ms);
    let error = null;
    this.activity.runApp(() => {
      try {
        this.clock.tick(ms);
      } catch (e) {
        error = e;
      }
    });
    if (due > 0) this.activity.markActivity('fake-timer');
    if (error) {
      // 本物のタイマーと同じく、処理されない例外として扱う（クラッシュの検知を偽装で隠さない）
      process.nextTick(() => {
        throw error;
      });
    }
  }

  freeze() {
    this.frozen = true;
  }

  resume() {
    this.frozen = false;
    this.lastReal = performance.now();
  }

  advance(ms) {
    if (!(ms >= 0)) throw new RangeError('ms must be a non-negative number');
    this._tickApp(ms);
  }

  setSystemTime(epochMs) {
    if (!Number.isFinite(epochMs)) throw new RangeError('epoch_ms must be a number');
    this.clock.setSystemTime(epochMs);
  }

  /** 偽装したタイマーのうち、threshold 以内に期限が来る1回きりのもの。 */
  pendingTimers(thresholdMs) {
    const list = [];
    if (!this.clock) return list;
    for (const timer of this._timerList()) {
      if (timer.interval) continue;
      const remaining = timer.callAt - this.clock.now;
      if (remaining <= thresholdMs) list.push({ id: timer.id, kind: 'fake', remaining_ms: Math.max(0, Math.round(remaining)) });
    }
    return list;
  }

  /** threshold 以内で最も早く期限が来る1回きりのタイマーまでの時間。なければ null。 */
  nextDueWithin(thresholdMs) {
    let best = null;
    for (const timer of this._timerList()) {
      if (timer.interval) continue;
      const remaining = Math.max(0, timer.callAt - this.clock.now);
      if (remaining <= thresholdMs && (best === null || remaining < best)) best = remaining;
    }
    return best;
  }

  status() {
    if (!this.clock) return { installed: false };
    const timers = this._timerList();
    const oneShot = timers.filter((t) => !t.interval).map((t) => Math.max(0, Math.round(t.callAt - this.clock.now)));
    return {
      installed: true,
      frozen: this.frozen,
      now: this.clock.now,
      iso: new Date(this.clock.now).toISOString(),
      installed_at: this.installedAt,
      pending_timers: timers.length,
      next_due_ms: oneShot.length ? Math.min(...oneShot) : null,
    };
  }
}

module.exports = { Clock, DEFAULT_REAL_TIMER_CALLERS };
