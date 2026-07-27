# Resume Bullet Points — NeuroSearch Agent

All metrics in this file come from **MEASUREMENTS.txt** (see repo root).
Every number has a corresponding command and raw output recorded there.
Do NOT add or change numbers without updating MEASUREMENTS.txt first.

---

## Primary Bullet (Project Title)

**NeuroSearch** — Autonomous AI Research Agent
*Stack: C# / .NET 10, Microsoft Semantic Kernel, Qdrant Vector DB, Ollama, Docker, Native AOT*

---

## Resume Bullets (Choose 3–4)

### Option 1: Architecture & Agent Framework (Best for Microsoft / Azure)
```
Architected an autonomous AI agent using Microsoft Semantic Kernel with auto function
calling, enabling multi-step ReAct-style planning and deterministic C# plugin execution
(WebSearch, WebScraper, VectorMemory) driven by natural language instructions.
```
**Why it works**: Shows architecture skills, Microsoft tech, and understanding of
AI design patterns.

---

### Option 2: Function Calling & Native Code Integration (Best for General SWE)
```
Implemented auto function calling by decorating C# methods with [KernelFunction]
attributes, allowing the LLM to dynamically invoke 3 custom plugins based on
natural language intent — bridging AI reasoning with deterministic code execution.
```
**Why it works**: Demonstrates deep understanding of how LLMs invoke traditional code.

---

### Option 3: Vector Database & RAG Performance (Best for Data Infrastructure)
```
Engineered long-term semantic memory using Qdrant (HNSW indexing) with
nomic-embed-text embeddings; benchmarked Qdrant search p95 at 0.93 ms (10K vectors)
and 5.3 ms (100K vectors); end-to-end RAG (embed + search) p95 ≈ 23 ms on a warm
local model. Retrieval quality on 50 paraphrased queries over 100K distractors
with HNSW engaged (indexed_vectors_count>0): recall@1=0.88, @5=0.96, @10=0.98,
MRR@10≈0.92 at ef=128 (structural labels — not from the embedder; see §12).
An earlier N=50 ef sweep was invalidated (exact search; indexed_vectors_count=0).
```
**Why it works**: Shows database expertise, performance measurement rigour, and
honesty about what the index was actually doing.

---

### Option 4: Performance Engineering & AOT (Best for Systems / Performance Roles)
```
Published with .NET Native AOT: measured startup averaged 48 ms vs 99 ms JIT
(~52% faster), peak RSS 24 MB vs 64 MB (~62% less) on macOS arm64. Multi-stage
Dockerfile produces a ~51 MB linux-arm64 image; container startup avg 8.8 ms
over 5 runs (single-run 23/32 ms figures were not averages — corrected).
```
**Why it works**: Quantifiable, reproducible performance data with honest
measurement methodology.

---

### Option 5: Security Hardening (Best for Security / Regulated Industries)
```
Enforced OWASP-oriented security across input AND content boundaries: 97
automated tests (43 input validation + LLM01 suite). Six enforcing controls —
ExfilCheck demoted to advisory after an audit showed fixture-shaped matching
(count went 7→6 on purpose). Happy-path research→save works; mutation matrix
covers each enforcing control; benign corpus heuristic FP 0/40. Spotlighting
ASR 12/20→1/20 on qwen3.5:9b (McNemar p≈0.00098); policy 1→0 not demonstrable
at n=20. Multi-hop follow of scraped links blocked by design. Cleared
GHSA-2ww3-72rp-wpp4 (SK 1.78.0). Defense-in-depth — not injection-proof.
```
**Why it works**: Shows security posture with honest demotion, FP rate, and
measured soft-control effect — without overclaiming.

---

### Option 6: Local AI & Privacy (Best for Privacy-Sensitive Roles)
```
Designed privacy-first architecture using Ollama for local LLM inference
(qwen3.5:9b for chat, nomic-embed-text for embeddings), eliminating external
API dependencies and enabling zero-cost, on-premises AI deployment suitable
for data sovereignty requirements.
```
**Why it works**: Addresses compliance, cost, and modern AI infrastructure
concerns without overstating capabilities.

---

## Recommended Combination (3 bullets)

**For AI Engineer / Backend roles at Microsoft, Rubrik, Datadog:**

1. **Architecture bullet** (Option 1) — system design
2. **Security bullet** (Option 5) — security posture with a real CVE fix
3. **RAG/Vector or AOT bullet** (Option 3 or 4) — data infrastructure or
   performance, whichever the JD emphasises

---

## Leadership / Impact Bullets (Only if true)

Use these ONLY if you actually did these things:

```
• Presented autonomous agent capabilities explaining ReAct planning, vector
  embeddings, and Native AOT optimizations to [N] peers — [replace N with real count].
```
```
• Published technical write-up on Microsoft Semantic Kernel agent patterns
  [link if public] — [replace view count with real figure if verifiable].
```

**Do not use placeholder counts** ("20+ peers", "500+ views") unless personally verified.

---

## Skills Section Additions

**Technical Skills**:
- Microsoft Semantic Kernel
- Ollama / Local LLM Inference
- Vector Databases (Qdrant, HNSW indexing)
- RAG (Retrieval Augmented Generation)
- Native AOT Compilation (.NET)
- Docker / Multi-stage Containerisation
- Agent Frameworks / ReAct Pattern
- Embeddings / Semantic Search
- NuGet Security Auditing

**Updated existing skills**:
- C# → **C# (.NET 10, Async/Await, Native AOT)**
- APIs → **RESTful APIs, Semantic Function Calling**
- Databases → **SQL, NoSQL, Vector Databases (Qdrant)**

---

## Project Description (Resume / LinkedIn)

**Short Version (Resume)**:
```
Autonomous AI research agent using Microsoft Semantic Kernel with auto function
calling, Qdrant vector memory (RAG p95 ≈ 23 ms), 43 security unit tests, and
Native AOT — startup ~52% faster than JIT, peak RAM ~62% lower.
Containerised: 51.2 MB Docker image, STARTUP_MS=23.
```

**Long Version (LinkedIn)**:
```
NeuroSearch is an autonomous AI research agent implementing the ReAct pattern
for multi-step planning. Built with Microsoft Semantic Kernel and .NET 10, it
dynamically invokes custom C# plugins (WebSearch, WebScraper, VectorMemory)
based on natural language intent.

Measured results (see MEASUREMENTS.txt):
• Qdrant HNSW search: p95 0.93 ms @ 10K / 5.3 ms @ 100K vectors (synthetic)
• End-to-end RAG (Ollama embed + Qdrant search): E2E p95 ≈ 23 ms, warm model
• Native AOT (macOS arm64): startup 48 ms vs 99 ms JIT (~52% faster);
  peak RSS 24 MB vs 64 MB (~62% less)
• Docker image: 51.2 MB (linux-arm64, runtime-deps:chiseled), STARTUP_MS=23
• 43 security unit tests (SSRF, SQL injection, XSS, rate limiting) — 43/43
• Audit-as-error NuGet gate: build fails on any moderate+ advisory

All numbers are reproducible; commands in MEASUREMENTS.txt §8.
```

---

## GitHub README Tagline

```markdown
> Built with Semantic Kernel, Qdrant vector memory, Ollama local inference,
> and Native AOT — every performance claim is measured and logged in MEASUREMENTS.txt.
```

---

## Quality Checklist

Before submitting your resume, verify:
- [ ] Every metric has a matching entry in MEASUREMENTS.txt
- [ ] Technology names are capitalised correctly (Semantic Kernel, Qdrant, Ollama)
- [ ] Each bullet is 1–2 lines max (not paragraphs)
- [ ] Action verbs are past tense ("Benchmarked", not "Benchmarking")
- [ ] No placeholder counts in "peers" or "blog views" unless you can verify them
- [ ] No numbers from the old RESUME_BULLETS.md (3.2s→310ms, 205MB→82MB, <50ms @ 100K)
