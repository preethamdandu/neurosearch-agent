# NeuroSearch Agent

A **.NET Native AOT** autonomous research agent — not another multi-gigabyte Python container.

| | NeuroSearch (this repo) | Typical Python agent image |
|---|---|---|
| Image | **~51 MB** | 800 MB–2 GB |
| Container cold start | **~9 ms** avg (5-run) | seconds |
| Peak RSS (macOS AOT) | **~24 MB** | hundreds of MB |

Sub-millisecond Qdrant search is Qdrant's achievement. Shipping an agentic SK stack as a chiseled ~51 MB AOT binary that is ready in milliseconds is the uncommon part.

---

## Injection defense — with a measurement

Most security writeups list controls. This one measured attack success on identical payloads (`qwen3.5:9b`, temp 0, 3 repeats):

> Spotlighting reduced attack success from **12/20 to 1/20** (McNemar exact, 11 discordant pairs, **p ≈ 0.00098**) — significant at α=0.05. The policy filter reduced 1/20 to 0/20, but with a single discordant pair (p = 1.0) that step is **not demonstrable at n=20**; it is retained as a hard backstop for payloads that defeat spotlighting. Cross-model generalization is untested.

**Six enforcing controls** (not seven): content sanitizer, delimiter neutralizer, spotlight wrapper, tainted-sink rule, allowlist (tools **and** outbound hosts/provenance), tool-call budget. A seventh “ExfilCheck” shape heuristic was demoted to **advisory-only** after an audit showed it matched fixture domains (`attacker.*`) while the allowlist did the real work — a control count going **down** after scrutiny.

Every number above is in [`MEASUREMENTS.txt`](MEASUREMENTS.txt).

---

## What it is

- **C# / .NET 10** + **Semantic Kernel 1.78** auto function calling  
- Plugins: `WebSearch`, `WebScraper`, `VectorMemory` (Qdrant + Ollama `nomic-embed-text`)  
- Local chat via Ollama (`qwen3.5:9b` by default)  
- Provenance taint: scraper/search → untrusted spotlight markers → memory payload `provenance=untrusted` → retrieval surfaces it  

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

# Run (JIT; PublishAot stays false by default)
dotnet run --project src/NeuroSearch.Agent -c Release

# Non-interactive checks
dotnet run --project src/NeuroSearch.Agent -c Release -- --startup-benchmark
dotnet run --project src/NeuroSearch.Agent -c Release -- --smoke-test

# Tests (97 expected — not “97 attack defenses”)
dotnet test tests/NeuroSearch.Tests/NeuroSearch.Tests.csproj -c Release

# AOT container
docker build -t neurosearch-agent:aot .
docker run --rm neurosearch-agent:aot --startup-benchmark
```

More reproduce commands (ASR, retrieval quality, latency benches): see §8 of `MEASUREMENTS.txt`.

---

## What I measured and what I didn't

Source of truth: **[`MEASUREMENTS.txt`](MEASUREMENTS.txt)** (outranks resume/interview docs).

**Measured (examples):**

- Spotlighting ASR 12/20 → 1/20 (McNemar p ≈ 0.00098); policy 1 → 0 not significant at n=20  
- Benign-page exfil heuristic FP **0/40** after narrowing; 65 unauthorized-host blocks are expected  
- Retrieval on **100K distractors + 50 paraphrased queries** with HNSW engaged (`indexed_vectors_count > 0`): recall@1=0.88, @5=0.96, @10=0.98, MRR@10≈0.92  
- Qdrant synthetic search p95 ≈ 0.93 ms @10K / 5.3 ms @100K; E2E embed+search p95 ≈ 23 ms (warm local)  
- Container image ~51 MB; startup **avg 8.8 ms** over 5 runs  

**Deliberate capability tradeoff:** the agent will **not** follow URLs discovered inside scraped content. Multi-hop research requires the user to paste the second URL. Auto-following page links is the primary exfiltration vector.

**Not claimed / not done:**

- Injection-proof / red-team certified  
- Azure production deploy  
- Cross-model ASR generalization  
- That an early N=50 “ef sweep” measured HNSW (it didn’t — `indexed_vectors_count` was 0; table deleted)  

---

## Auditing a green suite found the interesting bugs

Two findings came from refusing to trust a green bar:

1. **P0 product break:** a tainted-sink rule that “secured” the agent by blocking legitimate research→save. Narrowed with a user save-intent carve-out; memory-poisoning without that intent still blocks.  
2. **ExfilCheck demotion:** the shape heuristic keyed on fixture hostnames. Plausible-host tests + allowlist enforcement replaced a fake seventh defense.

That habit — measure, then demote over-claims — is the point of this repo as much as the agent itself.

---

## Docs

| File | Role |
|---|---|
| [`MEASUREMENTS.txt`](MEASUREMENTS.txt) | Verified metrics only |
| [`docs/RESUME_BULLETS.md`](docs/RESUME_BULLETS.md) | Resume language ≤ MEASUREMENTS |
| [`docs/INTERVIEW_PREP.md`](docs/INTERVIEW_PREP.md) | Talk track ≤ MEASUREMENTS |

`PublishAot` remains **false** for day-to-day builds; enable only at publish/Docker time.
