'use strict';

// テスト用の異常の定義（VERIFY_TEST_API_FAULTS に指定する）。
let resets = 0;

module.exports = {
  faults: {
    'compute-off-by-one': {
      description: 'compute の結果が1つずれる',
      reportsHits: true,
      patches: {
        'test/fixtures/target-module.js': (exports, fault) => {
          const original = exports.compute;
          exports.compute = (x) => {
            if (!fault.enabled()) return original(x);
            fault.hit();
            return original(x) + 1;
          };
        },
      },
    },
    'never-loaded': {
      description: '読み込まれないモジュールを対象にした異常',
      patches: {
        'no/such/module.js': () => {},
      },
    },
  },
  reset() {
    resets++;
  },
  resets: () => resets,
};
