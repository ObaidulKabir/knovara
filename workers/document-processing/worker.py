"""Document Processing Worker (Phase 1 - Intelligent document ingestion).

Consumes ingestion jobs from RabbitMQ. For each job it:
  1. fetches the source (a file under SOURCE_ROOT, or a public http(s) URL),
  2. hashes it and asks the API whether that version is new (an unchanged
     source stops here, before any parsing),
  3. converts it with Docling (PDF, DOCX, PPTX, HTML, Markdown),
  4. turns Docling's document into a hierarchy: document -> sections -> passages, one passage
     per structural unit (paragraph, list, table, code), joining units too small to be found on
     their own and splitting oversized ones on sentence (or table-row) boundaries,
  5. posts the hierarchy back to the API, or reports the failure.

See system_architecture.md "Format-Aware Processing" and README.md.
"""

from __future__ import annotations

import functools
import hashlib
import ipaddress
import itertools
import json
import logging
import os
import re
import socket
import tempfile
import time
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from urllib.parse import unquote, urljoin, urlparse

import httpx
import pika
from docling.datamodel.base_models import InputFormat
from docling.datamodel.pipeline_options import PdfPipelineOptions
from docling.document_converter import DocumentConverter, PdfFormatOption
from docling_core.types.doc import DocItemLabel, PictureItem, SectionHeaderItem, TableItem, TitleItem
from tokenizers import Tokenizer

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
logger = logging.getLogger("document-processing-worker")

RABBITMQ_URL = os.environ.get("RABBITMQ_URL", "amqp://guest:guest@localhost:5672/")
API_BASE_URL = os.environ.get("KNOVARA_API_BASE_URL", "http://localhost:5000")
QUEUE_NAME = os.environ.get("INGESTION_QUEUE", "ingestion.requests")
SOURCE_ROOT = Path(os.environ.get("SOURCE_ROOT", "/sources"))
MAX_SOURCE_BYTES = int(os.environ.get("MAX_SOURCE_BYTES", str(50 * 1024 * 1024)))
# Passage size is counted in the embedding model's own tokens. The embedder and the reranker both
# read at most 512 tokens, and the reranker shares that with the question, so the cap stays below it.
CHUNK_TOKENIZER = os.environ.get("CHUNK_TOKENIZER", "/opt/tokenizer/tokenizer.json")
MAX_CHUNK_TOKENS = int(os.environ.get("MAX_CHUNK_TOKENS", "384"))
MIN_CHUNK_TOKENS = int(os.environ.get("MIN_CHUNK_TOKENS", "64"))
ALLOW_PRIVATE_URLS = os.environ.get("ALLOW_PRIVATE_URLS", "false").lower() == "true"
DO_OCR = os.environ.get("DOCLING_DO_OCR", "false").lower() == "true"
ARTIFACTS_PATH = os.environ.get("DOCLING_ARTIFACTS_PATH")
HEARTBEAT_SECONDS = int(os.environ.get("RABBITMQ_HEARTBEAT_SECONDS", "600"))
MAX_REDIRECTS = 5
MAX_TITLE_CHARS = 1000

# Part of every version hash: the stored hierarchy depends on the extraction logic as well as the
# source bytes, so bumping this re-processes every source once on its next ingest.
EXTRACTOR_VERSION = "docling-hierarchy-3"
NUMBERED_HEADING = re.compile(r"^(\d+(?:\.\d+)*)\.?\s+\S")

SUPPORTED_SUFFIXES = {".pdf", ".docx", ".pptx", ".html", ".htm", ".md", ".markdown"}
SUFFIX_BY_CONTENT_TYPE = {
    "application/pdf": ".pdf",
    "text/html": ".html",
    "application/xhtml+xml": ".html",
    "text/markdown": ".md",
    "application/vnd.openxmlformats-officedocument.wordprocessingml.document": ".docx",
    "application/vnd.openxmlformats-officedocument.presentationml.presentation": ".pptx",
}
SKIPPED_LABELS = {DocItemLabel.PAGE_HEADER, DocItemLabel.PAGE_FOOTER, DocItemLabel.DOCUMENT_INDEX}
LEVEL_BY_LABEL = {
    DocItemLabel.PARAGRAPH: "paragraph",
    DocItemLabel.TEXT: "paragraph",
    DocItemLabel.LIST_ITEM: "list_item",
    DocItemLabel.CODE: "code",
    DocItemLabel.FORMULA: "formula",
    DocItemLabel.CAPTION: "caption",
    DocItemLabel.FOOTNOTE: "footnote",
}
# Objects that stay one passage each instead of being joined with neighbouring text.
STANDALONE_LEVELS = {"table", "figure", "code"}


class SourceError(Exception):
    """The source can't be fetched or isn't a supported document. Reported to the API, not retried."""


class ApiUnavailable(Exception):
    """The API couldn't be reached or failed on its side. The job is requeued."""


@dataclass(frozen=True)
class IngestionJob:
    resource_id: str
    tenant_id: str
    uri: str
    source_type: str


@dataclass(frozen=True)
class FetchedSource:
    data: bytes
    suffix: str
    name: str


@dataclass(frozen=True)
class Unit:
    """One structural piece of a section's text, before small neighbours are joined."""
    level: str
    text: str
    metadata: dict
    tokens: int
    list_ref: str | None


# --------------------------------------------------------------------------- fetching

def fetch(uri: str) -> FetchedSource:
    if urlparse(uri).scheme in ("http", "https"):
        return fetch_url(uri)
    return read_local(uri)


def read_local(relative: str) -> FetchedSource:
    root = SOURCE_ROOT.resolve()
    candidate = (root / PurePosixPath(relative.replace("\\", "/"))).resolve()
    if root not in candidate.parents:
        raise SourceError(f"'{relative}' is outside the source folder")
    if not candidate.is_file():
        raise SourceError(f"'{relative}' was not found in the source folder")
    if candidate.stat().st_size > MAX_SOURCE_BYTES:
        raise SourceError(f"'{relative}' is larger than {MAX_SOURCE_BYTES} bytes")

    suffix = candidate.suffix.lower()
    if suffix not in SUPPORTED_SUFFIXES:
        raise SourceError(f"'{suffix or 'no extension'}' is not a supported format ({', '.join(sorted(SUPPORTED_SUFFIXES))})")
    return FetchedSource(candidate.read_bytes(), suffix, candidate.name)


def fetch_url(url: str) -> FetchedSource:
    for _ in range(MAX_REDIRECTS + 1):
        ensure_public_host(url)
        with httpx.stream("GET", url, follow_redirects=False, timeout=60) as response:
            if response.is_redirect:
                url = urljoin(url, response.headers["location"])
                continue
            if response.status_code >= 400:
                raise SourceError(f"GET {url} returned {response.status_code}")

            body = bytearray()
            for chunk in response.iter_bytes():
                body.extend(chunk)
                if len(body) > MAX_SOURCE_BYTES:
                    raise SourceError(f"{url} is larger than {MAX_SOURCE_BYTES} bytes")

            suffix = suffix_for(url, response.headers.get("content-type", ""))
            name = unquote(PurePosixPath(urlparse(url).path).name) or urlparse(url).hostname or "document"
            return FetchedSource(bytes(body), suffix, name)
    raise SourceError(f"more than {MAX_REDIRECTS} redirects")


def ensure_public_host(url: str) -> None:
    """Refuses URLs that resolve to private, loopback or link-local addresses (internal services, cloud metadata)."""
    if ALLOW_PRIVATE_URLS:
        return
    host = urlparse(url).hostname
    if not host:
        raise SourceError(f"'{url}' has no host")
    try:
        addresses = {info[4][0] for info in socket.getaddrinfo(host, None)}
    except socket.gaierror as exc:
        raise SourceError(f"cannot resolve {host}: {exc}") from exc
    for address in addresses:
        if not ipaddress.ip_address(address).is_global:
            raise SourceError(f"refusing {host}: it resolves to non-public address {address}")


def suffix_for(url: str, content_type: str) -> str:
    path_suffix = PurePosixPath(urlparse(url).path).suffix.lower()
    if path_suffix in SUPPORTED_SUFFIXES:
        return path_suffix
    media_type = content_type.split(";")[0].strip().lower()
    if media_type in SUFFIX_BY_CONTENT_TYPE:
        return SUFFIX_BY_CONTENT_TYPE[media_type]
    raise SourceError(f"unsupported content type '{media_type or 'unknown'}' at {url}")


# --------------------------------------------------------------------------- extraction

def build_converter() -> DocumentConverter:
    pdf_options = PdfPipelineOptions(artifacts_path=ARTIFACTS_PATH) if ARTIFACTS_PATH else PdfPipelineOptions()
    pdf_options.do_ocr = DO_OCR
    pdf_options.do_table_structure = True
    return DocumentConverter(
        allowed_formats=[InputFormat.PDF, InputFormat.DOCX, InputFormat.PPTX, InputFormat.HTML, InputFormat.MD],
        format_options={InputFormat.PDF: PdfFormatOption(pipeline_options=pdf_options)},
    )


def extract(converter: DocumentConverter, source: FetchedSource) -> list[dict]:
    with tempfile.TemporaryDirectory() as work_dir:
        path = Path(work_dir) / f"source{source.suffix}"
        path.write_bytes(source.data)
        try:
            document = converter.convert(path).document
        except Exception as exc:  # noqa: BLE001 - any conversion failure is a property of the source
            raise SourceError(f"Docling could not convert {source.name}: {exc}") from exc
    return to_nodes(document, source.name)


def to_nodes(document, source_name: str) -> list[dict]:
    """Document -> section (one per heading, nested by heading level) -> passages, in reading order.

    Each passage's title is its heading path, so a search on a section's topic finds its passages.
    """
    nodes: list[dict] = []
    ids = (f"n{i}" for i in itertools.count())

    def add(parent: str | None, level: str, title: str, content: str | None, metadata: dict) -> str:
        local_id = next(ids)
        nodes.append({
            "localId": local_id,
            "parentLocalId": parent,
            "level": level,
            "title": title[:MAX_TITLE_CHARS],
            "content": content,
            "metadata": metadata,
        })
        return local_id

    root = add(None, "document", source_name, None, {"source": source_name})
    sections: list[tuple[int, str, str]] = []  # (heading level, local id, heading text)
    levels, inferred = heading_levels(document)
    caption_refs = {ref.cref for owner in [*document.tables, *document.pictures] for ref in owner.captions}
    titled = content_seen = False

    def heading_path() -> str:
        return " > ".join([nodes[0]["title"]] + [text for _, _, text in sections])

    def parent() -> str:
        return sections[-1][1] if sections else root

    pending: list[Unit] = []  # the current section's text units, joined when the section ends

    def flush() -> None:
        for group in merge_units(pending):
            level, content, metadata = combine(group)
            add(parent(), level, heading_path(), content, metadata)
        pending.clear()

    for item, _depth in document.iterate_items():
        label = getattr(item, "label", None)
        if label in SKIPPED_LABELS or getattr(item, "self_ref", None) in caption_refs:
            continue

        metadata = {"label": str(getattr(label, "value", label))}
        page = item.prov[0].page_no if getattr(item, "prov", None) else None
        if page:
            metadata["page"] = str(page)

        if isinstance(item, TitleItem):
            if item.text.strip() and not sections:
                nodes[0]["title"] = item.text.strip()[:MAX_TITLE_CHARS]
                titled = True
            continue

        if isinstance(item, SectionHeaderItem):
            text = item.text.strip()
            if not text:
                continue
            # PDFs rarely mark a title; a heading that opens page 1 before any text is the title.
            if inferred and not titled and not content_seen and not sections and page == 1:
                nodes[0]["title"] = text[:MAX_TITLE_CHARS]
                titled = True
                continue
            flush()
            heading_level = levels.get(item.self_ref, item.level or 1)
            while sections and sections[-1][0] >= heading_level:
                sections.pop()
            metadata["headingLevel"] = str(heading_level)
            section_id = add(parent(), "section", f"{heading_path()} > {text}", None, metadata)
            sections.append((heading_level, section_id, text))
            continue

        if isinstance(item, TableItem):
            level, content, pieces = "table", item.export_to_markdown(doc=document), split_table
        elif isinstance(item, PictureItem):
            level, content, pieces = "figure", item.caption_text(doc=document), split_text
        else:
            level = LEVEL_BY_LABEL.get(label, str(getattr(label, "value", label)))
            content, pieces = getattr(item, "text", ""), split_text

        content = (content or "").strip()
        if not content:
            continue
        content_seen = True

        chunks = pieces(content, MAX_CHUNK_TOKENS)
        standalone = level in STANDALONE_LEVELS
        if standalone:
            flush()
        list_ref = item.parent.cref if level == "list_item" and getattr(item, "parent", None) else None
        for index, chunk in enumerate(chunks, start=1):
            chunk_metadata = dict(metadata)
            if len(chunks) > 1:
                chunk_metadata["chunk"] = f"{index}/{len(chunks)}"
            if standalone:
                add(parent(), level, heading_path(), chunk, chunk_metadata)
            else:
                pending.append(Unit(level, chunk, chunk_metadata, count_tokens(chunk), list_ref))

    flush()
    return nodes


def merge_units(units: list[Unit]) -> list[list[Unit]]:
    """Groups a section's consecutive units: small ones join neighbours, a list stays together, none exceeds the cap."""
    groups: list[list[Unit]] = []
    for unit in units:
        if groups and joinable(groups[-1], unit):
            groups[-1].append(unit)
        else:
            groups.append([unit])
    return groups


def joinable(group: list[Unit], unit: Unit) -> bool:
    size = sum(member.tokens for member in group)
    if size + unit.tokens > MAX_CHUNK_TOKENS:
        return False
    same_list = unit.list_ref is not None and unit.list_ref == group[-1].list_ref
    # A unit under the minimum (an author line, a one-line paragraph) carries too little meaning to
    # be found on its own; two units that are each big enough stay separate passages.
    return same_list or size < MIN_CHUNK_TOKENS or unit.tokens < MIN_CHUNK_TOKENS


def combine(group: list[Unit]) -> tuple[str, str, dict]:
    if len(group) == 1:
        return group[0].level, group[0].text, group[0].metadata
    levels = {member.level for member in group}
    level = "list" if levels == {"list_item"} else next(iter(levels)) if len(levels) == 1 else "text"
    metadata = {
        "label": ",".join(dict.fromkeys(member.metadata["label"] for member in group)),
        "merged": str(len(group)),
    }
    pages = [member.metadata["page"] for member in group if "page" in member.metadata]
    if pages:
        metadata["page"] = pages[0]
        if pages[-1] != pages[0]:
            metadata["pageEnd"] = pages[-1]
    return level, "\n".join(member.text for member in group), metadata


def heading_levels(document) -> tuple[dict[str, int], bool]:
    """Returns each heading's level, and whether the levels had to be inferred.

    Docling's PDF pipeline reports every heading at level 1. When all headings share one level,
    depth comes from section numbering ("3.1 PDF backends" -> 2), and an unnumbered heading sits
    one level below the most recent numbered one ("Layout Analysis Model" under "3.2 AI models").
    """
    headers = [item for item, _ in document.iterate_items() if isinstance(item, SectionHeaderItem)]
    if len({header.level for header in headers}) > 1:
        return {header.self_ref: header.level for header in headers}, False

    levels, last_numbered = {}, 0
    for header in headers:
        match = NUMBERED_HEADING.match(header.text.strip())
        if match:
            last_numbered = match.group(1).count(".") + 1
            levels[header.self_ref] = last_numbered
        else:
            levels[header.self_ref] = last_numbered + 1 if last_numbered else 1
    return levels, True


@functools.cache
def tokenizer() -> Tokenizer:
    loaded = Tokenizer.from_file(CHUNK_TOKENIZER)
    loaded.no_truncation()  # the saved tokenizer truncates at 512, which would hide oversized passages
    loaded.no_padding()
    return loaded


def count_tokens(text: str) -> int:
    return len(tokenizer().encode(text, add_special_tokens=False).ids)


def pack(parts: list[str], separator: str, limit: int, split_part) -> list[str]:
    """Joins consecutive parts up to the token limit; a part over the limit on its own goes to split_part."""
    chunks, current, size = [], [], 0
    for part in parts:
        tokens = count_tokens(part)
        if tokens > limit:
            if current:
                chunks.append(separator.join(current))
                current, size = [], 0
            chunks.extend(split_part(part))
            continue
        if current and size + tokens > limit:
            chunks.append(separator.join(current))
            current, size = [], 0
        current.append(part)
        size += tokens
    if current:
        chunks.append(separator.join(current))
    return chunks


def split_text(text: str, limit: int) -> list[str]:
    """Splits on sentence boundaries, then between words; a single over-long word is cut."""
    if count_tokens(text) <= limit:
        return [text]

    def cut(word: str) -> list[str]:
        return [word[i:i + limit] for i in range(0, len(word), limit)]  # a token is at least one character

    def split_sentence(sentence: str) -> list[str]:
        return pack(sentence.split(), " ", limit, cut)

    return pack(re.split(r"(?<=[.!?])\s+", text), " ", limit, split_sentence)


def split_table(markdown: str, limit: int) -> list[str]:
    """Splits a Markdown table between rows, repeating the header row and separator in every piece."""
    if count_tokens(markdown) <= limit:
        return [markdown]
    lines = markdown.splitlines()
    header = "\n".join(lines[:2])
    budget = limit - count_tokens(header)
    if budget < limit // 2:  # a header this wide would leave too little room for rows
        return split_text(markdown, limit)
    pieces = pack(lines[2:], "\n", budget, lambda row: split_text(row, budget))
    return [f"{header}\n{piece}" for piece in pieces]


# --------------------------------------------------------------------------- API

class Api:
    def __init__(self, tenant_id: str):
        self._client = httpx.Client(base_url=API_BASE_URL, timeout=120, headers={"X-Tenant-Id": tenant_id})

    def __enter__(self) -> "Api":
        return self

    def __exit__(self, *_exc) -> None:
        self._client.close()

    def _post(self, path: str, body: dict) -> httpx.Response:
        try:
            response = self._client.post(path, json=body)
        except httpx.TransportError as exc:
            raise ApiUnavailable(f"POST {path}: {exc}") from exc
        if response.status_code >= 500:
            raise ApiUnavailable(f"POST {path} returned {response.status_code}")
        response.raise_for_status()
        return response

    def version_changed(self, resource_id: str, content_hash: str) -> bool:
        return self._post(f"/api/resources/{resource_id}/version", {"contentHash": content_hash}).json()["changed"]

    def submit(self, resource_id: str, content_hash: str, nodes: list[dict]) -> None:
        self._post(f"/api/resources/{resource_id}/nodes", {"contentHash": content_hash, "nodes": nodes})

    def fail(self, resource_id: str, error: str) -> None:
        self._post(f"/api/resources/{resource_id}/failure", {"error": error[:2000]})


# --------------------------------------------------------------------------- consuming

def process(converter: DocumentConverter, job: IngestionJob) -> None:
    with Api(job.tenant_id) as api:
        try:
            source = fetch(job.uri)
            content_hash = "sha256:" + hashlib.sha256(EXTRACTOR_VERSION.encode() + b"\0" + source.data).hexdigest()
            if not api.version_changed(job.resource_id, content_hash):
                logger.info("%s unchanged (%s), skipped", job.uri, content_hash[:19])
                return

            started = time.monotonic()
            nodes = extract(converter, source)
            api.submit(job.resource_id, content_hash, nodes)
            logger.info("%s -> %d nodes in %.1fs", job.uri, len(nodes), time.monotonic() - started)
        except SourceError as exc:
            logger.warning("%s failed: %s", job.uri, exc)
            api.fail(job.resource_id, str(exc))
        except httpx.HTTPStatusError as exc:
            message = f"API rejected {exc.request.url.path}: {exc.response.status_code} {exc.response.text[:500]}"
            logger.warning("%s failed: %s", job.uri, message)
            api.fail(job.resource_id, message)


def handle_message(converter: DocumentConverter, channel, method, _properties, body: bytes) -> None:
    try:
        payload = json.loads(body)
        job = IngestionJob(
            resource_id=payload["resourceId"],
            tenant_id=payload["tenantId"],
            uri=payload["uri"],
            source_type=payload["sourceType"],
        )
    except (ValueError, KeyError) as exc:
        logger.error("discarding malformed job %r: %s", body[:200], exc)
        channel.basic_ack(delivery_tag=method.delivery_tag)
        return

    try:
        process(converter, job)
        channel.basic_ack(delivery_tag=method.delivery_tag)
    except ApiUnavailable as exc:
        logger.warning("API unavailable, requeueing %s: %s", job.uri, exc)
        time.sleep(5)
        channel.basic_nack(delivery_tag=method.delivery_tag, requeue=True)
    except Exception:  # noqa: BLE001 - one bad job must not stop the worker
        logger.exception("unexpected failure on %s; dropping the job", job.uri)
        channel.basic_ack(delivery_tag=method.delivery_tag)


def main() -> None:
    logger.info("loading Docling (OCR %s)", "on" if DO_OCR else "off")
    converter = build_converter()

    parameters = pika.URLParameters(RABBITMQ_URL)
    parameters.heartbeat = HEARTBEAT_SECONDS
    parameters.blocked_connection_timeout = 300

    while True:
        try:
            connection = pika.BlockingConnection(parameters)
        except pika.exceptions.AMQPConnectionError:
            logger.warning("RabbitMQ unavailable, retrying in 5s")
            time.sleep(5)
            continue

        channel = connection.channel()
        channel.queue_declare(queue=QUEUE_NAME, durable=True)
        channel.basic_qos(prefetch_count=1)
        channel.basic_consume(queue=QUEUE_NAME, on_message_callback=functools.partial(handle_message, converter))
        logger.info("listening on '%s', API at %s, sources at %s", QUEUE_NAME, API_BASE_URL, SOURCE_ROOT)
        try:
            channel.start_consuming()
        except pika.exceptions.ConnectionClosedByBroker:
            logger.warning("connection closed by broker, reconnecting")
        except pika.exceptions.AMQPConnectionError:
            logger.warning("connection lost, reconnecting")
        except KeyboardInterrupt:
            channel.stop_consuming()
            connection.close()
            return


if __name__ == "__main__":
    main()
