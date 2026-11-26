'use strict';

// Custos の Node 向けテスト用API の入口（設計書 第14.6節、第25.11節）。
//
//   node --require /opt/verify-testapi/index.js dist/index.js
//
// テスト用ビルドに組み込み、さらに明示的に有効にしたときだけ動く（第14.9節）。有効にする方法は次のどちらか。
//   1. 環境変数 VERIFY_TEST_API=1（ほかの設定も VERIFY_TEST_API_* の環境変数で渡す）
//   2. テスト用ビルドに焼き込んだ設定ファイル（既定は この部品の activation.json。VERIFY_TEST_API_CONFIG で変えられる）に
//      "VERIFY_TEST_API": "1" と書く。turbo のように、タスクを起動するときに環境変数を落とす実行器の下でも有効にできる。
// NODE_OPTIONS で渡すと、同じコンテナの全部の Node のプロセスがこのファイルを読むので、
// 引数が VERIFY_TEST_API_PROCESS_MATCH（既定 dist/index\.js）に合うプロセスだけで有効にする。
// 本番用ビルドには入れない。

(function activate() {
  let env = process.env;
  if (env.VERIFY_TEST_API !== '1') {
    const fs = require('fs');
    const path = require('path');
    const file = process.env.VERIFY_TEST_API_CONFIG || path.join(__dirname, 'activation.json');
    let config;
    try {
      config = JSON.parse(fs.readFileSync(file, 'utf8'));
    } catch (e) {
      return;
    }
    if (!config || typeof config !== 'object' || config.VERIFY_TEST_API !== '1') return;
    env = {};
    for (const [key, value] of Object.entries(config)) {
      if (key.startsWith('VERIFY_TEST_API') && typeof value === 'string') env[key] = value;
    }
  }
  let pattern;
  try {
    pattern = new RegExp(env.VERIFY_TEST_API_PROCESS_MATCH || 'dist/index\\.js');
  } catch (e) {
    process.stderr.write(`[verify-testapi] invalid VERIFY_TEST_API_PROCESS_MATCH: ${e.message}\n`);
    return;
  }
  const argv = process.argv.slice(1).join(' ');
  if (!pattern.test(argv)) return;
  const api = require('./lib/testapi');
  const keepEnv = env.VERIFY_TEST_API_KEEP_ENV === '1';
  api.start(env);
  // 設定ファイルで有効にした場合も、このプロセスの環境変数からテスト用APIの目印を消す
  if (env !== process.env && !keepEnv) api.scrubEnvironment(process.env);
})();
