'use strict';

// テスト用の検査役の参照モデル：ADD は数を足す。規則「1手で足せるのは 2 まで」。
module.exports = {
  version: 'test-checker',
  supported: { cards: [1] },
  apply(state, change) {
    if (change.type === 'ADD') return { ...state, n: state.n + change.by };
    if (change.type === 'BROKEN') throw new Error('知らない変化');
    return state;
  },
  check(state, action) {
    if (action.type === 'skip') return { unsupported: '確かめない手' };
    const total = action.changes.filter((c) => c.type === 'ADD').reduce((s, c) => s + c.by, 0);
    return { violations: total > 2 ? [{ rule: 'add_at_most_two', message: `足した数 ${total}（${state.n} から）` }] : [] };
  },
};
