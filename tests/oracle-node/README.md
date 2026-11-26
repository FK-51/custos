# oracle-node（Socket.IO の読み解きの正解作り。テスト専用）

指示書 タスク5「Socket.IO と、その下の Engine.IO の読み解きは、C# で書く。公式の JavaScript の実装を正解として使い、テストする」のための Node.js のコード。
ここで作った正解のデータと、公式のサーバー・クライアントを使って、C# 側（`Custos.SocketIO.Wire`、記録用のプロキシ）の読み解きを照らし合わせる。

## 版

対象アプリ（halpal）の `package-lock.json` と同じ版に固定する（`package.json` と `package-lock.json`）。

| パッケージ | 版 |
|---|---|
| socket.io | 4.8.1 |
| socket.io-client | 4.8.3 |
| socket.io-parser | 4.2.7 |
| socket.io-adapter | 2.5.8 |
| engine.io | 6.6.9 |
| engine.io-client | 6.6.6 |
| engine.io-parser | 5.2.3（Engine.IO プロトコル 4） |
| ws | 8.21.3 |

Node.js は対象アプリ（`node:20-slim`）と同じ 20 で動かす。ホストの Node の版に依存しないよう、コンテナで実行する。

- イメージ：`node:20-slim@sha256:2cf067cfed83d5ea958367df9f966191a942351a2df77d6f0193e162b5febfc0`（Node v20.20.2、arm64 と amd64 を含む）

## 正解のデータを作り直す

```sh
tests/oracle-node/gen-fixtures.sh
```

`tests/Custos.Web.SocketIO.Tests/Fixtures/socketio-fixtures.json` を書く。外部への出口のないコンテナで、公式の `socket.io-parser` と `engine.io-parser` を動かす。

| 部分 | 件数 | 内容 |
|---|---|---|
| `socketio_packets` | 39 | 正しいパケット。`encoded`（Engine.IO の message の中身の列。文字列は `{"text":…}`、バイナリの添付は `{"binary_b64":…}`）と、公式の Decoder で読み直した `decoded`（`type`、`nsp`、`id`、`data`、`attachments`） |
| `socketio_edge_cases` | 19 | 境界の文字列。公式の Decoder が誤りにするか（`error`、`message`）、どう読むか（`decoded`）、添付を待つか（`waiting_for_attachments`）をそのまま記録する |
| `engineio_packets` | 15 | Engine.IO のパケット。`encoded_text`（文字列、ポーリングのバイナリは `b` ＋ base64）か `encoded_binary_b64`（WebSocket のバイナリ）と、`decoded`（`type`、`data`、`binary`） |
| `polling_payloads` | 9 | ポーリングのペイロード（区切りは `\u001e`）と、読み直した各パケット |

表し方：

- Socket.IO の `type` は `connect`、`disconnect`、`event`、`ack`、`connect_error`、`binary_event`、`binary_ack`。
- Engine.IO の `type` は `open`、`close`、`ping`、`pong`、`message`、`upgrade`、`noop`。
- 復元した値の中のバイナリは `{"_binary_b64": "..."}`。値がない（undefined）場合は `null`。

公式の Decoder の、気づきにくい振る舞いの例（C# 側も合わせる）：

- `"2"`（EVENT で中身なし）は誤りにならず、`data` なしの EVENT になる。
- `"242"` は、ID が 42 で中身なしの EVENT になる。
- `"2 [\"a\"]"` は、空白を数字とみなして ID が 0 になる（`Number(' ') == ' '` が真のため）。
- `"2[1,2]"` のように、イベント名が数のものも受け付ける。予約されたイベント名（`"2[\"connect\"]"` など）は誤り。
- `"1{}"`（DISCONNECT に中身がある）は誤り。`"4\"x\""`（CONNECT_ERROR に文字列）は受け付ける。

## 公式のサーバーとクライアントで確かめる

```sh
tests/oracle-node/live-check.sh
```

外部への出口のないネットワークに、公式のサーバー（`live-server.js`）と、クライアント（`live-client.js`）を3通りのトランスポートでつなぐ。結果は `out/`（コミットしない）。

- `live-server.js [--port 3000] [--host 0.0.0.0] [--max-http-buffer-size 32768]`
  - 受けたイベントを JSON Lines で標準出力に書く（`kind`：`listening`、`connection`、`upgrade`、`received`、`sent`、`acked`、`disconnect`）。
  - 接続した時点で `welcome` を送る（`upgrade` の場合、切り替えの前なのでポーリングで届く）。`echo` は同じ値を返す。`request-binary` にはバイナリを含む `binary` を送る。確認応答付きのイベントには `{ok, event, count}` を返し、受けた値にバイナリがあればバイナリも付ける（BINARY_ACK）。`bye` で切断する。名前空間 `/` と `/admin`。
- `live-client.js --url <URL> --transport polling|websocket|upgrade [--path /socket.io/] [--namespace /] [--timeout 10000] [--header 名前=値]`
  - `polling`：ポーリングだけ。`websocket`：WebSocket だけ。`upgrade`：ポーリングで始めて切り替える（Socket.IO の既定）。
  - 決まった一覧（`echo` ×2、確認応答付き `ack-test`、バイナリの `binary-test`、バイナリの確認応答 `binary-ack`、`request-binary`、配列の `array`、`bye`）を送り、送ったもの（`sent`）、受け取ったもの（`received`）、確認応答（`ack`）を、そのときのトランスポートと一緒に JSON Lines で書く。最後に `done`。失敗したら `error` を書いて終了コード 1。
  - 記録用のプロキシの前に置けば、プロキシの記録とこの出力を照らし合わせられる（適合テスト C-4 の3通り）。

確認の結果（2026-11-25）：3通りとも成功。`upgrade` では、Engine.IO の握手、Socket.IO の CONNECT、サーバーからの `welcome` がポーリングで運ばれ、その後の送受信は WebSocket で運ばれた。
