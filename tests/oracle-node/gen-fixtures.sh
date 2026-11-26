#!/usr/bin/env bash
# 正解のデータを作り直す。node:20 のコンテナで公式の実装を動かす（ホストの Node の版に依存しない）。
# 使い方：tests/oracle-node/gen-fixtures.sh
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
out="$here/../Custos.Web.SocketIO.Tests/Fixtures/socketio-fixtures.json"
: "${DOCKER_HOST:=unix://$HOME/.colima/custos/docker.sock}"
export DOCKER_HOST
docker build -q -t custos-oracle-node:local "$here" >/dev/null
trap 'rm -f "$out.tmp"' EXIT
docker run --rm --network none custos-oracle-node:local node gen-fixtures.js > "$out.tmp"
mv "$out.tmp" "$out"
echo "wrote $out"
