FROM python:3.12-slim

RUN apt-get update \
    && apt-get install -y --no-install-recommends libgl1 libglib2.0-0 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# CPU-only torch first, so docling doesn't pull the multi-gigabyte CUDA build.
RUN pip install --no-cache-dir --index-url https://download.pytorch.org/whl/cpu torch torchvision
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Bake the layout and table models into the image instead of downloading them at runtime.
ENV DOCLING_ARTIFACTS_PATH=/opt/docling-models
RUN docling-tools models download --output-dir "$DOCLING_ARTIFACTS_PATH"

# Passage sizes are counted in the embedding model's tokens; keep this in step with the model service's EMBEDDING_MODEL.
ARG CHUNK_TOKENIZER_MODEL=BAAI/bge-small-en-v1.5
ENV CHUNK_TOKENIZER=/opt/tokenizer/tokenizer.json
RUN mkdir -p /opt/tokenizer \
    && python -c "import sys; from tokenizers import Tokenizer; Tokenizer.from_pretrained(sys.argv[1]).save(sys.argv[2])" \
       "$CHUNK_TOKENIZER_MODEL" "$CHUNK_TOKENIZER"

RUN useradd --create-home --uid 10001 worker
USER worker

COPY worker.py .

CMD ["python", "-u", "worker.py"]
