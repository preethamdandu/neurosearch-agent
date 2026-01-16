# NeuroSearch Agent - Example Queries

This document contains example queries to test your NeuroSearch Agent's capabilities across different scenarios.

---

## 🎯 Quick Start Examples

### Basic Web Search
```
Find the latest news about SpaceX Starship launches
```

### Research + Memory
```
Research the top 3 AI trends in 2026 and save the insights to memory
```

### Memory Recall
```
What AI trends did we save earlier?
```

---

## 📊 Interview Demo Scenarios

### Scenario 1: Stock Market Research
**Objective**: Demonstrate multi-step planning (Search → Scrape → Summarize)

```
Research Tesla's stock performance in 2026 and provide a summary of key factors affecting the price
```

**Expected Behavior**:
1. Agent searches for "Tesla stock 2026"
2. Agent scrapes top article(s)
3. Agent synthesizes information
4. Agent provides structured summary

---

### Scenario 2: Long-Term Memory (RAG)
**Objective**: Show semantic memory storage and retrieval

**Step 1 - Save Information**:
```
Research Microsoft Azure's new AI services announced in 2026 and save the important details to memory
```

**Step 2 - Later Recall** (in new session or after other queries):
```
What Azure AI services did we learn about?
```

**Expected Behavior**:
- Vector similarity search finds relevant memories
- Agent synthesizes response from stored embeddings
- Demonstrates <50ms retrieval latency

---

### Scenario 3: Deep Dive Research
**Objective**: Complex multi-source information gathering

```
Compare the latest features of OpenAI GPT-5 and Anthropic Claude 4. Save a comparison table to memory.
```

**Expected Behavior**:
1. Search for GPT-5 features
2. Search for Claude 4 features
3. Potentially scrape official documentation
4. Create structured comparison
5. Save to vector memory

---

### Scenario 4: Technical Documentation Research
**Objective**: Extract specific technical information

```
Find documentation on Microsoft Semantic Kernel's function calling feature and explain how it works
```

**Expected Behavior**:
- Search for official documentation
- Scrape relevant pages
- Extract technical explanations
- Provide clear summary

---

## 🔥 Advanced Use Cases

### Industry Intelligence
```
Track recent acquisitions in the cybersecurity industry and identify emerging trends
```

### Technology Comparison
```
Compare Llama 3 vs GPT-4 vs Claude 3 for coding tasks. Which one is best for C# development?
```

### Market Analysis
```
Research Rubrik's latest product announcements and competitive positioning against Veeam
```

### Technical Learning
```
Explain vector databases and why they're important for AI applications. Save key concepts to memory.
```

---

## 🧪 Testing Edge Cases

### Empty Results
```
Search for "xyzabc123nonexistent999"
```
**Expected**: Graceful handling with "No results found" message

### URL Scraping Errors
```
Scrape https://this-url-does-not-exist-at-all.com
```
**Expected**: Error message "Failed to fetch URL"

### Memory Search (No Matches)
```
Search memory for "quantum computing unicorns"
```
**Expected**: "No relevant memories found" (if topic never researched)

---

## 🎓 Resume/Interview Talking Points

### Query to Highlight Planning
```
Research the latest cloud computing trends, scrape the top article, and save a summary to long-term memory for future reference
```

**What to Mention**:
- "The agent automatically broke down my request into 3 function calls"
- "Uses ReAct pattern: Reason about the task, then Act by calling C# functions"
- "FunctionCallingStepwisePlanner orchestrated the workflow"

### Query to Highlight RAG
```
What did we learn about cloud computing trends?
```

**What to Mention**:
- "Vector similarity search using Qdrant's HNSW algorithm"
- "Sub-50ms retrieval across thousands of embeddings"
- "Semantic search: finds relevant memories even with different wording"

### Query to Highlight Local Inference
```
Compare local AI models: Llama 3 vs Phi-4 for privacy-sensitive enterprise use
```

**What to Mention**:
- "100% local inference - no data sent to external APIs"
- "Ollama endpoint on localhost:11434"
- "Privacy-first architecture suitable for regulated industries"

---

## 🚀 Performance Benchmarks

Use these queries to measure performance for your portfolio:

### Latency Test
```
What is 2+2?
```
**Measure**: Total response time (should be <1s for simple queries)

### Multi-Step Test
```
Search for AI news, scrape the first result, summarize it
```
**Measure**: 
- Planning time (~500ms)
- Total execution time (<5s for 3-step plan)

### Memory Write/Read Test
```
Save "NeuroSearch is an autonomous AI agent" to memory
```
Then:
```
Search memory for "autonomous agent"
```
**Measure**: Round-trip latency (embedding + storage + retrieval)

---

## 💡 Tips for Demos

1. **Start Simple**: Begin with basic search to show it works
2. **Show Planning**: Use queries that require 2-3 steps to highlight the planner
3. **Demonstrate Memory**: Save something, then retrieve it to show RAG
4. **Explain Plugins**: Point out the colored console output showing which plugins are called
5. **Highlight Local**: Mention Ollama running locally, no API costs

---

## 📝 Sample Demo Script

**For a 5-minute interview demo:**

1. **Introduction** (30s):
   - "This is NeuroSearch, an autonomous AI agent with long-term memory"

2. **Basic Query** (1min):
   - `Find the latest SpaceX news`
   - Show console output with plugin calls

3. **Complex Planning** (2min):
   - `Research Tesla Optimus robot and save key facts to memory`
   - Explain ReAct pattern as it executes
   - Point out: Search → Scrape → Save workflow

4. **Memory Recall** (1min):
   - `What did we learn about Tesla robots?`
   - Show vector search in action
   - Mention <50ms retrieval

5. **Architecture Explanation** (30s):
   - Semantic Kernel + Ollama + Qdrant
   - Native AOT for performance
   - Three custom plugins in C#

**Total**: ~5 minutes with buffer for questions

---

**Pro Tip**: Record a GIF/video of the agent in action for your README or LinkedIn!
