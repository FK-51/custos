# ひな形：Socket.IO のターン制ゲーム（設計書 第24.6節、第25章）

2つ目のアプリを安く載せるためのひな形。最初の対象アプリ（halpal）の検証用リポジトリから、アプリに依存しない部分を取り出した。
`custos template init <検証用リポジトリのディレクトリ>` で写し、`TODO` の箇所をアプリに合わせて書き換える。

| ファイル | 書き換えるところ |
|---|---|
| config/app.yaml | name、criticality、required_maturity |
| config/environment.yaml | 公開ホスト名とポート、Dockerfile、DB のファイル、Socket.IO のパス |
| config/state.yaml | SQL のキー（表と列）、送信された状態のキー（イベント名と値の位置） |
| config/authority.yaml | 状態ごとの権威と可視範囲、イベントごとの受け取ってよい値、局面ごとの許可 |
| config/invariants.yaml | 違反している行を返す SQL（sql / sent_sql / cross_sql） |
| scenarios/bot.yaml | 選択の要求のイベント、選択肢の位置、返答のイベントとひな形 |
| scenarios/two-players-start.yaml | 登録、部屋の作成、参加、開始のイベント |
| scenarios/phases/*.yaml | 局面ごとの準備と、送り手の種類（actors） |
| scenarios/fuzz/templates.yaml | イベントごとの値の見本 |
| environment/testbuild/ | テスト用APIの組み込み（activation.json の起動するプロセスの名前） |
| ledger/definition.yaml | 台帳の対象（カード、イベント、画面）と状況の型の条件 |

検証設定はAIが下書きし、人間が承認する（第18.1節）。承認するまで、局面の検査や不変条件の結果は「宣言に対して」の結果である。
