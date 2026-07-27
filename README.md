# NeuroSearch Agent

Local-first autonomous research agent in **C# / .NET 10** + Semantic Kernel — chat and embeddings on Ollama, memory on Qdrant, web search via Tavily (default) or Serper. Definitive description: [`PROJECT.txt`](PROJECT.txt). Every metric: [`MEASUREMENTS.txt`](MEASUREMENTS.txt).

| | This repo (measured) | Context |
|---|---|---|
| Docker image | **~51 MB** (§6) | chiseled linux-arm64 AOT |
| Container cold start | **avg 8.8 ms** over 5 runs (§6) | single-run 23/32 ms were not averages |
| Peak RSS (macOS AOT) | **~24 MB** vs ~64 MB JIT (§5) | process start → READY |

Sub-millisecond Qdrant search figures are Qdrant’s (§3). Shipping an agentic SK stack as a ~51 MB AOT binary that reaches READY in milliseconds is the project’s packaging claim (§5–§6).

---

## Injection defense — with a measurement

Measured attack success on identical payloads (`qwen3.5:9b`, temp 0, 3 repeats, single machine):

> **Baseline (n=20):** Spotlighting reduced attack success from **12/20 to 1/20** (McNemar exact, 11 discordant pairs, **p ≈ 0.00098**) — significant at α=0.05 (§11). Policy 1→0 is not demonstrable at n=20 (p = 1.0).
>
> **With search-path + ProviderAnswer payloads (n=24):** A=16/24, B=1/24, C=0/24 (§14). B held at 1. A smaller McNemar p at larger n is **not** stronger evidence of effect size — state the rates and n (§14).

**Six enforcing controls** (not seven): content sanitizer, delimiter neutralizer, spotlight wrapper, tainted-sink rule, allowlist (tools **and** outbound hosts/provenance), tool-call budget. ExfilCheck demoted to **advisory** after it matched fixture domains (`attacker.*`) while the allowlist did the real work — count **7 → 6** on purpose (§11).

Honest claim: measured reduction with known residual risk — **not** injection-proof (§11).

---

## What it is

- **C# / .NET 10** + **Semantic Kernel 1.78** auto function calling  
- Plugins: `WebSearch`, `WebScraper`, `VectorMemory` (Qdrant + Ollama `nomic-embed-text`)  
- Local chat via Ollama (`qwen3.5:9b` by default)  
- Provenance: scraper/search → Untrusted + spotlight → memory `provenance=untrusted` → retrieval surfaces it (§11, §14)  
- Multi-hop via **re-search** (`ResearchDeeperAsync`); scraped-link follow remains blocked (§11, §14)

```text
User → SK agent → tools
              ↓
     WebScraper / WebSearch  →  Untrusted + spotlight delimiters
              ↓
     InjectionPolicyFilter   →  allowlist / budget / tainted-sink
              ↓
     VectorMemory (Qdrant)   →  tagged persistence + semantic recall
```

---

## Quickstart

```bash
# Infra
docker compose up -d
# ollama serve && ollama pull nomic-embed-text && ollama pull qwen3.5:9b

# API keys — user-secrets (dev) OR environment (containers)
dotnet user-secrets set "Tavily:ApiKey" "tvly-..." --project src/NeuroSearch.Agent
dotnet user-secrets set "Serper:ApiKey" "..." --project src/NeuroSearch.Agent
# or: export TAVILY_API_KEY=... SERPER_API_KEY=...
# or container: -e Tavily__ApiKey=... -e Serper__ApiKey=...

# Run (JIT; PublishAot stays false by default)
# Default search provider = tavily (basic depth). Override: --provider serper
dotnet run --project src/NeuroSearch.Agent -c Release

# Non-interactive checks
dotnet run --project src/NeuroSearch.Agent -c Release -- --startup-benchmark
dotnet run --project src/NeuroSearch.Agent -c Release -- --smoke-test
dotnet run --project src/NeuroSearch.Agent -c Release -- --live-verify --provider tavily
dotnet run --project src/NeuroSearch.Agent -c Release -- --live-verify --provider serper

# Tests (124 as of ProviderAnswer session — not “124 attack defenses”; §14)
dotnet test tests/NeuroSearch.Tests/NeuroSearch.Tests.csproj -c Release

# AOT container (Docker Desktop macOS: host.docker.internal, not --network host)
docker build -t neurosearch-agent:aot .
docker run --rm neurosearch-agent:aot --startup-benchmark
docker run --rm \
  -e Ollama__Endpoint=http://host.docker.internal:11434 \
  -e Qdrant__Host=host.docker.internal \
  -e Tavily__ApiKey="$TAVILY_API_KEY" \
  -e Serper__ApiKey="$SERPER_API_KEY" \
  neurosearch-agent:aot --smoke-test
```

**Why Tavily is default:** LLM-oriented retrieval (snippets + optional `RawContent` / `answer`), which can reduce WebScraper calls — the main injection surface. Search-returned content is still Untrusted + spotlighted. Provider URL authorization is **exact-URL** (§14).

More reproduce commands: MEASUREMENTS §8 / §14. Narrative: [`PROJECT.txt`](PROJECT.txt).

---

## What is measured and what is not

Source of truth: **[`MEASUREMENTS.txt`](MEASUREMENTS.txt)** (outranks resume/interview docs). Summary: **[`PROJECT.txt`](PROJECT.txt)** §§5–6.

**Measured (examples):**

- Spotlighting ASR 12/20 → 1/20 (McNemar p ≈ 0.00098); extended suite n=24 still B=1/24 (§11, §14)  
- Benign-page exfil heuristic FP **0/40**; 65 unauthorized-host blocks expected (§11)  
- Retrieval on **100K distractors + 50 paraphrased queries** with HNSW engaged: recall@1=0.88, @5=0.96, @10=0.98, MRR@10≈0.92 (§12)  
- Index: **`hnsw_ef=16`**; **quantization=none** after scalar/binary did not win cleanly (§13)  
- Qdrant synth p95 ≈ 0.93 ms @10K / 5.3 ms @100K; E2E embed+search p95 ≈ 23 ms warm local (§3, §4)  
- Container ~51 MB; startup **avg 8.8 ms** over 5 runs (§6)  
- Live `--live-verify` Serper + Tavily PASS (§14)  

**Not claimed / not done:** third-party red-team; Azure deploy; cross-model ASR; that the early N=50 ef sweep measured HNSW (`indexed_vectors_count` was 0 — invalidated, §12).

---

## Auditing a green suite found the interesting bugs

Four findings (detail in PROJECT.txt §8 / MEASUREMENTS §11–§14):

1. **Tainted-sink** blocked legitimate research→save until a user save-intent carve-out.  
2. **ExfilCheck** matched fixture hostnames — demoted; defenses 7 → 6.  
3. **ef sweep** ran with `indexed_vectors_count=0` (exact search) — table deleted; eval rebuilt (§12).  
4. **Provider URL prefix auth** authorized `/article-<payload>` — fixed to exact-URL (§14).

---

## Docs

| File | Role |
|---|---|
| [`MEASUREMENTS.txt`](MEASUREMENTS.txt) | Verified metrics only |
| [`PROJECT.txt`](PROJECT.txt) | Definitive description ≤ MEASUREMENTS |
| [`docs/RESUME_BULLETS.md`](docs/RESUME_BULLETS.md) | Resume language ≤ MEASUREMENTS |
| [`docs/INTERVIEW_PREP.md`](docs/INTERVIEW_PREP.md) | Talk track ≤ MEASUREMENTS |

`PublishAot` remains **false** for day-to-day builds; enable only at publish/Docker time.
