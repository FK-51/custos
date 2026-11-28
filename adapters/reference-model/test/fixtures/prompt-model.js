'use strict';

// テスト用：begin で受け取った選択の要求を、最初の手の確かめで「反する点」として返して見せる。
let seen = [];
let game = null;
module.exports = {
  version: 'test-prompt',
  supported: { cards: [] },
  begin(input) {
    game = input.game;
    seen = input.prompts;
  },
  apply(state, change) {
    return change.type === 'ADD' ? { ...state, n: state.n + change.by } : state;
  },
  check() {
    const v = seen.map((p) => ({ rule: `prompt_seen:${game}:${p.kind}:${p.choices.length}`, message: '' }));
    seen = [];
    return { violations: v };
  },
};
