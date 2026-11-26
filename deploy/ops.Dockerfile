# 管理側の操作（custos-ops）。アプリ側のネットワークにだけつなぐ。
ARG BASE
FROM ${BASE}
COPY publish/ /custos/
ENTRYPOINT ["/custos/custos-ops"]
