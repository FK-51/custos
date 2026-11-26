#!/usr/bin/env bash
# テスト用API の単体テストを node:20 のコンテナで実行する（外部への出口のないネットワーク）。
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
: "${DOCKER_HOST:=unix://$HOME/.colima/custos/docker.sock}"
export DOCKER_HOST
docker build -q -f "$here/Dockerfile.test" -t custos-node-testapi-test:local "$here" >/dev/null
docker run --rm --network none custos-node-testapi-test:local
