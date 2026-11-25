"""Exports one tenant's processed corpus (resources, passages, embeddings) from the Docker Postgres to a JSONL file.

The local runtime imports it (eval/import_corpus.cs), so the same already-extracted documents and the same vectors can be
searched through the SQLite store and compared with the Postgres path on eval/run_benchmark.py, with the extractors and the
embedding model held constant. One resource per line.

    python eval/export_postgres_corpus.py --tenant acme --out eval/corpus-acme.jsonl
"""

import argparse
import subprocess
import sys

QUERY = """
SELECT json_build_object(
  'resourceId', r.resource_id, 'type', r.source_type, 'uri', r.uri, 'sourceVersion', r.source_version,
  'nodes', coalesce((
    SELECT json_agg(json_build_object(
        'nodeId', n.node_id, 'parentNodeId', n.parent_node_id, 'level', n.level, 'title', n.title,
        'summary', n.summary, 'content', n.original_content, 'metadata', n.metadata,
        'embedding', n.embedding::text::json, 'embeddingModel', n.embedding_model) ORDER BY n.ordinal)
    FROM knowledge_nodes n WHERE n.resource_id = r.resource_id), '[]'::json))::text
FROM resources r
WHERE r.tenant_id = '{tenant}' AND r.source_version IS NOT NULL
ORDER BY r.uri
"""


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--tenant", default="acme")
    parser.add_argument("--out", default="eval/corpus-acme.jsonl")
    parser.add_argument("--container", default="knovara-postgres-1")
    args = parser.parse_args()

    if not args.tenant.replace("-", "").replace("_", "").isalnum():
        print("tenant must be alphanumeric", file=sys.stderr)
        return 2

    result = subprocess.run(
        ["docker", "exec", args.container, "psql", "-U", "knovara", "-d", "knovara", "-At", "-c", QUERY.format(tenant=args.tenant)],
        capture_output=True, text=True, encoding="utf-8", check=False)
    if result.returncode != 0:
        print(result.stderr, file=sys.stderr)
        return result.returncode

    lines = [line for line in result.stdout.splitlines() if line.strip()]
    with open(args.out, "w", encoding="utf-8", newline="\n") as out:
        out.write("\n".join(lines) + "\n")
    print(f"wrote {len(lines)} resources to {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
