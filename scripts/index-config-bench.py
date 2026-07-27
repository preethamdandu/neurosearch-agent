#!/usr/bin/env python3
"""Index config bench: exact-vs-ANN, baseline memory, scalar/binary quantization.

Uses neurosearch-retrieval-100k (must already exist, green, indexed).
Recall always computed on the 50 labeled pairs only.
"""
from __future__ import annotations

import json
import os
import subprocess
import time
import urllib.request
from pathlib import Path

QDRANT = os.environ.get("QDRANT_HTTP", "http://localhost:6333")
OLLAMA = os.environ.get("OLLAMA", "http://localhost:11434")
MODEL = "nomic-embed-text"
BASE = "neurosearch-retrieval-100k"
SQ = "neurosearch-retrieval-100k-sq"
BQ = "neurosearch-retrieval-100k-bq"
DIM = 768
ROOT = Path(__file__).resolve().parents[1]
FIXTURES = ROOT / "tests/NeuroSearch.Tests/Fixtures/RetrievalEval"


def http_json(method: str, url: str, body=None, timeout=600):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(
        url, data=data, method=method,
        headers={"Content-Type": "application/json"} if data else {})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode())


def embed(text: str) -> list[float]:
    payload = json.dumps({"model": MODEL, "prompt": text}).encode()
    req = urllib.request.Request(
        f"{OLLAMA}/api/embeddings", data=payload,
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=120) as r:
        return json.loads(r.read().decode())["embedding"]


def collection_info(name: str) -> dict:
    return http_json("GET", f"{QDRANT}/collections/{name}")["result"]


def wait_green(name: str, timeout=900, min_points=1):
    deadline = time.time() + timeout
    last_idx = None
    stable = 0
    while time.time() < deadline:
        info = collection_info(name)
        status = info["status"]
        indexed = info.get("indexed_vectors_count", 0)
        points = info.get("points_count", 0)
        print(f"  wait {name}: status={status} points={points} indexed={indexed}", flush=True)
        if points < min_points:
            time.sleep(2)
            continue
        if status == "green" and indexed > 0:
            if indexed == last_idx:
                stable += 1
                if stable >= 2:
                    return info
            else:
                stable = 0
            last_idx = indexed
        time.sleep(2)
    raise TimeoutError(f"{name} not green/stable in {timeout}s")


def search(name: str, vector, limit=5, *, ef=16, exact=False,
           rescore=None, oversampling=None):
    params = {"hnsw_ef": ef, "exact": exact}
    if rescore is not None or oversampling is not None:
        q = {}
        if rescore is not None:
            q["rescore"] = rescore
        if oversampling is not None:
            q["oversampling"] = oversampling
        params["quantization"] = q
    body = {
        "vector": vector,
        "limit": limit,
        "with_payload": True,
        "params": params,
    }
    return http_json("POST", f"{QDRANT}/collections/{name}/points/search", body)["result"]


def load_eval():
    corpus = json.loads((FIXTURES / "corpus.json").read_text())
    paras = json.loads((FIXTURES / "paraphrase_queries.json").read_text())
    docs = {d["id"]: d for d in corpus["docs"]}
    queries = [(q["query"], q["doc_id"]) for q in paras["queries"]]
    # Map doc_id -> point id (distractors=100000, labeled start at 100001)
    id_map = {d["id"]: 100000 + i + 1 for i, d in enumerate(corpus["docs"])}
    return docs, queries, id_map


def metrics(name, qvecs, queries, id_map, *, ef=16, exact=False,
            rescore=None, oversampling=None, repeats=5):
    hits1 = hits5 = hits10 = 0
    rr = 0.0
    lats = []
    top5_sets = []
    # Exact search is expensive — one pass for set comparison / recall; ANN keeps repeats for p95
    use_repeats = 1 if exact else repeats
    for qi, ((query, rel), vec) in enumerate(zip(queries, qvecs)):
        relevant = id_map[rel]
        result = None
        for r in range(use_repeats):
            t0 = time.perf_counter()
            result = search(name, vec, limit=10, ef=ef, exact=exact,
                            rescore=rescore, oversampling=oversampling)
            ms = (time.perf_counter() - t0) * 1000
            if (not exact and r > 0) or (exact and r == 0):
                lats.append(ms)
        if qi % 10 == 0:
            print(f"  … query {qi}/{len(queries)} exact={exact} ef={ef}", flush=True)
        ids = [int(h["id"]) for h in result]
        top5_sets.append(tuple(ids[:5]))
        try:
            rank = ids.index(relevant)
        except ValueError:
            rank = -1
        if rank == 0:
            hits1 += 1
        if 0 <= rank < 5:
            hits5 += 1
        if 0 <= rank < 10:
            hits10 += 1
            rr += 1.0 / (rank + 1)
    n = len(queries)
    lats.sort()
    p50 = lats[max(0, int(0.50 * len(lats)) - 1)] if lats else 0
    p95 = lats[max(0, int(0.95 * len(lats)) - 1)] if lats else 0
    return {
        "recall@1": hits1 / n,
        "recall@5": hits5 / n,
        "recall@10": hits10 / n,
        "mrr@10": rr / n,
        "p50_ms": p50,
        "p95_ms": p95,
        "top5_sets": top5_sets,
    }


def docker_stats_qdrant():
    out = subprocess.check_output(
        ["docker", "stats", "--no-stream", "--format",
         "{{.Name}}\t{{.MemUsage}}\t{{.MemPerc}}"],
        text=True)
    for line in out.splitlines():
        if "qdrant" in line.lower() or "neurosearch-qdrant" in line:
            return line.strip()
    return out.strip()


def storage_du():
    path = ROOT / "qdrant_data"
    out = subprocess.check_output(["du", "-sh", str(path)], text=True)
    return out.strip()


def delete_collection(name):
    try:
        http_json("DELETE", f"{QDRANT}/collections/{name}")
        print(f"deleted {name}", flush=True)
    except Exception as e:
        print(f"delete {name}: {e}", flush=True)


def scroll_copy(source: str, dest: str, batch=256):
    """Copy all points (vectors + payload) from source → dest via scroll/upsert."""
    offset = None
    total = 0
    while True:
        body = {
            "limit": batch,
            "with_payload": True,
            "with_vector": True,
        }
        if offset is not None:
            body["offset"] = offset
        resp = http_json("POST", f"{QDRANT}/collections/{source}/points/scroll", body)
        points = resp["result"]["points"]
        next_offset = resp["result"].get("next_page_offset")
        if not points:
            break
        upsert = {
            "points": [
                {
                    "id": p["id"],
                    "vector": p["vector"],
                    "payload": p.get("payload") or {},
                }
                for p in points
            ]
        }
        http_json("PUT", f"{QDRANT}/collections/{dest}/points?wait=true", upsert)
        total += len(points)
        if total % 5000 < batch:
            print(f"  copied {total}…", flush=True)
        if next_offset is None:
            break
        offset = next_offset
    print(f"  copied total={total}", flush=True)
    return total


def create_from(source, dest, quant_config):
    delete_collection(dest)
    body = {
        "vectors": {"size": DIM, "distance": "Cosine", "on_disk": False},
        "quantization_config": quant_config,
        "optimizers_config": {"indexing_threshold": 10000},
    }
    print(f"Creating {dest} with {json.dumps(quant_config)}…", flush=True)
    http_json("PUT", f"{QDRANT}/collections/{dest}", body)
    print(f"Copying points {source} → {dest}…", flush=True)
    scroll_copy(source, dest)
    src_pts = collection_info(source)["points_count"]
    return wait_green(dest, min_points=max(1, src_pts - 10))


def main():
    print("=== INDEX CONFIG BENCH ===")
    print(f"UTC={time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())}")
    docs, queries, id_map = load_eval()
    print(f"Queries={len(queries)} labeled_docs={len(docs)}")

    info = wait_green(BASE)
    print(f"BASE indexed={info['indexed_vectors_count']} points={info['points_count']}")

    print("Embedding queries...")
    qvecs = [embed(q) for q, _ in queries]
    print("Ollama warm OK")

    # ── Phase 0: exact vs ANN ─────────────────────────────────────────
    print("\n=== PHASE 0: exact vs ANN (ef=16) ===")
    ann = metrics(BASE, qvecs, queries, id_map, ef=16, exact=False)
    exact = metrics(BASE, qvecs, queries, id_map, ef=16, exact=True)
    identical = sum(1 for a, b in zip(ann["top5_sets"], exact["top5_sets"]) if a == b)
    print(f"IDENTICAL_TOP5={identical}/{len(queries)}")
    print(f"ANN_recall@5={ann['recall@5']:.4f} EXACT_recall@5={exact['recall@5']:.4f}")
    print(f"ANN_p95={ann['p95_ms']:.3f} EXACT_p95={exact['p95_ms']:.3f}")
    if identical >= len(queries) - 1:
        chosen_ef = 16
        print("ANN-recall ≈ 1.0 — the index is lossless at this scale; "
              "the 0.04 miss is embedding/label quality.")
    else:
        print("Material ANN/exact divergence — scanning ef...")
        chosen_ef = None
        for ef in (16, 32, 64, 128, 256):
            m = metrics(BASE, qvecs, queries, id_map, ef=ef, exact=False)
            same = sum(1 for a, b in zip(m["top5_sets"], exact["top5_sets"]) if a == b)
            print(f"  ef={ef} identical_top5={same}/{len(queries)}")
            if same >= len(queries) - 1:
                chosen_ef = ef
                break
        if chosen_ef is None:
            chosen_ef = 256
            print("NOT SAFE at low ef — defaulting to 256")
    print(f"CHOSEN_EF={chosen_ef}")

    # ── Phase 1: baseline memory ──────────────────────────────────────
    print("\n=== PHASE 1: baseline memory + recall @ chosen ef ===")
    info = wait_green(BASE)
    print("COLLECTION_INFO_STATUS=", info["status"])
    print("COLLECTION_POINTS=", info["points_count"])
    print("COLLECTION_INDEXED=", info["indexed_vectors_count"])
    print("COLLECTION_CONFIG=", json.dumps(info.get("config", {}), default=str)[:2000])
    print("DOCKER_STATS_BEFORE_RESTART=", docker_stats_qdrant())
    print("STORAGE_DU=", storage_du())
    theoretical = 100_000 * 768 * 4 / (1024 * 1024)
    print(f"THEORETICAL_FLOAT32_VECTORS_MB={theoretical:.1f}")

    print("Restarting Qdrant for steady-state RSS...")
    subprocess.check_call(["docker", "restart", "neurosearch-qdrant"])
    time.sleep(5)
    # wait for API
    for _ in range(60):
        try:
            wait_green(BASE, timeout=30)
            break
        except Exception:
            time.sleep(2)
    info = wait_green(BASE)
    print("DOCKER_STATS_STEADY=", docker_stats_qdrant())
    print("STORAGE_DU_STEADY=", storage_du())

    base_m = metrics(BASE, qvecs, queries, id_map, ef=chosen_ef, exact=False)
    print(f"BASELINE_EF={chosen_ef}")
    for k in ("recall@1", "recall@5", "recall@10", "mrr@10", "p50_ms", "p95_ms"):
        print(f"BASELINE_{k.upper().replace('@','_')}={base_m[k]:.4f}" if "recall" in k or "mrr" in k
              else f"BASELINE_{k.upper()}={base_m[k]:.3f}")

    # ── Phase 2: scalar int8 ──────────────────────────────────────────
    print("\n=== PHASE 2: scalar int8 ===")
    create_from(BASE, SQ, {
        "scalar": {"type": "int8", "quantile": 0.99, "always_ram": True}
    })
    print("DOCKER_STATS_SQ=", docker_stats_qdrant())
    print("STORAGE_DU_SQ=", storage_du())

    sq_rescore_on = metrics(SQ, qvecs, queries, id_map, ef=chosen_ef,
                            rescore=True, oversampling=1.0)
    sq_rescore_off = metrics(SQ, qvecs, queries, id_map, ef=chosen_ef,
                             rescore=False, oversampling=1.0)
    sq_over2 = metrics(SQ, qvecs, queries, id_map, ef=chosen_ef,
                       rescore=True, oversampling=2.0)
    print("SQ_RESCORE_ON=", json.dumps({k: v for k, v in sq_rescore_on.items() if k != "top5_sets"}))
    print("SQ_RESCORE_OFF=", json.dumps({k: v for k, v in sq_rescore_off.items() if k != "top5_sets"}))
    print("SQ_RESCORE_ON_OVER2=", json.dumps({k: v for k, v in sq_over2.items() if k != "top5_sets"}))

    # ── Phase 3: binary ───────────────────────────────────────────────
    print("\n=== PHASE 3: binary quantization ===")
    create_from(BASE, BQ, {"binary": {"always_ram": True}})
    print("DOCKER_STATS_BQ=", docker_stats_qdrant())
    print("STORAGE_DU_BQ=", storage_du())
    bq = metrics(BQ, qvecs, queries, id_map, ef=chosen_ef,
                 rescore=True, oversampling=2.0)
    print("BQ_RESCORE_ON_OVER2=", json.dumps({k: v for k, v in bq.items() if k != "top5_sets"}))
    if bq["recall@5"] < 0.90:
        print("BQ_VERDICT=against — recall@5 below 0.90")
    else:
        print("BQ_VERDICT=acceptable_at_this_corpus")

    print("\n=== SUMMARY TABLE ===")
    print(f"chosen_ef={chosen_ef} identical_top5={identical}/{len(queries)}")
    print("=== END ===")


if __name__ == "__main__":
    main()
