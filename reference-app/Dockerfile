# 参照アプリのイメージ。土台は node:22-slim をダイジェストで固定（node:sqlite を使う）
FROM node@sha256:be979e5523c76fa4d5ca4ebd3099078f220aa47ec39c48ad5b5df9314debd6eb
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --omit=dev --no-audit --no-fund
COPY server.js ./
COPY public ./public
RUN mkdir -p /app/data && chown -R node:node /app/data
USER node
ENV NODE_NO_WARNINGS=1 PORT=3000 DB_PATH=/app/data/ref.db
EXPOSE 3000
CMD ["node", "server.js"]
