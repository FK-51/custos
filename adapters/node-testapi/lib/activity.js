'use strict';

// プロセスの中の処理が空になったかを見る（POST /__verify/settle のため）。
//
// async_hooks で次を追う。
//   - 動き：コールバックが走った最後の時刻（テスト用API自身の動きは数えない）
//   - 1回きりの処理（I/O の要求など）：作られてから破棄されるまで
//   - 仕事の根（例：Prisma の "prisma-client-request"）：その中で作られた Promise がすべて解決するまで
//   - 本物のタイマー（時計を偽装していない場合）：短いものだけ
//
// 長いタイマー（選択の待ち時間など）や、利用者の入力を待っている Promise は待たない。
// 待ち受け中のソケットのような、ずっと残る資源も数えない。

const asyncHooks = require('async_hooks');
const { performance } = require('perf_hooks');

const now = () => performance.now();

/**
 * 1回きりの処理（要求）。コールバックが一度走ったら終わりとみなす。
 * 破棄（destroy）は後でまとめて起きることがある（例：RANDOMBYTESREQUEST）ので、破棄だけを終わりの合図にしない。
 */
const IO_REQUEST_TYPES = new Set([
  'FSREQCALLBACK', 'FSREQPROMISE', 'GETADDRINFOREQWRAP', 'GETNAMEINFOREQWRAP', 'QUERYWRAP',
  'CONNECTWRAP', 'PIPECONNECTWRAP', 'TCPCONNECTWRAP', 'UDPSENDWRAP',
]);
/** 書き込み。同期で書き終えた場合は resource.async が false になり、コールバックの前後の合図が来ない。 */
const WRITE_REQUEST_TYPES = new Set(['WRITEWRAP', 'SHUTDOWNWRAP']);
/** crypto のジョブ。同期で実行した場合（randomBytes(size) など）は ondone が付かず、前後の合図が来ない。 */
const CRYPTO_JOB_TYPES = new Set([
  'RANDOMBYTESREQUEST', 'RANDOMPRIMEREQUEST', 'CHECKPRIMEREQUEST', 'PBKDF2REQUEST', 'SCRYPTREQUEST',
  'KEYGENREQUEST', 'KEYPAIRGENREQUEST', 'CIPHERREQUEST', 'DERIVEBITSREQUEST', 'HASHREQUEST',
  'SIGNREQUEST', 'VERIFYREQUEST', 'HKDFREQUEST', 'KEYEXPORTREQUEST',
]);
const REQUEST_TYPES = new Set([...IO_REQUEST_TYPES, ...WRITE_REQUEST_TYPES, ...CRYPTO_JOB_TYPES]);

/** テスト用API自身の資源の型。 */
const OURS_TYPE = 'VERIFY_TESTAPI';
/** テスト用APIが、偽装した時計のタイマーを発火させるときに使う型。中で走るのはアプリの処理なので、自分のものとして扱わない。 */
const APP_CALLBACKS_TYPE = 'VERIFY_APP_CALLBACKS';

class ActivityTracker {
  constructor({ workTypes = ['prisma-client-request'], rootMaxAgeMs = 30000, requestMaxAgeMs = 30000 } = {}) {
    this.workTypes = new Set(workTypes);
    this.rootMaxAgeMs = rootMaxAgeMs;
    this.requestMaxAgeMs = requestMaxAgeMs;
    this.ours = new Set();
    this.requests = new Map(); // asyncId -> { type, since, ref: WeakRef<resource> }
    this.timeouts = new Map(); // asyncId -> { ms, repeat, since }
    this.roots = new Map(); // rootId -> { type, since, pending: Set<promiseId>, destroyed }
    this.rootOf = new Map(); // asyncId -> rootId
    this.lastActivity = now();
    this.lastActivityType = null;
    this.types = new Map(); // asyncId -> type（診断用。根と要求とタイマーだけ）
    this.appScopes = new Set(); // 偽装した時計のタイマーを発火させるための資源（それ自体の実行は動きとして数えない）
    this.appScope = null;
    this.hook = null;
  }

  enable() {
    if (this.hook) return;
    const self = this;
    this.hook = asyncHooks.createHook({
      init(asyncId, type, triggerAsyncId, resource) {
        self._init(asyncId, type, triggerAsyncId, resource);
      },
      before(asyncId) {
        if (!self.ours.has(asyncId) && !self.appScopes.has(asyncId)) {
          self.lastActivity = now();
          self.lastActivityType = self.types.get(asyncId) || null;
        }
      },
      after(asyncId) {
        // 要求は、コールバックが一度走ったら終わり
        self.requests.delete(asyncId);
      },
      destroy(asyncId) {
        self._destroy(asyncId);
      },
      promiseResolve(asyncId) {
        const root = self.rootOf.get(asyncId);
        if (root !== undefined) {
          const entry = self.roots.get(root);
          if (entry) entry.pending.delete(asyncId);
        }
      },
    });
    this.hook.enable();
  }

  _init(asyncId, type, triggerAsyncId, resource) {
    const executionAsyncId = asyncHooks.executionAsyncId();
    if (type === OURS_TYPE) {
      this.ours.add(asyncId);
      return;
    }
    if (type === APP_CALLBACKS_TYPE) {
      this.appScopes.add(asyncId);
      return;
    }
    if (type !== APP_CALLBACKS_TYPE && (this.ours.has(triggerAsyncId) || this.ours.has(executionAsyncId))) {
      this.ours.add(asyncId);
      return;
    }

    let root;
    if (this.workTypes.has(type)) {
      root = asyncId;
      this.roots.set(asyncId, { type, since: now(), pending: new Set(), destroyed: false });
      this.types.set(asyncId, type);
    } else {
      root = this.rootOf.get(triggerAsyncId);
      if (root === undefined) root = this.rootOf.get(executionAsyncId);
    }
    if (root !== undefined) {
      this.rootOf.set(asyncId, root);
      if (type === 'PROMISE') {
        const entry = this.roots.get(root);
        if (entry) entry.pending.add(asyncId);
      }
    }

    if (REQUEST_TYPES.has(type)) {
      this.requests.set(asyncId, { type, since: now(), ref: resource && typeof resource === 'object' ? new WeakRef(resource) : null });
      this.types.set(asyncId, type);
    } else if (type === 'Timeout' && resource) {
      const ms = typeof resource._idleTimeout === 'number' ? resource._idleTimeout : -1;
      const repeat = typeof resource._repeat === 'number' && resource._repeat > 0;
      this.timeouts.set(asyncId, { ms, repeat, since: now() });
      this.types.set(asyncId, type);
    }
  }

  _destroy(asyncId) {
    this.ours.delete(asyncId);
    this.appScopes.delete(asyncId);
    this.requests.delete(asyncId);
    this.timeouts.delete(asyncId);
    this.types.delete(asyncId);
    const root = this.rootOf.get(asyncId);
    if (root !== undefined) {
      const entry = this.roots.get(root);
      if (entry) entry.pending.delete(asyncId);
      this.rootOf.delete(asyncId);
    }
    const own = this.roots.get(asyncId);
    if (own) {
      own.destroyed = true;
      if (own.pending.size === 0) this.roots.delete(asyncId);
    }
  }

  /** テスト用API自身の処理として走らせる（その中の動きは数えない）。 */
  runOurs(fn) {
    const resource = new asyncHooks.AsyncResource(OURS_TYPE);
    return resource.runInAsyncScope(fn);
  }

  /**
   * アプリの処理として走らせる（偽装した時計のタイマーを発火させるとき）。
   * 中で作られた資源はアプリのものとして扱う。資源そのものの実行は動きとして数えない（発火したときは markActivity で記録する）。
   */
  runApp(fn) {
    if (!this.appScope) this.appScope = new asyncHooks.AsyncResource(APP_CALLBACKS_TYPE, { requireManualDestroy: true });
    return this.appScope.runInAsyncScope(fn);
  }

  /** 動きがあったことを記録する（偽装した時計のタイマーが発火したとき）。 */
  markActivity(kind) {
    this.lastActivity = now();
    this.lastActivityType = kind || null;
  }

  /** 最後の動きからの経過時間（ミリ秒）。 */
  quietForMs() {
    return now() - this.lastActivity;
  }

  /** 要求がまだ終わっていないか（コールバックの前後の合図が来ない、同期で終わった要求を除く）。 */
  _requestInFlight(r) {
    const resource = r.ref ? r.ref.deref() : undefined;
    if (WRITE_REQUEST_TYPES.has(r.type)) return !(resource && resource.async === false);
    if (CRYPTO_JOB_TYPES.has(r.type)) return Boolean(resource && typeof resource.ondone === 'function');
    return true;
  }

  /** 終わっていない処理の一覧。 */
  pending({ shortTimerMs = 1000, includeRealTimers = true } = {}) {
    const t = now();
    const requests = [];
    const staleRequests = [];
    for (const [id, r] of this.requests) {
      if (!this._requestInFlight(r)) {
        this.requests.delete(id);
        continue;
      }
      const item = { id, type: r.type, age_ms: Math.round(t - r.since) };
      if (t - r.since > this.requestMaxAgeMs) staleRequests.push(item);
      else requests.push(item);
    }

    const work = [];
    const stale = [];
    for (const [id, r] of this.roots) {
      if (r.pending.size === 0) {
        if (r.destroyed || t - r.since > 1000) this.roots.delete(id);
        continue;
      }
      const age = t - r.since;
      const item = { id, type: r.type, pending_promises: r.pending.size, age_ms: Math.round(age) };
      if (age > this.rootMaxAgeMs) stale.push(item);
      else work.push(item);
    }

    const timers = [];
    if (includeRealTimers) {
      for (const [id, r] of this.timeouts) {
        if (r.repeat || r.ms < 0 || r.ms > shortTimerMs) continue;
        const remaining = r.ms - (t - r.since);
        timers.push({ id, kind: 'real', delay_ms: r.ms, remaining_ms: Math.max(0, Math.round(remaining)) });
      }
    }
    return { requests, work, timers, stale_work: stale.concat(staleRequests) };
  }
}

module.exports = { ActivityTracker, now, OURS_TYPE, APP_CALLBACKS_TYPE };
