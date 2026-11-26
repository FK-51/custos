'use strict';

// 参照モデルの実行器（設計書 第25.9節）。管理側で、ネットワークなし（--network none）のコンテナで動かす。
// 標準入力：{ games: [{ game, actions: [{ seq, type, player, changes, snapshot }] }] }（手のログ）
// 参照モデル（/ref/model.js）：
//   module.exports = {
//     version: '0.1',
//     supported: { cards: [1, 2, ...] },              // 対応しているカード（対応率の分母はすべてのカード）
//     step(state, action) { return { state } | { unsupported: '理由' } },
//   };
//   state はログの状態の写し（stateSnapshotJson）と同じ形。action はログの1行（種類、手番の人、変化）。
// 比べ方：最初の写しから参照モデルで1手ずつ進め、写しのある手で比べる。参照モデルが対応していない手は、
// アプリの次の写しを新しい基準として取り込み（追従）、その区間を「未検証」と記録する。
const ref = require('/ref/model.js');

function diff(a, b, path, out) {
  if (out.length >= 5 || a === b) return;
  if (typeof a !== typeof b || a === null || b === null || typeof a !== 'object') {
    out.push({ path, reference: a, app: b });
    return;
  }
  const keys = new Set([...Object.keys(a), ...Object.keys(b)]);
  for (const k of keys) diff(a[k], b[k], `${path}.${k}`, out);
}

let input = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', (d) => (input += d));
process.stdin.on('end', () => {
  const { games } = JSON.parse(input);
  const results = [];
  for (const g of games) {
    const r = { game: g.game, compared: 0, unverified_spans: 0, mismatches: [], unsupported: {} };
    let state = null;
    let verified = true;
    for (const a of g.actions.sort((x, y) => Number(x.seq) - Number(y.seq))) {
      const action = { seq: a.seq, type: a.type, player: a.player, changes: a.changes ? JSON.parse(a.changes) : [] };
      if (state !== null) {
        let out;
        try {
          out = ref.step(JSON.parse(JSON.stringify(state)), action);
        } catch (e) {
          out = { unsupported: `参照モデルの例外：${e && e.message}` };
        }
        if (out && out.state) state = out.state;
        else {
          verified = false;
          const reason = (out && out.unsupported) || '不明';
          r.unsupported[reason] = (r.unsupported[reason] || 0) + 1;
        }
      }
      if (a.snapshot) {
        const snap = JSON.parse(a.snapshot);
        if (state !== null && verified) {
          const d = [];
          diff(state, snap, '$', d);
          r.compared++;
          if (d.length) r.mismatches.push({ seq: a.seq, diff: d });
        } else if (state !== null) {
          r.unverified_spans++;
        }
        state = snap; // 写しに合わせて続ける
        verified = true;
      }
    }
    results.push(r);
  }
  process.stdout.write(JSON.stringify({ model_version: ref.version || 'unknown', supported: ref.supported || {}, results }));
});
