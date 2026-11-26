# テスト用ビルド（設計書 第14.6節、第14.9節、第27.2節の段階2）。
# 本番用ビルドのイメージ（アプリの Dockerfile をそのまま使ったもの）の上に、テスト用APIを重ねる。アプリのリポジトリは変えない。
# 文脈は custos が作る：testapi/（フレームワークの adapters/node-testapi）と app/（このディレクトリ）。
ARG APP_IMAGE
FROM ${APP_IMAGE}
USER root
COPY testapi/ /opt/verify-testapi/
RUN cd /opt/verify-testapi && npm ci --omit=dev --no-audit --no-fund --ignore-scripts \
 && chmod -R a+rX /opt/verify-testapi
COPY app/faults.js /opt/verify-faults/faults.js
# turbo（strict env mode）はタスクの環境変数を落とすので、有効にする設定はファイルで焼き込む（NODE_OPTIONS は通る）
COPY app/activation.json /opt/verify-testapi/activation.json
USER node
ENV NODE_OPTIONS="--require /opt/verify-testapi/index.js"
