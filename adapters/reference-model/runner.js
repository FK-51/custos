'use strict';

// 参照モデルの実行器（設計書 第25.9節）。管理側で、ネットワークなし（--network none）のコンテナで動かす。
// 標準入力：{ games: [{ game, actions: [{ seq, type, player, changes, snapshot }] }] }（手のログ）
//
// 参照モデル（/ref/model.js）は、次の2つの形のどちらかで書く。
//
// 1) 検査役の形（apply と check を持つ。こちらを勧める）
//   module.exports = {
//     version: '0.1',
//     supported: { cards: [1, 2, ...] },          // 規則を確かめているカード（対応率の分母はすべてのカード）
//     apply(state, change) { return nextState },    // アプリが記録した変化1件を状態に当てる（変化の形の定義だけから書ける）
//     check(state, action) {                        // 手の前の状態と、その手でアプリが記録した変化（action.changes）から、
//       return { violations: [{ rule, message }] }  //   規則に反する点を返す（なければ空の配列）
//         | { unsupported: '理由' };                 //   確かめられない手
//     },
//   };
//   進め方：手ごとに check で確かめてから、その手の変化を apply で当てて次の手に進む。状態の写しのある手では、
//   apply で進めた状態と写しを比べ（食い違いは apply の誤りかアプリの記録の誤りで、apply_mismatches に入れる）、写しに合わせて続ける。
//   アプリの変化で状態を進めるので、参照モデルの計算がずれても次の手の検査に影響しない（毎手を確かめられる）。
//
// 2) 状態を計算する形（step だけを持つ。以前からの形）
//   module.exports = { version, supported, step(state, action) { return { state } | { unsupported: '理由' } } };
//   最初の写しから参照モデルで1手ずつ進め、写しのある手で比べる。対応していない手は、アプリの次の写しを新しい基準として
//   取り込み（追従）、その区間を「未検証」と記録する。
//
// state はログの状態の写し（stateSnapshotJson）と同じ形。action はログの1行（seq、type、player、changes）。
//
// どちらの形でも、任意で begin({ game, prompts }) を持てる。対局の手を読む前に1回呼ぶ。prompts は、その対局で各プレイヤーに
// 送られた選択の要求の列（届いた順。{ seq, turn_number, kind, choices }）。手のログには「何枚から選ばせたか」が残らないので、
// 提示された選択肢の数や中身を規則と比べるときに使う（設計書 第25.9節「打てる手の列挙」）。
const ref = require(process.env.CUSTOS_REF_MODEL || '/ref/model.js');

function diff(a, b, path, out) {
  if (out.length >= 5 || a === b) return;
  if (typeof a !== typeof b || a === null || b === null || typeof a !== 'object') {
    out.push({ path, reference: a, app: b });
    return;
  }
  const keys = new Set([...Object.keys(a), ...Object.keys(b)]);
  for (const k of keys) diff(a[k], b[k], `${path}.${k}`, out);
}

function clone(value) {
  return JSON.parse(JSON.stringify(value));
}

function runChecker(g) {
  const r = { game: g.game, mode: 'check', checked: 0, compared: 0, unverified_spans: 0, mismatches: [], violations: [], apply_mismatches: [], unsupported: {} };
  let state = null;
  for (const a of g.actions.sort((x, y) => Number(x.seq) - Number(y.seq))) {
    const action = { seq: a.seq, type: a.type, player: a.player, changes: a.changes ? JSON.parse(a.changes) : [] };
    if (state !== null) {
      let out;
      try {
        out = ref.check(clone(state), clone(action));
      } catch (e) {
        out = { unsupported: `参照モデルの例外（check）：${e && e.message}` };
      }
      if (out && Array.isArray(out.violations)) {
        r.checked++;
        for (const v of out.violations) r.violations.push({ seq: a.seq, type: a.type, player: a.player, rule: v.rule || 'unknown', message: v.message || '' });
      } else {
        const reason = (out && out.unsupported) || '不明';
        r.unsupported[reason] = (r.unsupported[reason] || 0) + 1;
      }
      try {
        for (const change of action.changes) state = ref.apply(state, clone(change));
      } catch (e) {
        // 当てられなければ、次の写しまでの区間は確かめられない
        r.unsupported[`参照モデルの例外（apply）：${e && e.message}`] = (r.unsupported[`参照モデルの例外（apply）：${e && e.message}`] || 0) + 1;
        state = null;
      }
    }
    if (a.snapshot) {
      const snap = JSON.parse(a.snapshot);
      if (state !== null) {
        const d = [];
        diff(state, snap, '$', d);
        r.compared++;
        if (d.length) r.apply_mismatches.push({ seq: a.seq, diff: d });
      } else if (r.compared > 0 || r.checked > 0) {
        r.unverified_spans++;
      }
      state = snap;
    }
  }
  return r;
}

function runStepper(g) {
  const r = { game: g.game, mode: 'step', compared: 0, unverified_spans: 0, mismatches: [], violations: [], apply_mismatches: [], unsupported: {} };
  let state = null;
  let verified = true;
  for (const a of g.actions.sort((x, y) => Number(x.seq) - Number(y.seq))) {
    const action = { seq: a.seq, type: a.type, player: a.player, changes: a.changes ? JSON.parse(a.changes) : [] };
    if (state !== null) {
      let out;
      try {
        out = ref.step(clone(state), action);
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
  return r;
}

function run(games) {
  const checker = typeof ref.check === 'function' && typeof ref.apply === 'function';
  return games.map((g) => {
    if (typeof ref.begin === 'function') ref.begin({ game: g.game, prompts: clone(g.prompts || []) });
    return checker ? runChecker(g) : runStepper(g);
  });
}

module.exports = { run, diff };

if (require.main === module) {
  let input = '';
  process.stdin.setEncoding('utf8');
  process.stdin.on('data', (d) => (input += d));
  process.stdin.on('end', () => {
    const { games } = JSON.parse(input);
    const results = run(games);
    process.stdout.write(JSON.stringify({ model_version: ref.version || 'unknown', supported: ref.supported || {}, results }));
  });
}
