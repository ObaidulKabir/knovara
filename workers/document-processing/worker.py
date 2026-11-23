"""Document Processing Worker (Phase 1 — Intelligent document ingestion).

Scaffold only. Consumes ingestion jobs from RabbitMQ, runs format-aware
structural extraction with Docling (PDF/DOCX/PPTX/EPUB/HTML), and posts the
resulting knowledge nodes back to the ICE.Api `/api/resources/ingest` pipeline
so they are built into the hierarchy and indexed.

See system_architecture.md "Format-Aware Processing" and
system_specifications.md "Phase 1 - Intelligent document ingestion".
"""

from __future__ import annotations

import json
import logging
import os
from dataclasses import dataclass

import httpx
import pika

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("document-processing-worker")

RABBITMQ_URL = os.environ.get("RABBITMQ_URL", "amqp://guest:guest@localhost:5672/")
API_BASE_URL = os.environ.get("ICE_API_BASE_URL", "http://localhost:5000")
QUEUE_NAME = os.environ.get("INGESTION_QUEUE", "ingestion.requests")


@dataclass
class IngestionJob:
    resource_id: str
    uri: str
    source_type: str
    tenant_id: str


def extract_structure(job: IngestionJob) -> list[dict]:
    """Runs Docling structural extraction for the job's source.

    Placeholder: returns a single document-level node. Replace with a real
    Docling DocumentConverter call that walks the resulting structure
    (chapters/sections/paragraphs/tables) into KnowledgeNode-shaped dicts.
    """
    logger.info("Extracting structure for %s (%s)", job.uri, job.source_type)
    return [
        {
            "nodeId": job.resource_id,
            "resourceId": job.resource_id,
            "parentNodeId": None,
            "level": "document",
            "title": job.uri,
            "summary": None,
            "originalContent": None,
            "metadata": {"sourceType": job.source_type},
        }
    ]


def handle_message(channel, method, properties, body: bytes) -> None:
    try:
        payload = json.loads(body)
        job = IngestionJob(
            resource_id=payload["resourceId"],
            uri=payload["uri"],
            source_type=payload["sourceType"],
            tenant_id=payload["tenantId"],
        )
        nodes = extract_structure(job)

        with httpx.Client(base_url=API_BASE_URL, timeout=30.0) as client:
            response = client.post(
                f"/api/resources/{job.resource_id}/nodes",
                headers={"X-Tenant-Id": job.tenant_id},
                json=nodes,
            )
            response.raise_for_status()

        channel.basic_ack(delivery_tag=method.delivery_tag)
    except Exception:  # noqa: BLE001 - worker must not crash the consume loop
        logger.exception("Failed to process ingestion job")
        channel.basic_nack(delivery_tag=method.delivery_tag, requeue=False)


def main() -> None:
    connection = pika.BlockingConnection(pika.URLParameters(RABBITMQ_URL))
    channel = connection.channel()
    channel.queue_declare(queue=QUEUE_NAME, durable=True)
    channel.basic_qos(prefetch_count=1)
    channel.basic_consume(queue=QUEUE_NAME, on_message_callback=handle_message)

    logger.info("Listening on queue '%s'", QUEUE_NAME)
    try:
        channel.start_consuming()
    except KeyboardInterrupt:
        channel.stop_consuming()
    finally:
        connection.close()


if __name__ == "__main__":
    main()
