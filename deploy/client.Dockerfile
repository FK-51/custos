# Socket.IO のスクリプトクライアント（custos-client）。利用者側のネットワークにだけつなぐ。判定器と設定は入れない。
ARG BASE
FROM ${BASE}
COPY publish/ /custos/
ENTRYPOINT ["/custos/custos-client"]
