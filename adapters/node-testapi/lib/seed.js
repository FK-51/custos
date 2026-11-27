'use strict';

// 乱数の固定（POST /__verify/seed）。
// crypto.randomInt、randomUUID、randomBytes、randomFill(Sync)、getRandomValues（webcrypto を含む）、Math.random を、
// シードつきの決まった乱数で置き換える。シードを null にすると元に戻す。
//
// アプリが「呼び出しのたびに crypto.xxx を引く」書き方（TypeScript の import crypto from 'crypto' のコンパイル結果など）なら効く。
// 読み込みの時点で関数を取り出して持っている書き方には効かない（README の制約）。

const crypto = require('crypto');
const Module = require('module');
const { createPrng } = require('./prng');

class SeedControl {
  constructor() {
    this.seed = null;
    this.prng = null;
    this.originals = null;
    this.patched = [];
    this.pooled = new WeakSet();
    this.hooked = false;
  }

  get installed() {
    return this.prng !== null;
  }

  /**
   * 乱数をためておく部品を包む（アプリのモジュールより前に呼ぶ。テスト用APIは preload で動くので間に合う）。
   *
   * base64id（engine.io の接続の ID と socket.io の socket.id を作る）は、crypto.randomBytes でまとめて作った乱数を
   * ためておき、そこから ID を作る。起動時にためた分は、後からシードを入れても変わらない。
   * socket.id はアプリの振る舞いに効くことがある（例：利用者の ID に使い、その並びで席の順番が決まる）ので、
   * シードを入れている間は、ためた乱数を使わず、シードつきの crypto.randomBytes から作る（設計書 第22.2節「乱数、UUID」）。
   *
   * base64id は、パッケージ（socket.io、古い engine.io が require('base64id') で使う）と、
   * 新しい engine.io が同梱した写し（build/contrib/base64id.js。exports.base64id）の両方を包む。
   * ID の後ろ4文字は base64id のプロセスの中の通し番号で、シードを入れ直しても戻さない
   * （戻すと、残っている古い接続と ID が重なりうる）。シードで決まるのは前の 16 文字（乱数の部分）。
   */
  hookPooledSources() {
    if (this.hooked) return;
    this.hooked = true;
    const self = this;
    const originalLoad = Module._load;
    Module._load = function load(request) {
      const exports = originalLoad.apply(this, arguments);
      if (typeof request === 'string' && request.includes('base64id') && exports) {
        self._wrapBase64id(typeof exports.getRandomBytes === 'function' ? exports : exports.base64id);
      }
      return exports;
    };
  }

  _wrapBase64id(instance) {
    if (!instance || typeof instance !== 'object' || typeof instance.getRandomBytes !== 'function' || this.pooled.has(instance)) return;
    const self = this;
    const original = instance.getRandomBytes;
    instance.getRandomBytes = function getRandomBytes(bytes) {
      // シードを入れている間は crypto.randomBytes（シードつきに置き換え済み）を、その場で呼ぶ
      if (self.prng) return crypto.randomBytes(bytes || 12);
      return original.apply(this, arguments);
    };
    this.pooled.add(instance);
  }

  install(seed) {
    if (seed === null || seed === undefined) {
      this.uninstall();
      return;
    }
    if (!this.originals) this._captureOriginals();
    this.seed = String(seed);
    this.prng = createPrng(this.seed);
    this._patch();
  }

  uninstall() {
    if (!this.originals) return;
    for (const [target, key, descriptor] of this.patched) {
      if (descriptor) Object.defineProperty(target, key, descriptor);
      else delete target[key];
    }
    this.patched = [];
    this.prng = null;
    this.seed = null;
  }

  _captureOriginals() {
    this.originals = { Math_random: Math.random };
  }

  _define(target, key, value) {
    if (!target) return false;
    const descriptor = Object.getOwnPropertyDescriptor(target, key);
    // 再定義できない項目は飛ばす（例：Node 20 の crypto.getRandomValues は再定義できないゲッターだが、
    // 中で webcrypto.getRandomValues を呼ぶので、webcrypto の側を置き換えれば効く）
    if (descriptor && descriptor.configurable === false) return false;
    if (!this.patched.some(([t, k]) => t === target && k === key)) this.patched.push([target, key, descriptor]);
    Object.defineProperty(target, key, { value, configurable: true, writable: true, enumerable: descriptor ? descriptor.enumerable : true });
    return true;
  }

  _patch() {
    const self = this;
    const prng = () => {
      if (!self.prng) throw new Error('seed is not installed');
      return self.prng;
    };

    const randomBytes = function randomBytes(size, callback) {
      if (!Number.isInteger(size) || size < 0) throw new RangeError('The value of "size" is out of range.');
      const buffer = Buffer.alloc(size);
      prng().fill(buffer);
      if (typeof callback === 'function') {
        process.nextTick(callback, null, buffer);
        return undefined;
      }
      return buffer;
    };

    const randomInt = function randomInt(min, max, callback) {
      // randomInt(max[, callback]) と randomInt(min, max[, callback])
      if (typeof max === 'undefined' || typeof max === 'function') {
        callback = max;
        max = min;
        min = 0;
      }
      if (!Number.isSafeInteger(min) || !Number.isSafeInteger(max) || max <= min) {
        throw new RangeError('The value of "max" is out of range. It must be greater than "min".');
      }
      const value = prng().int(min, max);
      if (typeof callback === 'function') {
        process.nextTick(callback, null, value);
        return undefined;
      }
      return value;
    };

    const randomUUID = function randomUUID() {
      const b = prng().fill(Buffer.alloc(16));
      b[6] = (b[6] & 0x0f) | 0x40;
      b[8] = (b[8] & 0x3f) | 0x80;
      const h = b.toString('hex');
      return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
    };

    const fillView = (view, offset, size) => {
      const bytes = new Uint8Array(view.buffer, view.byteOffset, view.byteLength);
      const start = offset || 0;
      const end = size === undefined ? bytes.length : start + size;
      prng().fill(bytes.subarray(start, end));
      return view;
    };

    const randomFillSync = function randomFillSync(buffer, offset, size) {
      return fillView(buffer, offset, size);
    };

    const randomFill = function randomFill(buffer, offset, size, callback) {
      if (typeof offset === 'function') {
        callback = offset;
        offset = undefined;
      } else if (typeof size === 'function') {
        callback = size;
        size = undefined;
      }
      fillView(buffer, offset, size);
      process.nextTick(callback, null, buffer);
    };

    const getRandomValues = function getRandomValues(view) {
      if (!ArrayBuffer.isView(view)) throw new TypeError('The data argument must be an integer-type TypedArray');
      return fillView(view);
    };

    this._define(crypto, 'randomBytes', randomBytes);
    this._define(crypto, 'pseudoRandomBytes', randomBytes);
    this._define(crypto, 'randomInt', randomInt);
    this._define(crypto, 'randomUUID', randomUUID);
    this._define(crypto, 'randomFillSync', randomFillSync);
    this._define(crypto, 'randomFill', randomFill);
    this._define(crypto, 'getRandomValues', getRandomValues);
    if (crypto.webcrypto) {
      this._define(crypto.webcrypto, 'getRandomValues', getRandomValues);
      this._define(crypto.webcrypto, 'randomUUID', randomUUID);
    }
    if (globalThis.crypto && globalThis.crypto !== crypto.webcrypto) {
      this._define(globalThis.crypto, 'getRandomValues', getRandomValues);
      this._define(globalThis.crypto, 'randomUUID', randomUUID);
    }
    this._define(Math, 'random', function random() {
      return prng().float();
    });
  }

  status() {
    return { installed: this.installed, seed: this.seed };
  }
}

module.exports = { SeedControl };
