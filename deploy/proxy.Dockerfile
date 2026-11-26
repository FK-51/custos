# 記録用のプロキシ（custos-proxy）。tc（netem）のために iproute2 を入れる。
ARG BASE
FROM ${BASE}
RUN apt-get update && apt-get install -y --no-install-recommends iproute2 && rm -rf /var/lib/apt/lists/*
COPY publish/ /custos/
ENTRYPOINT ["/custos/custos-proxy"]
