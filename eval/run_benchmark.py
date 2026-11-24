"""Retrieval benchmark for Knovara.

Asks every question in questions.json through POST /api/retrieval/search in each
retrieval mode and reports, per mode and per question kind:
  Recall@k  share of questions with a correct passage in the top k
  MRR       mean reciprocal rank of the first correct passage (0 if none in top 10)
  latency   client-side p50 / p95 per search
  top-3 chars  mean size of the top 3 passages: what a correct hit costs in context, since
               larger passages make the "contains a fragment" check easier to pass
  reranked  share of answers the API reports as reranked (Adaptive reranks only uncertain ones)

A passage counts as correct when it contains any of the question's "expect"
fragments (case-insensitive). Standard library only.

  python eval/run_benchmark.py --base-url http://localhost:5299 --tenant acme
"""

from __future__ import annotations

import argparse
import json
import statistics
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

MODES = ["Lexical", "Semantic", "Hybrid", "HybridRerank", "Adaptive"]  # must match Knovara.Core.Models.RetrievalMode exactly
CUTOFFS = [1, 3, 5, 10]


def search(base_url: str, tenant: str, question: str, mode: str, top_k: int) -> tuple[list[dict], float, bool]:
    body = json.dumps({"question": question, "mode": mode, "topK": top_k}).encode()
    request = urllib.request.Request(
        f"{base_url}/api/retrieval/search",
        data=body,
        headers={"Content-Type": "application/json", "X-Tenant-Id": tenant},
        method="POST",
    )
    started = time.perf_counter()
    with urllib.request.urlopen(request, timeout=120) as response:
        payload = json.load(response)
    return payload["evidence"], (time.perf_counter() - started) * 1000, bool(payload.get("reranked"))


def first_hit(evidence: list[dict], expect: list[str]) -> int | None:
    fragments = [fragment.lower() for fragment in expect]
    for rank, item in enumerate(evidence, start=1):
        content = item["content"].lower()
        if any(fragment in content for fragment in fragments):
            return rank
    return None


def summarize(ranks: list[int | None]) -> dict:
    count = len(ranks)
    result = {f"R@{k}": sum(1 for r in ranks if r is not None and r <= k) / count for k in CUTOFFS}
    result["MRR"] = sum(1 / r for r in ranks if r is not None) / count
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    # 127.0.0.1, not localhost: on Windows, Python tries the IPv6 resolution first and falls back
    # to IPv4 only after it fails, adding ~2s per call when the server listens on IPv4 only (as
    # ASP.NET Core does when bound to 0.0.0.0) - this was silently inflating every latency figure
    # in the first two runs of this benchmark.
    parser.add_argument("--base-url", default="http://127.0.0.1:5299")
    parser.add_argument("--tenant", default="acme")
    parser.add_argument("--modes", default=",".join(MODES))
    parser.add_argument("--questions", default=str(Path(__file__).with_name("questions.json")))
    parser.add_argument("--out", default=str(Path(__file__).parent / "results"))
    args = parser.parse_args()

    questions = json.loads(Path(args.questions).read_text(encoding="utf-8"))
    modes = [m.strip() for m in args.modes.split(",") if m.strip()]
    kinds = sorted({q["kind"] for q in questions})

    report: dict = {"generatedAt": datetime.now(timezone.utc).isoformat(), "questions": len(questions), "modes": {}}
    misses: dict[str, list[str]] = {}

    for mode in modes:
        ranks, latencies, top3_chars, per_kind = [], [], [], {kind: [] for kind in kinds}
        misses[mode] = []
        errors = reranked = 0
        try:
            search(args.base_url, args.tenant, "warm up", mode, 10)  # first call pays connection/model warm-up
        except (urllib.error.HTTPError, urllib.error.URLError, TimeoutError) as exc:
            print(f"{mode} warm-up failed ({exc}); continuing", file=sys.stderr)
        for q in questions:
            try:
                evidence, latency, was_reranked = search(args.base_url, args.tenant, q["question"], mode, 10)
            except (urllib.error.HTTPError, urllib.error.URLError, TimeoutError) as exc:
                # A single slow/failed call (CPU inference time varies a lot with real passage length)
                # counts as a miss rather than aborting every other question's result in this mode.
                detail = exc.read()[:200] if isinstance(exc, urllib.error.HTTPError) else str(exc)
                print(f"{mode} {q['id']}: request failed ({detail!r})", file=sys.stderr)
                errors += 1
                rank, latency = None, None
            else:
                rank = first_hit(evidence, q["expect"])
                top3_chars.append(sum(len(item["content"]) for item in evidence[:3]))
                reranked += was_reranked
            if latency is not None:
                latencies.append(latency)
            ranks.append(rank)
            per_kind[q["kind"]].append(rank)
            if rank is None or rank > 3:
                reason = "request failed" if latency is None else f"rank {rank or '>10'}"
                misses[mode].append(f"{q['id']} ({reason}): {q['question']}")

        report["modes"][mode] = {
            "all": summarize(ranks),
            **{kind: summarize(per_kind[kind]) for kind in kinds},
            "errors": errors,
            "top3Chars": round(statistics.mean(top3_chars)) if top3_chars else None,
            "rerankedShare": round(reranked / len(questions), 2),
            "latencyMs": {
                "p50": round(statistics.median(latencies), 1) if latencies else None,
                "p95": round(sorted(latencies)[max(0, int(len(latencies) * 0.95) - 1)], 1) if latencies else None,
            },
        }

    lines = [
        f"# Retrieval benchmark ({report['generatedAt'][:19]}Z)",
        "",
        f"{len(questions)} questions ({', '.join(f'{sum(q['kind'] == k for q in questions)} {k}' for k in kinds)}), tenant `{args.tenant}`.",
        "",
        "| Mode | R@1 | R@3 | R@5 | R@10 | MRR | " + " | ".join(f"{k} R@3" for k in kinds) + " | top-3 chars | reranked | p50 ms | p95 ms | errors |",
        "|---|---|---|---|---|---|" + "---|" * len(kinds) + "---|---|---|---|---|",
    ]
    for mode, result in report["modes"].items():
        overall = result["all"]
        lines.append(
            f"| {mode} | " + " | ".join(f"{overall[f'R@{k}']:.2f}" for k in CUTOFFS)
            + f" | {overall['MRR']:.2f} | " + " | ".join(f"{result[k]['R@3']:.2f}" for k in kinds)
            + f" | {result['top3Chars']} | {result['rerankedShare']:.0%} | {result['latencyMs']['p50']} | {result['latencyMs']['p95']} | {result['errors']} |"
        )
    lines += ["", "## Questions not answered in the top 3", ""]
    for mode, items in misses.items():
        lines.append(f"**{mode}** ({len(items)})")
        lines += [f"- {item}" for item in items] or ["- none"]
        lines.append("")

    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
    (out_dir / f"{stamp}.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    (out_dir / f"{stamp}.md").write_text("\n".join(lines), encoding="utf-8")
    print("\n".join(lines))
    print(f"\nwritten to {out_dir / stamp}.md/.json")
    return 0


if __name__ == "__main__":
    sys.exit(main())
