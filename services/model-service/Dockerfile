FROM python:3.12-slim

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Bake the models into the image. Changing either model means rebuilding the image
# and re-embedding stored passages (the API does that automatically on re-ingest).
ENV MODEL_CACHE_DIR=/models \
    EMBEDDING_MODEL=BAAI/bge-small-en-v1.5 \
    RERANK_MODEL=Xenova/ms-marco-MiniLM-L-6-v2
RUN python -c "import os; from fastembed import TextEmbedding; from fastembed.rerank.cross_encoder import TextCrossEncoder; TextEmbedding(os.environ['EMBEDDING_MODEL'], cache_dir='/models'); TextCrossEncoder(os.environ['RERANK_MODEL'], cache_dir='/models')"

RUN useradd --create-home --uid 10001 models && chmod -R a+rX /models
USER models

COPY app.py .

EXPOSE 8000
CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8000"]
