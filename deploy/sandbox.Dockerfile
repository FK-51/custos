# AI用サンドボックス（設計書 第12.6節）。対象アプリの複製だけを /workspace に見せる。
# 判定器、検証設定、通信の記録、結果、DBは入れない。
# 役割ごとのエージェント（Claude Code）は非対話モードかつ --bare で、フレームワークが docker exec で起動する。
# 探索・攻撃役がアプリと話すための Socket.IO のクライアント（アプリと同じ版）を /opt/tools に置く。
# 版はすべて固定する（指示書 制約10）。外部への通信はイメージを作るときだけで、実行時は出口のプロキシ（許可した宛先だけ）を通る。
ARG BASE
FROM ${BASE}
ARG CLAUDE_CODE_VERSION=2.1.331
ARG SOCKET_IO_CLIENT_VERSION=4.8.3
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates procps && rm -rf /var/lib/apt/lists/*
RUN npm install -g --no-audit --no-fund "@anthropic-ai/claude-code@${CLAUDE_CODE_VERSION}" \
 && mkdir -p /opt/tools && cd /opt/tools && npm init -y >/dev/null \
 && npm install --no-audit --no-fund --save-exact "socket.io-client@${SOCKET_IO_CLIENT_VERSION}" \
 && chmod -R a+rX /opt/tools
ENV NODE_PATH=/opt/tools/node_modules
USER node
WORKDIR /workspace
