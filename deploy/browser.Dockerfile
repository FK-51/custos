# ブラウザ（custos-browser）。Playwright の公式イメージ（ブラウザと依存を含む）の上に置く。版は Microsoft.Playwright と同じ 1.65.1。
ARG BASE
FROM ${BASE}
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
COPY publish/ /custos/
ENTRYPOINT ["/custos/custos-browser"]
