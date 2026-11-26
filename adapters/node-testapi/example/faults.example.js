'use strict';

// 異常の定義の例（halpal を想定）。実際の定義は検証用リポジトリに置く（判定用のデータなので、アプリのリポジトリには置かない）。
module.exports = {
  faults: {
    'card7-draws-two': {
      description: '正夢（7）の効果で、次のドローが3枚ではなく2枚になる（適合テスト D の陽性確認用）',
      patches: {
        'packages/server/dist/domain/state/applyChange.js': (exports, fault) => {
          const original = exports.applyChange;
          exports.applyChange = function applyChange(state, change) {
            if (fault.enabled() && change && change.type === 'SET_PLAYER_GET' && change.get === 3) {
              change = { ...change, get: 2 };
            }
            return original.call(this, state, change);
          };
        },
      },
    },
  },
  reset() {},
};
