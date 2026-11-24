"""Model Service: self-hosted embeddings and reranking.

Runs small open models through ONNX Runtime on CPU (via fastembed); no paid APIs and
no PyTorch. Both ingestion (passage embeddings) and retrieval (query embeddings,
reranking) call this one service, so documents and questions always go through the
same model version.

Endpoints:
  GET  /health   -> model names and embedding dimensions
  POST /embed    -> {"texts": [...], "kind": "passage" | "query"}
  POST /rerank   -> {"query": "...", "documents": [...]}
"""

from __future__ import annotations

import logging
import os
from typing import Literal

from fastapi import FastAPI
from fastembed import TextEmbedding
from fastembed.rerank.cross_encoder import TextCrossEncoder
from pydantic import BaseModel, Field

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
logger = logging.getLogger("model-service")

EMBEDDING_MODEL = os.environ.get("EMBEDDING_MODEL", "BAAI/bge-small-en-v1.5")
RERANK_MODEL = os.environ.get("RERANK_MODEL", "Xenova/ms-marco-MiniLM-L-6-v2")
CACHE_DIR = os.environ.get("MODEL_CACHE_DIR", "/models")
THREADS = int(os.environ.get("MODEL_THREADS", "0")) or None
MAX_BATCH = 256
MAX_TEXT_CHARS = 8000
# A batch is padded to its longest text, and ONNX Runtime keeps its peak allocation, so one large
# batch of long passages (e.g. ~60 rerank candidates) exhausted the container's memory.
INFERENCE_BATCH_SIZE = int(os.environ.get("INFERENCE_BATCH_SIZE", "8"))

# Instruction text some embedding models expect in front of a search query (fastembed doesn't add
# it). For BGE it measurably improves short-question -> passage matching.
QUERY_PREFIXES = {"baai/bge": "Represent this sentence for searching relevant passages: "}
QUERY_PREFIX = os.environ.get(
    "QUERY_PREFIX",
    next((prefix for family, prefix in QUERY_PREFIXES.items() if EMBEDDING_MODEL.lower().startswith(family)), ""),
)

logger.info("loading %s and %s", EMBEDDING_MODEL, RERANK_MODEL)
embedder = TextEmbedding(EMBEDDING_MODEL, cache_dir=CACHE_DIR, threads=THREADS)
reranker = TextCrossEncoder(RERANK_MODEL, cache_dir=CACHE_DIR, threads=THREADS)
DIMENSIONS = len(next(iter(embedder.passage_embed(["probe"]))))
logger.info("ready: %d-dimensional embeddings", DIMENSIONS)

app = FastAPI(title="ICE Model Service")


def by_length(texts: list[str], infer) -> list:
    """Runs infer on texts sorted by length, so each small batch pads little, and returns results in the original order."""
    order = sorted(range(len(texts)), key=lambda i: len(texts[i]))
    results: list = [None] * len(texts)
    for i, result in zip(order, infer([texts[i] for i in order])):
        results[i] = result
    return results


class EmbedRequest(BaseModel):
    texts: list[str] = Field(min_length=1, max_length=MAX_BATCH)
    kind: Literal["passage", "query"] = "passage"


class RerankRequest(BaseModel):
    query: str = Field(min_length=1, max_length=MAX_TEXT_CHARS)
    documents: list[str] = Field(min_length=1, max_length=MAX_BATCH)


@app.get("/health")
def health() -> dict:
    return {"embeddingModel": EMBEDDING_MODEL, "dimensions": DIMENSIONS, "rerankModel": RERANK_MODEL, "queryPrefix": QUERY_PREFIX}


@app.post("/embed")
def embed(request: EmbedRequest) -> dict:
    if request.kind == "query":
        vectors = embedder.query_embed([QUERY_PREFIX + text[:MAX_TEXT_CHARS] for text in request.texts])
    else:
        vectors = by_length(
            [text[:MAX_TEXT_CHARS] for text in request.texts],
            lambda texts: embedder.passage_embed(texts, batch_size=INFERENCE_BATCH_SIZE))
    return {
        "model": EMBEDDING_MODEL,
        "dimensions": DIMENSIONS,
        "vectors": [[round(float(x), 6) for x in vector] for vector in vectors],
    }


@app.post("/rerank")
def rerank(request: RerankRequest) -> dict:
    scores = by_length(
        [document[:MAX_TEXT_CHARS] for document in request.documents],
        lambda documents: reranker.rerank(request.query, documents, batch_size=INFERENCE_BATCH_SIZE))
    return {"model": RERANK_MODEL, "scores": [float(score) for score in scores]}
