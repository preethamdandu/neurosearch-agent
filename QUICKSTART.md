# NeuroSearch Agent — Quick start

Short path to run the agent. For platform-specific detail, use **[SETUP.md](./SETUP.md)**.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker Desktop](https://www.docker.com/products/docker-desktop) (for Qdrant)
- [Ollama](https://ollama.com) with model `llama3:8b` (optional: embedding model for memory features)
- [Serper](https://serper.dev) API key (web search)

## 1. Repository root

Open a terminal in the **root of this repository** (the folder that contains `NeuroSearch.slnx` and `docker-compose.yml`).

## 2. Start Qdrant

```bash
docker-compose up -d
curl -s http://localhost:6333/healthz
```

## 3. Configure environment

```bash
cp .env.example .env
```

Edit `.env` and set `SERPER_API_KEY`. Never commit `.env` (it is listed in `.gitignore`).

## 4. Ollama

Install Ollama from the official site, then pull and run the model (command names may differ if you use the desktop app only):

```bash
ollama pull llama3:8b
ollama serve
```

Keep Ollama running while you use the agent.

## 5. Build and run

```bash
dotnet build
dotnet run --project src/NeuroSearch.Agent
```

## Optional helper scripts

From the repository root (after `chmod +x test-agent.sh test-phase2.sh` on macOS/Linux):

- `./test-agent.sh` — minimal stdin smoke test  
- `./test-phase2.sh` — longer multi-step reasoning smoke test  

## Troubleshooting

- **Qdrant**: `docker-compose down` then `docker-compose up -d`  
- **Ollama**: confirm `http://localhost:11434` responds while `ollama serve` is running  
- **Full install paths and OS notes**: [SETUP.md](./SETUP.md)
