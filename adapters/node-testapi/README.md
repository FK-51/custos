# custos-node-testapi（Node 向けのテスト用API）

設計書 第14.6節、第25.11節のテスト用APIを、アプリのコードを変えずに **テスト用ビルドにだけ** 組み込むための部品。
Node の `--require`（preload）でアプリより先に読み込み、アプリの公開ポートとは別のポートで `/__verify/*` を待ち受ける。

- **本番用ビルドには入れない。** テスト用ビルドで、かつ `VERIFY_TEST_API=1` で明示的に有効にしたときだけ動く（第14.9節）。
- 待ち受けるのは管理用のポート（既定 3999）。記録用のプロキシはアプリの公開ポートにしか転送しないので、利用者側（AIのサンドボックス、スクリプトクライアント、ブラウザ）からは届かない。管理側（ops コンテナ）からだけ使う（第12.6節）。
- 本番用ビルドで `/__verify/` が存在しないパスと同じ応答になることは、適合テスト E-5 で確かめる。

## 組み込み方（テスト用ビルドの Dockerfile の例）

```dockerfile
# 本番用ビルドのイメージの上に重ねる（アプリのリポジトリは変えない）
ARG APP_IMAGE
FROM ${APP_IMAGE}
USER root
COPY node-testapi/ /opt/verify-testapi/
RUN cd /opt/verify-testapi && npm ci --omit=dev --no-audit --no-fund --ignore-scripts \
 && chmod -R a+rX /opt/verify-testapi
COPY faults.js /opt/verify-faults/faults.js
USER node
ENV NODE_OPTIONS="--require /opt/verify-testapi/index.js" \
    VERIFY_TEST_API=1 \
    VERIFY_TEST_API_FAULTS=/opt/verify-faults/faults.js
```

`example/Dockerfile.testbuild` に同じものを置いてある。halpal のように1つのコンテナで turbo が複数の Node のプロセス（ゲームサーバーと Next.js）を起動する場合でも、`NODE_OPTIONS` は全部のプロセスに渡るが、有効になるのは引数が `VERIFY_TEST_API_PROCESS_MATCH`（既定 `dist/index\.js`）に合うプロセス（ゲームサーバー）だけである。

有効にしたプロセスでは、`VERIFY_TEST_API*` の環境変数と、`NODE_OPTIONS` の中のこの部品の `--require` を、`process.env` から消す（アプリが環境変数を見て検証中だけ振る舞いを変える余地を減らすため。第12.6節）。残す場合は `VERIFY_TEST_API_KEEP_ENV=1`。

## 環境変数

| 名前 | 既定 | 内容 |
|---|---|---|
| `VERIFY_TEST_API` | （なし） | `1` のときだけ有効 |
| `VERIFY_TEST_API_PROCESS_MATCH` | `dist/index\.js` | 有効にするプロセスの引数の正規表現 |
| `VERIFY_TEST_API_PORT` | `3999` | 管理用のポート |
| `VERIFY_TEST_API_HOST` | `0.0.0.0` | 待ち受けるアドレス |
| `VERIFY_TEST_API_CLOCK` | `1` | `0` にすると時計を偽装しない（`/__verify/clock` は 409） |
| `VERIFY_TEST_API_REAL_TIMERS` | engine.io、engine.io-client、socket.io、socket.io-client、socket.io-adapter、ws | 本物のタイマーを使う呼び出し元のファイルの正規表現 |
| `VERIFY_TEST_API_WORK_TYPES` | `prisma-client-request` | 完了待ちで「仕事の根」として追う非同期資源の型（カンマ区切り） |
| `VERIFY_TEST_API_ROOT_MAX_AGE_MS` | `30000` | これより古い仕事の根・要求は待たずに `stale_work` として報告する |
| `VERIFY_TEST_API_FAULTS` | （なし） | 異常の定義のモジュールのパス |
| `VERIFY_TEST_API_SEED` | （なし） | 起動の時点から乱数を固定する場合のシード |
| `VERIFY_TEST_API_KEEP_ENV` | （なし） | `1` なら環境変数を消さない |

## エンドポイント

すべて JSON。誤りは `{ "error": "..." }`（400、404、409、413、500）。

### `GET /__verify/capabilities`

```json
{"protocol_version":"0.1","role":"server","implementation":"custos-node-testapi","implementation_version":"0.1.0",
 "node":"v20.20.2","pid":42,
 "capabilities":{"settle":"1.0","seed":"1.0","reset":"1.0","clock":"1.0","faults":"1.0"},
 "faults":["card7-draws-two"],"faults_error":null}
```

`clock` は時計を偽装しているときだけ、`faults` は異常の定義を読んだときだけ載る。

### `POST /__verify/settle`

プロセスの中の処理が空になるまで待つ。本文（すべて任意）：

| 項目 | 既定 | 内容 |
|---|---|---|
| `quiet_ms` | 100 | コールバックが一度も走らない時間がこれだけ続いたら「空」とみなす |
| `timeout_ms` | 10000 | 上限。過ぎたら 504 と `settled: false` |
| `short_timer_ms` | 1000 | これ以下の1回きりのタイマー（アプリのもの）は、発火するまで待つ |
| `flush_timers_under_ms` | 0 | 0 より大きければ、これ以下の1回きりのタイマーを、時計を進めて今すぐ発火させる（例：halpal の切断後の掃除 2秒、終局後の掃除 10秒） |

待つもの：

1. 動き：テスト用API自身を除くコールバック（I/O、タイマー、Promise の続き）が `quiet_ms` の間走っていない。
2. 要求：ファイル、名前の解決、接続、書き込み、crypto の非同期のジョブが、コールバックまで終わっている（同期で終わったものは数えない）。
3. 仕事の根：`VERIFY_TEST_API_WORK_TYPES` の型の非同期資源（Prisma 6 は問い合わせごとに `prisma-client-request` を作る）の中で作られた Promise が、すべて解決している。Prisma 6.19.2 で、385ms かかる問い合わせの完了まで待つことを確かめた。
4. 短いタイマー：`short_timer_ms` 以下の1回きりのタイマーが発火している。

待たないもの：長いタイマー（halpal の手番の制限時間 65秒など）、利用者の入力を待っている Promise、待ち受け中のソケットのような残り続ける資源、通信の層の生存確認のタイマー。時計を止めている間の偽装したタイマーは、待っても進まないので `frozen_timers` に報告するだけにする。

応答（成功）：

```json
{"settled":true,"waited_ms":436,"quiet_for_ms":59,"flushed_timers":0,"frozen_timers":[],"stale_work":[]}
```

応答（時間切れ、504）：

```json
{"settled":false,"reason":"timeout","waited_ms":500,"quiet_for_ms":3,"last_activity_type":"Immediate",
 "pending":{"requests":[],"work":[],"timers":[]},"frozen_timers":[],"stale_work":[]}
```

### `GET /__verify/clock`、`POST /__verify/clock`

`@sinonjs/fake-timers`（15.5.0）で `Date` と `setTimeout`/`clearTimeout`/`setInterval`/`clearInterval`（`globalThis` と `timers` モジュールの両方）を偽装する。ふだんは 10ms ごとの刻みで本物の時間と同じ速さで進める（fake-timers の `shouldAdvanceTime` と同じ振る舞いを自前で行う。止められるようにするため）。

| `action` | 内容 |
|---|---|
| `freeze` | 時計を止める（`Date` も偽装したタイマーも進まない） |
| `resume` | 再開する（止めていた間の分は進めない） |
| `advance` | `ms` だけ進め、その間に期限の来たタイマーを発火させる（例：時間切れを今すぐ起こす。第25.8節） |
| `set` | `epoch_ms` に `Date` を合わせる（タイマーは発火させない） |

通信の層（engine.io、socket.io、ws）が直接呼ぶタイマーは偽装しない。時刻を一気に進めたときに、生存確認（ping）の待ち時間が切れて接続が切れるのを避けるため。どちらを使うかは、`setTimeout` を **直接** 呼んだファイルで決める（スタック全体で判定すると、socket.io から呼ばれたアプリのイベント処理の中のタイマーまで本物になってしまう）。

偽装したタイマーの中で投げられた例外は、本物のタイマーと同じく処理されない例外としてプロセスを落とす（クラッシュの検知を偽装で隠さない）。

応答：`{"installed":true,"frozen":false,"now":1795589704981,"iso":"…","installed_at":"…","pending_timers":0,"next_due_ms":null}`

### `GET /__verify/seed`、`POST /__verify/seed`

`{"seed": "abc"}` で、`crypto.randomInt`、`randomUUID`、`randomBytes`（`pseudoRandomBytes`）、`randomFill`、`randomFillSync`、`webcrypto.getRandomValues`（`globalThis.crypto` を含む）、`Math.random` を、シードつきの決まった乱数（sfc32、シードの文字列から cyrb128 で初期化）で置き換える。同じシードを入れ直すと、同じ列を最初から返す。`{"seed": null}` で元に戻す。

- `crypto.getRandomValues`（Node 20 では再定義できないゲッター）は置き換えないが、中で `webcrypto.getRandomValues` を呼ぶので固定される。
- 効くのは、呼び出しのたびに `crypto.xxx` を引く書き方（TypeScript の `import crypto from 'crypto'` のコンパイル結果など）。読み込みの時点で関数を取り出して持っている書き方には効かない。
- engine.io の接続の ID と socket.io の `socket.id` も固定される。この2つは base64id が `crypto.randomBytes` でまとめて作ってためておいた乱数から作られ、起動時にためた分は後からシードを入れても変わらない。そこでテスト用APIは、読み込まれた base64id（パッケージと、新しい engine.io が同梱した写しの両方）を包み、シードを入れている間はためた乱数を使わず、シードつきの `crypto.randomBytes` から ID を作る（`{"seed": null}` で元に戻る）。socket.id を利用者の ID に使うアプリでは、これがないと席の順番などが実行のたびに変わる。
  - シードで決まるのは ID の前の 16 文字（乱数の部分）。後ろ4文字は base64id のプロセスの中の通し番号で、シードを入れ直しても戻さない（戻すと、残っている古い接続と ID が重なりうる）。ID の並び順は前の 16 文字で決まる。

### `POST /__verify/reset`

異常の定義のモジュールの `reset()` を呼ぶ。応答：`{"ok":true,"hooks":1}`。アプリ内のキャッシュを捨てる処理が必要なアプリは、異常の定義のモジュールに `reset` を書く。

### `GET /__verify/faults`、`POST /__verify/faults`

異常の一覧と、有効化・無効化。`{"name":"x","enabled":true}`、`{"enable":["x"],"disable":["y"]}`、`{"disable_all":true}`。

異常の定義のモジュール（`VERIFY_TEST_API_FAULTS`）：

```js
module.exports = {
  faults: {
    'card7-draws-two': {
      description: '正夢（7）の効果で、次のドローが3枚ではなく2枚になる',
      patches: {
        // 読み込まれたモジュールのパスの末尾（または /正規表現/）→ export を差し替える関数
        'packages/server/dist/domain/state/applyChange.js': (exports, fault) => {
          const original = exports.applyChange;
          exports.applyChange = function (state, change) {
            if (fault.enabled() && change.type === 'SET_PLAYER_GET' && change.get === 3) change = { ...change, get: 2 };
            return original.call(this, state, change);
          };
        },
      },
    },
  },
  reset() {},
};
```

差し替えは require のフックで、対象のモジュールが読み込まれたときに一度だけ行う。有効かどうかは呼び出しのたびに `fault.enabled()` で見るので、実行中に切り替えられる。TypeScript のコンパイル結果のように、別のモジュールから `module_1.fn(...)` で呼ぶ関数に効く（同じモジュールの中の直接の呼び出しには効かない）。校正用のバグはパッチで仕込む（第19.4節）。ここは適合テストの陽性確認（D）のように、切り替えが要る場面に限って使う。

## テスト

```sh
adapters/node-testapi/run-tests.sh
```

`node:20-slim@sha256:2cf067cfed83d5ea958367df9f966191a942351a2df77d6f0193e162b5febfc0`（Node v20.20.2）のコンテナで、外部への出口のないネットワークのまま `node --test` を実行する。対象のプロセスは `test/fixtures/target-app.js`（小さな Socket.IO サーバー）。

## 依存

- 実行時：`@sinonjs/fake-timers` 15.5.0（と、その依存）。`package-lock.json` で固定。
- テストだけ：`socket.io` 4.8.1、`socket.io-client` 4.8.3。
