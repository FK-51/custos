# Custos の使い方（最初の対象アプリ halpal の例）

## 前提

- .NET 10 SDK（`~/.dotnet`）、Docker（colima のプロファイル `custos`）、git、node（アダプタのテストだけ）
- 3つのリポジトリとデータの置き場所は、互いの外に置く（指示書 第3.1節、制約6）

| 場所 | 例 |
|---|---|
| フレームワーク | `~/i/hobby/custos` |
| 検証用リポジトリ | `~/i/hobby/custos_test/halpal-custos` |
| 対象アプリ（作業用の複製） | `~/i/hobby/custos_test/halpal-work` |
| データ（結果、記録、スナップショット、登録簿、秘密の値） | `~/i/hobby/custos_test/var` |

`custos_test/bin/custos` が環境変数（CUSTOS_VERIFY_REPO、CUSTOS_DATA_DIR、CUSTOS_APP_REPO、CUSTOS_FRAMEWORK_REPO、CUSTOS_READONLY_REPOS、DOCKER_HOST）を設定して CLI を呼ぶ。
CLI は `dotnet build src/Custos.Cli --artifacts-path <フレームワーク>/artifacts` で作る。

## 段階ごとの流れ

```sh
export CUSTOS_APP_REPO=~/i/hobby/custos_test/halpal-work
cd ~/i/hobby/custos_test

# 段階0：土台
./bin/custos config check
./bin/custos up                         # イメージを作り、閉じたネットワークで起動し、初期のスナップショットを取る
./bin/custos conformance --stage 0      # A、B、C、E の必須項目（成熟度レベル1）
./bin/custos conformance --all-breakage # わざと壊す確認

# 段階1：機械判定（AIを使わない）
./bin/custos stage1 --games 3           # ボットの対局、局面の検査、宣言と実態のずれ、隠し情報となりすまし、ファジング、静的解析
./bin/custos bug export                 # 登録簿を検証用リポジトリの registry/ に書き出す

# 段階2：再現性（テスト用ビルド）
./bin/custos up --build test
./bin/custos conformance --item A-1,B-3,B-4,B-5,D-1,D-2,D-3,D-4
./bin/custos replay && ./bin/custos determinism --runs 3
./bin/custos probe time && ./bin/custos probe disruption

# 段階3：探索と修正のループ（API キーがなければ --agent scripted）
./bin/custos ledger build
./bin/custos loop --iterations 1 --agent scripted
./bin/custos hidden run

# 段階4：校正と統計
./bin/custos seeds check && ./bin/custos calibrate
./bin/custos canary && ./bin/custos sample && ./bin/custos maturity && ./bin/custos release

# 段階5：性能
./bin/custos up --profile mini-1
./bin/custos perf run --runs 3 && ./bin/custos perf capacity

# 段階6：広げる
./bin/custos selftest                   # 参照アプリ（既知のバグ入り）でフレームワーク自体の回帰テスト
./bin/custos template init <dir>        # 2つ目のアプリのひな形
./bin/custos reference check            # 参照モデルとの食い違い（参照モデルは参照モデル役が書く）
```

## AIエージェント（Claude Code）

- 起動は `claude -p --bare --output-format json --json-schema … --max-budget-usd …` を、サンドボックスのコンテナの中で行う。
- `--bare` は API キー以外の認証を読まないので、`ANTHROPIC_API_KEY` を CLI を動かす環境に設定する（ファイルには書かない。キーは起動したプロセスにだけ渡す）。
- モデルの API への通信は、`custos up --egress` で起動する出口のプロキシ（許可した宛先だけ）を通る。

## 結果

- `var/results/runs/<番号>-<種類>/`：判定の結果と証拠。`manifest.json` と `ledger.jsonl` のハッシュの連鎖で改ざんを検査する（`custos results verify`）。
- `var/registry/bugs.db`：バグの登録簿（追記だけ）。`custos bug export` で検証用リポジトリの `registry/bugs.md` と `bugs.jsonl` に書き出す。
