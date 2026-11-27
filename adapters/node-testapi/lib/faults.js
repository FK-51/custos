'use strict';

// 切り替えが必要な異常の一覧と有効化（GET・POST /__verify/faults）。
//
// 異常の定義は VERIFY_TEST_API_FAULTS に指定したモジュールから読む。形：
//
//   module.exports = {
//     faults: {
//       'card7-draws-two': {
//         description: '…',
//         patches: {
//           // 読み込まれたモジュールのパスの末尾（または正規表現）→ export を差し替える関数
//           'packages/server/dist/domain/state/applyChange.js': (exports, fault) => {
//             const original = exports.applyChange;
//             exports.applyChange = function (state, change) {
//               if (fault.enabled()) { /* 異常な振る舞い */ }
//               return original.call(this, state, change);
//             };
//           },
//         },
//       },
//     },
//     reset() { /* POST /__verify/reset で呼ばれる */ },
//   };
//
// 異常な振る舞いが実際に起きたときに fault.hit() を呼び、定義に reportsHits: true を書くと、
// 一覧の hits（有効にしてから起きた回数）で「異常の箇所を通ったか」が分かる。
// カナリアは、通らなかった試行（例：効果の誤りを仕込んだカードが一度も出なかった対局）を「見逃し」と区別する。
// reportsHits のない定義の hits は null（分からない）。
//
// 差し替えは require のフックで、対象のモジュールが読み込まれたときに一度だけ行う。
// 有効かどうかは呼び出しのたびに fault.enabled() で見るので、実行中に切り替えられる。
// 校正用のバグはパッチで仕込む（設計書 第19.4節）。ここは適合テストの陽性確認のように、切り替えが要る場面に限る。

const Module = require('module');

class FaultRegistry {
  constructor() {
    this.definitions = new Map(); // name -> { description, reportsHits, patches: [{ match, apply }] }
    this.enabled = new Set();
    this.hits = new Map(); // name -> 有効にしてから異常な振る舞いが起きた回数
    this.applied = new Map(); // name -> Set<filename>
    this.resetHooks = [];
    this.modulePath = null;
    this.loadError = null;
    this.hookInstalled = false;
  }

  load(modulePath) {
    this.modulePath = modulePath;
    let mod;
    try {
      mod = require(modulePath);
    } catch (e) {
      this.loadError = e.message;
      return;
    }
    const faults = (mod && mod.faults) || {};
    for (const [name, def] of Object.entries(faults)) {
      const patches = [];
      for (const [match, apply] of Object.entries((def && def.patches) || {})) {
        if (typeof apply !== 'function') continue;
        patches.push({ match, apply });
      }
      this.definitions.set(name, { description: (def && def.description) || '', reportsHits: Boolean(def && def.reportsHits), patches });
      this.applied.set(name, new Set());
      this.hits.set(name, 0);
    }
    if (mod && typeof mod.reset === 'function') this.resetHooks.push(mod.reset);
    this._installHook();
  }

  _matches(match, filename) {
    const normalized = filename.split('\\').join('/');
    if (match.startsWith('/') && match.endsWith('/') && match.length > 2) {
      try {
        return new RegExp(match.slice(1, -1)).test(normalized);
      } catch {
        return false;
      }
    }
    return normalized.endsWith(match);
  }

  _installHook() {
    if (this.hookInstalled) return;
    this.hookInstalled = true;
    const registry = this;
    const originalLoad = Module._load;
    const done = new Set();
    Module._load = function load(request, parent, isMain) {
      const exports = originalLoad.apply(this, arguments);
      let filename;
      try {
        filename = Module._resolveFilename(request, parent, isMain);
      } catch {
        return exports;
      }
      if (done.has(filename)) return exports;
      let replaced = exports;
      for (const [name, def] of registry.definitions) {
        for (const patch of def.patches) {
          if (!registry._matches(patch.match, filename)) continue;
          const api = {
            name,
            enabled: () => registry.enabled.has(name),
            // 異常な振る舞いが実際に起きた（有効なときだけ数える）
            hit: () => {
              if (registry.enabled.has(name)) registry.hits.set(name, (registry.hits.get(name) || 0) + 1);
            },
          };
          const result = patch.apply(replaced, api);
          if (result !== undefined) replaced = result;
          registry.applied.get(name).add(filename);
        }
      }
      done.add(filename);
      if (replaced !== exports) {
        const cached = Module._cache[filename];
        if (cached) cached.exports = replaced;
      }
      return replaced;
    };
  }

  list() {
    return [...this.definitions.entries()].map(([name, def]) => ({
      name,
      description: def.description,
      enabled: this.enabled.has(name),
      loaded: (this.applied.get(name) || new Set()).size > 0,
      hits: def.reportsHits ? this.hits.get(name) || 0 : null,
      targets: def.patches.map((p) => p.match),
    }));
  }

  set(name, on) {
    if (!this.definitions.has(name)) throw new Error(`unknown fault: ${name}`);
    if (on) {
      // 数え直すのは、無効から有効に変えたときだけ（有効のまま重ねて送っても数は消さない）
      if (!this.enabled.has(name)) this.hits.set(name, 0);
      this.enabled.add(name);
    } else this.enabled.delete(name);
  }

  onReset(fn) {
    this.resetHooks.push(fn);
  }

  async runResetHooks() {
    for (const hook of this.resetHooks) await hook();
    return this.resetHooks.length;
  }
}

module.exports = { FaultRegistry };
