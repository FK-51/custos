# Socket.IO の読み解きの正解作り（テスト専用）。Node.js は対象アプリと同じ 20 にする。
# イメージはダイジェストで固定する（指示書 制約10）。
FROM node:20-slim@sha256:2cf067cfed83d5ea958367df9f966191a942351a2df77d6f0193e162b5febfc0
WORKDIR /oracle
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund --ignore-scripts
COPY lib ./lib
COPY *.js ./
USER node
CMD ["node", "gen-fixtures.js"]
