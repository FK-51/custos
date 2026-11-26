#!/usr/bin/env bash
# 公式のサーバーとクライアントを、外部への出口のないネットワークの中で直接つなぎ、
# 3通りのトランスポート（polling、websocket、upgrade）で決まったイベントが届くことを確かめる。
# 使い方：tests/oracle-node/live-check.sh
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
: "${DOCKER_HOST:=unix://$HOME/.colima/custos/docker.sock}"
export DOCKER_HOST
img=custos-oracle-node:local
net=custos-oracle-check
docker build -q -t "$img" "$here" >/dev/null
docker network rm "$net" >/dev/null 2>&1 || true
docker network create --internal "$net" >/dev/null
cleanup() { docker rm -f oracle-server >/dev/null 2>&1 || true; docker network rm "$net" >/dev/null 2>&1 || true; }
trap cleanup EXIT
docker run -d --name oracle-server --network "$net" "$img" node live-server.js --port 3000 >/dev/null
for i in $(seq 1 50); do
  if docker logs oracle-server 2>/dev/null | grep -q '"kind":"listening"'; then break; fi
  sleep 0.2
done
mkdir -p "$here/out"
status=0
for t in polling websocket upgrade; do
  if docker run --rm --network "$net" "$img" node live-client.js --url http://oracle-server:3000 --transport "$t" > "$here/out/client-$t.jsonl"; then
    echo "client $t: ok ($(grep -c '"kind":"sent"' "$here/out/client-$t.jsonl") sent, $(grep -c '"kind":"received"' "$here/out/client-$t.jsonl") received, $(grep -c '"kind":"ack"' "$here/out/client-$t.jsonl") acks)"
  else
    echo "client $t: FAILED"; cat "$here/out/client-$t.jsonl"; status=1
  fi
done
docker logs oracle-server > "$here/out/server.jsonl" 2>&1
echo "server: $(grep -c '"kind":"received"' "$here/out/server.jsonl") received, transports: $(grep -o '"kind":"upgrade"[^}]*' "$here/out/server.jsonl" | wc -l | tr -d ' ') upgrades"
exit $status
