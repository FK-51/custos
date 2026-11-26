'use strict';

// シードつきの決まった乱数（sfc32）。シードの文字列から cyrb128 で初期状態を作る。
// 同じシードなら、同じ版の Node で同じ列を返す（設計書 第14.8節「決定性の定義」）。

function cyrb128(str) {
  let h1 = 1779033703, h2 = 3144134277, h3 = 1013904242, h4 = 2773480762;
  for (let i = 0, k; i < str.length; i++) {
    k = str.charCodeAt(i);
    h1 = h2 ^ Math.imul(h1 ^ k, 597399067);
    h2 = h3 ^ Math.imul(h2 ^ k, 2869860233);
    h3 = h4 ^ Math.imul(h3 ^ k, 951274213);
    h4 = h1 ^ Math.imul(h4 ^ k, 2716044179);
  }
  h1 = Math.imul(h3 ^ (h1 >>> 18), 597399067);
  h2 = Math.imul(h4 ^ (h2 >>> 22), 2869860233);
  h3 = Math.imul(h1 ^ (h3 >>> 17), 951274213);
  h4 = Math.imul(h2 ^ (h4 >>> 19), 2716044179);
  h1 ^= h2 ^ h3 ^ h4;
  h2 ^= h1;
  h3 ^= h1;
  h4 ^= h1;
  return [h1 >>> 0, h2 >>> 0, h3 >>> 0, h4 >>> 0];
}

/** 32ビットの符号なし整数を返す関数を作る。 */
function createUint32(seed) {
  let [a, b, c, d] = cyrb128(String(seed));
  const next = () => {
    a >>>= 0; b >>>= 0; c >>>= 0; d >>>= 0;
    let t = (a + b) | 0;
    a = b ^ (b >>> 9);
    b = (c + (c << 3)) | 0;
    c = (c << 21) | (c >>> 11);
    d = (d + 1) | 0;
    t = (t + d) | 0;
    c = (c + t) | 0;
    return t >>> 0;
  };
  // 初期状態のかたよりを捨てる
  for (let i = 0; i < 15; i++) next();
  return next;
}

/** 乱数の源。uint32、float（0以上1未満）、bytes、int（min以上max未満）を持つ。 */
function createPrng(seed) {
  const uint32 = createUint32(seed);
  return {
    uint32,
    float: () => uint32() / 4294967296,
    fill(target) {
      for (let i = 0; i < target.length; i += 4) {
        let v = uint32();
        for (let j = 0; j < 4 && i + j < target.length; j++) {
          target[i + j] = v & 0xff;
          v >>>= 8;
        }
      }
      return target;
    },
    int(min, max) {
      const range = max - min;
      if (range <= 0) throw new RangeError('max must be greater than min');
      // 2^48 未満の範囲で、かたよりのない整数（棄却法）
      if (range <= 4294967296) {
        const limit = Math.floor(4294967296 / range) * range;
        let v;
        do { v = uint32(); } while (v >= limit);
        return min + (v % range);
      }
      const hi = uint32() & 0xffff;
      const value = hi * 4294967296 + uint32();
      return min + (value % range);
    },
  };
}

module.exports = { createPrng, cyrb128 };
