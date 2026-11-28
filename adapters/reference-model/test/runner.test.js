'use strict';

// 参照モデルの実行器のテスト（node --test adapters/reference-model/test/runner.test.js）。
const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const { execFileSync } = require('node:child_process');

const runner = path.join(__dirname, '..', 'runner.js');

function run(model, games) {
  const out = execFileSync(process.execPath, [runner], {
    input: JSON.stringify({ games }),
    env: { ...process.env, CUSTOS_REF_MODEL: path.join(__dirname, 'fixtures', model) },
  });
  return JSON.parse(out.toString('utf8'));
}

const action = (seq, type, changes, snapshot) => ({
  seq,
  type,
  player: 0,
  changes: JSON.stringify(changes),
  snapshot: snapshot === undefined ? null : JSON.stringify(snapshot),
});

test('検査役の形：毎手を確かめ、規則に反する手を返し、アプリの変化で状態を進める', () => {
  const result = run('checker-model.js', [
    {
      game: 'g1',
      actions: [
        action(0, 'start', [], { n: 0 }),
        action(1, 'move', [{ type: 'ADD', by: 1 }]),
        action(2, 'move', [{ type: 'ADD', by: 3 }]),          // 規則に反する
        action(3, 'skip', [{ type: 'ADD', by: 1 }]),          // 確かめない手（状態は進める）
        action(4, 'move', [{ type: 'ADD', by: 1 }], { n: 6 }), // apply で進めた状態（6）と写しが一致
        action(5, 'move', [{ type: 'ADD', by: 2 }], { n: 9 }), // 写しは 9、apply では 8：apply の食い違い
      ],
    },
  ]);
  assert.equal(result.model_version, 'test-checker');
  const g = result.results[0];
  assert.equal(g.mode, 'check');
  assert.equal(g.checked, 4);
  assert.deepEqual(g.violations.map((v) => [v.seq, v.rule]), [[2, 'add_at_most_two']]);
  assert.equal(g.unsupported['確かめない手'], 1);
  assert.equal(g.compared, 2);
  assert.equal(g.apply_mismatches.length, 1);
  assert.equal(g.apply_mismatches[0].seq, 5);
});

test('検査役の形：apply が失敗したら、次の写しまで確かめずに写しから続ける', () => {
  const g = run('checker-model.js', [
    {
      game: 'g2',
      actions: [
        action(0, 'start', [], { n: 0 }),
        action(1, 'move', [{ type: 'BROKEN' }]),
        action(2, 'move', [{ type: 'ADD', by: 5 }]),
        action(3, 'move', [{ type: 'ADD', by: 1 }], { n: 7 }),
        action(4, 'move', [{ type: 'ADD', by: 3 }]),
      ],
    },
  ]).results[0];
  // 1手目は check で確かめてから apply で失敗する。2・3手目は確かめず、写し（3手目）から続けて4手目を確かめる
  assert.equal(g.checked, 2);
  assert.equal(g.unverified_spans, 1);
  assert.deepEqual(g.violations.map((v) => v.seq), [4]);
});

test('状態を計算する形：写しと比べ、対応していない区間は未検証にする', () => {
  const g = run('stepper-model.js', [
    {
      game: 'g3',
      actions: [
        action(0, 'start', [], { n: 0 }),
        action(1, 'move', []),
        action(2, 'move', [], { n: 2 }),  // 一致
        action(3, 'move', [], { n: 4 }),  // 参照モデルは 3：食い違い
        action(4, 'skip', []),
        action(5, 'move', [], { n: 9 }),  // 未検証の区間
      ],
    },
  ]).results[0];
  assert.equal(g.mode, 'step');
  assert.equal(g.compared, 2);
  assert.deepEqual(g.mismatches.map((m) => m.seq), [3]);
  assert.equal(g.unverified_spans, 1);
});

test('begin に、対局ごとの提示された選択肢が渡る', () => {
  const g = run('prompt-model.js', [
    {
      game: 'g4',
      prompts: [{ seq: 10, turn_number: 1, kind: 'draw', choices: [3, 5] }],
      actions: [action(0, 'start', [], { n: 0 }), action(1, 'move', [{ type: 'ADD', by: 1 }])],
    },
  ]).results[0];
  assert.deepEqual(g.violations.map((v) => v.rule), ['prompt_seen:g4:draw:2']);
});
