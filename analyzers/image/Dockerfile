# 画像の解析の外部コンポーネント（設計書 第12.8節）。管理側だけで、ネットワークなし（--network none）で動かす。CPU で動く。
ARG BASE
FROM ${BASE}
WORKDIR /opt/analyzer
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY analyzer.py .
RUN useradd --create-home --uid 10001 analyzer
USER analyzer
ENTRYPOINT ["python", "/opt/analyzer/analyzer.py"]
