'use strict';

// テスト用の状態を計算する参照モデル：ADD は常に 1 を足す（アプリが 2 を足すと食い違う）。
module.exports = {
  version: 'test-stepper',
  supported: { cards: [] },
  step(state, action) {
    if (action.type === 'skip') return { unsupported: '計算しない手' };
    return { state: { ...state, n: state.n + 1 } };
  },
};
