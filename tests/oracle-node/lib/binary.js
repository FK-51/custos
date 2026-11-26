'use strict';

// バイナリ（Buffer、ArrayBuffer、TypedArray）を JSON で表すための共通処理。
// 正解のデータでは、復元後の値の中のバイナリを {"_binary_b64": "..."} で表す。

function isBinary(value) {
  return (
    Buffer.isBuffer(value) ||
    value instanceof ArrayBuffer ||
    ArrayBuffer.isView(value)
  );
}

function toBuffer(value) {
  if (Buffer.isBuffer(value)) return value;
  if (value instanceof ArrayBuffer) return Buffer.from(value);
  if (ArrayBuffer.isView(value)) return Buffer.from(value.buffer, value.byteOffset, value.byteLength);
  throw new TypeError('not binary');
}

/** 値の中のバイナリを {"_binary_b64": "..."} に置き換えた複製を返す。 */
function toJsonValue(value) {
  if (value === undefined) return null;
  if (isBinary(value)) return { _binary_b64: toBuffer(value).toString('base64') };
  if (Array.isArray(value)) return value.map(toJsonValue);
  if (value !== null && typeof value === 'object') {
    // __proto__ という名前の項目も、自分の項目として残す（プロトタイプにしない）
    const out = Object.create(null);
    for (const key of Object.keys(value)) out[key] = toJsonValue(value[key]);
    return out;
  }
  return value;
}

module.exports = { isBinary, toBuffer, toJsonValue };
