# Custos の実装構成

設計書（docs/design.md）を実装するときの、部品の分け方と約束事をまとめる。設計書と食い違う場合は設計書を優先する。

## 1. 場所

| 場所 | 例 | 役割 |
|---|---|---|
| 対象アプリのリポジトリ | `~/i/shigenoki/halpal` | 読むだけ。ビルドの文脈としてだけ使う |
| フレームワークのリポジトリ | `~/i/hobby/custos` | このリポジトリ |
| 検証用リポジトリ | `~/i/hobby/custos_test/halpal-custos` | `config/`（設定）、`environment/`（コンテナの定義）、`docs/`、`registry/`（登録簿の書き出し）、`seeds/`（校正用バグ）、`hidden-scenarios/`、`ledger/`、`keys/`（許可する鍵の公開鍵）、`rules/`、`reference-model/` |
| データの置き場所 | `~/i/hobby/custos_test/var` | `results/`、`snapshots/`、`records/`、`control/`、`registry/bugs.db`、`secrets/`、`build/`、`compose/`、`sandbox/`、`docker-config/` |
| AIの作業用の複製 | `~/i/hobby/custos_test/halpal-work` | 修正AIが書き込むアプリの複製。コミットは `custos commit` を通す |

`Workspace`（src/Custos.Core/Util/Workspace.cs）がこれらを持ち、互いの外にあることを検査する。

## 2. 実行時の構成（ローカル検証）

Docker（colima のプロファイル `custos`、`DOCKER_HOST=unix://$HOME/.colima/custos/docker.sock`、`DOCKER_CONFIG=<data>/docker-config`）。
compose のファイルは `environment.yaml` から生成し、`<data>/compose/` に置く。

| コンテナ | イメージ | ネットワーク | 役割 |
|---|---|---|---|
| `app` | 対象アプリの Dockerfile（変更しない） | appnet | 対象アプリ。DBの入ったディレクトリは名前付きボリューム `appdata` |
| `edge` | custos-proxy | usernet（公開ホスト名の別名を持つ）、appnet | 記録用のプロキシ。利用者側の通信を中継・記録する |
| `ops` | custos-ops | appnet | 管理側の操作。`appdata` と `<data>/snapshots` をマウントする |
| `client-N` / `attacker-N` | custos-client | usernet | Socket.IO のスクリプトクライアント |
| `browser` | custos-browser（Playwright の公式イメージ） | usernet | ブラウザ |
| `sandbox` | custos-sandbox（node:20-slim + git） | usernet | AI用サンドボックスの枠。対象アプリの複製だけを見せる |
| `egress`（段階3） | custos-egress | usernet、egressnet | モデルのAPIへの出口（許可した宛先だけ） |

- usernet と appnet は `internal: true`（外部への出口なし。名前の解決も外へ出ない）。
- 管理側（ホストの `custos`）から各コンテナへの操作は `docker exec -i`（標準入出力の JSON Lines）で行う。ネットワーク上の管理用の待ち受けは作らない。利用者側から管理側に届く経路がない。
- 記録は `edge` が `<data>/records/<セッション>/` に書く。管理側（ホスト）だけが読む。
- アプリのコンテナの環境変数とコマンドは本番（staging.compose.yml）と同じにする。

## 3. プロジェクト

| プロジェクト | 中身 | 依存 |
|---|---|---|
| Custos.Analyzers | 禁止構文の検出（CUS001〜005） | - |
| Custos.Core | Util、Config、Results、Conformance、Maturity、Scenarios、Judging（Finding、TokenAnalysis） | YamlDotNet、Microsoft.Data.Sqlite |
| Custos.Statistics | 統計（上限、信頼区間、Mills、捕獲再捕獲、Goel-Okumoto、順位検定、ブートストラップ）、抜き取り検証の計算、リリース判定、性能の妥当性と換算 | Custos.Core |
| Custos.Registry | バグの登録簿（SQLite）、コミットの入口（分類、署名、チェックポイント、取り込み口、監査） | Custos.Core |
| Custos.Protocol | ゲームエンジン向けプロトコルのメッセージの型（netstandard2.1） | - |
| Custos.SocketIO.Wire | HTTP/1.1、WebSocket、Engine.IO v4、Socket.IO v5 の読み解き | - |
| Custos.Web.SocketIO | 管理側のアダプタ：Lab（compose の生成、起動、リセット、完了待ち、テスト用APIの呼び出し）、Records、State、Scenarios、Judges、Conformance（A〜F）、Probes（ファジング、局面の検査、隠し情報となりすまし、ボットの対局、静的解析）、Repro（再生、制限時間、切断・再起動、参照モデル）、Agents（台帳、レポートと機械チェック、探索役、修正役、ループ、隠しシナリオ、Claude Code の起動）、Calibration（校正用バグ、品質チェック、校正、カナリア）、Performance、Determinism、Analysis | Core、Wire、Statistics、Registry |
| Custos.Agent.Common | コンテナ内の部品の共通部分（JSON Lines の RPC、到達性の検査） | - |
| Custos.Agent.Proxy | 記録用のプロキシ（custos-proxy） | Common、Wire |
| Custos.Agent.Ops | DBの読み取り・スナップショット・差し替え（custos-ops） | Common、Microsoft.Data.Sqlite |
| Custos.Agent.Client | スクリプトクライアント、相手役のボット、生のクライアント（custos-client） | Common、Wire、SocketIOClient |
| Custos.Agent.Browser | ブラウザ（custos-browser） | Common、Microsoft.Playwright |
| Custos.Cli | コマンド `custos` | すべての管理側の部品 |
| adapters/node-testapi | Node 向けのテスト用API（JavaScript。テスト用ビルドに preload で組み込む。環境変数か activation.json で有効にする） | @sinonjs/fake-timers |
| adapters/reference-model | 参照モデルの実行器（runner.js。管理側のネットワークなしのコンテナで動かす） | - |
| analyzers/image | 画像の解析の外部コンポーネント（Python。差分、崩れの検出。selfcheck で陽性と陰性を確かめる） | pillow、numpy（版を固定） |
| reference-app | 参照アプリ（既知のバグ6件入り）と、その検証用リポジトリ（verify/）。`custos selftest` で回帰テストに使う | socket.io、node:sqlite |
| templates/web-socketio | 2つ目のアプリのひな形（`custos template init`） | - |
| calibration/shared-corrections.json | プロジェクトをまたいで共有する、層ごとの校正の集計値 | - |
| tests/oracle-node | Socket.IO の読み解きの正解を作る Node.js のコード（テスト専用） | socket.io（アプリと同じ版） |

## 4. 約束事

- C# は Directory.Build.props の設定に従う（null許容参照型、警告はエラー、latest-recommended、自前アナライザー）。`dynamic`、`!`、`#pragma warning disable`、`#nullable disable`、抑止の属性は使わない。
- 文字列の比較は `StringComparison.Ordinal`、数値の書式は `CultureInfo.InvariantCulture`。
- JSON のキーは snake_case（`Custos.Core.Util.Json`）。
- 結果は `RunStore` の `Run` に書く（上書き不可、封をすると連鎖に足す）。
- テストは xunit.v3（Microsoft.Testing.Platform）。テストのメソッド名に `_` を使わない（CA1707）。

## 5. データの形

### 5.1 通信の記録（`<data>/records/<session>/events.jsonl`）

1行に1件。`kind` は `http`（HTTP の要求と応答の組）、`eio`（Engine.IO のパケット）、`ws`（WebSocket のフレーム）、`conn`（接続の開始と終了）。

```json
{"seq":12,"ts":"2026-11-25T06:12:01.123456Z","kind":"eio","conn":3,"client":"172.20.0.5","sid":"Ab12","transport":"websocket",
 "dir":"c2s","eio":"message","sio":"event","nsp":"/","event":"playerChoice","args":[{"roomId":"room-x","choice":3}],"ack":null,
 "attachments":0,"raw":"42[\"playerChoice\",{...}]"}
```

生の通信は `raw/<conn>-c2s.bin`、`raw/<conn>-s2c.bin` に残す（Socket.IO のパスの通信は全部、それ以外は要求と応答の頭だけ）。

### 5.2 コンテナ内の部品との RPC（JSON Lines、標準入出力）

要求：`{"id":1,"method":"user.emit","params":{...}}`。応答：`{"id":1,"result":{...}}` か `{"id":1,"error":{"message":"..."}}`。

### 5.3 シナリオ（`Custos.Core.Scenarios`）

操作の列。探索AIのレポートの「試した操作」、確認用シナリオ、隠しシナリオ、適合テストで共用し、決定的に再生できる形にする。

## 6. 段階との対応

| 段階 | 主な部品 |
|---|---|
| 0 | Config、Results、Conformance（A、B、C、E）、Wire、Proxy、Ops、Client、Browser、Web.SocketIO の起動・リセット・記録・状態・完了待ち、Registry（記録のみ） |
| 1 | 汎用の判定、ファジング、不変条件（SQL、送信された状態の SQL、写しの突き合わせ）、状態遷移と選択肢、隠し情報と再接続トークン、相手役のボット、適合テスト D |
| 2 | node-testapi、テスト用ビルド、再生、正直なクライアント同士の比較、制限時間、切断・再接続・再起動の注入、決定性の等級 |
| 3 | Agents（役割ごとの起動、`--bare`）、Ledger、Reports、攻撃AI、修正ループ、隠しシナリオ、署名と取り込み口 |
| 4 | Calibration（シード作成、校正用ビルド、層、カナリア）、Sampling、検証役の感度、リリース判定 |
| 5 | 負荷のボット、計測と妥当性、リソースプロファイル、換算係数 |
| 6 | 参照モデルの枠、ひな形、共有する補正値、参照アプリ、解析の外部コンポーネント |

## 7. コマンドの一覧

`custos --help` で表示する。段階ごとの主なコマンド：

| 段階 | コマンド |
|---|---|
| 0 | config check、up、down、status、snapshot save/list、reset、settle、state get/dump、record show、logs、call、scenario run、conformance、bug add/export、commit、results list/verify |
| 1 | judge、fuzz、probe phases、probe secrets、bot play、stage1 |
| 2 | replay、determinism、probe time、probe disruption |
| 3 | ledger build、explore、loop、hidden run、keys init、intake check |
| 4 | seeds check、calibrate、canary、sample、maturity、release |
| 5 | perf run、perf compare、perf capacity |
| 6 | template init、reference check、selftest |

