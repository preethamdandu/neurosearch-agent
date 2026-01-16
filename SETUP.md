# NeuroSearch Agent - Environment Setup Guide

This guide will help you set up all required dependencies for the NeuroSearch Agent project.

---

## Step 1: Install .NET 10 SDK

### macOS

```bash
# Option 1: Using Homebrew (Recommended)
brew install --cask dotnet-sdk

# Verify installation
dotnet --version  # Should show 10.x.x
```

**Alternative: Direct Download**
1. Visit: https://dotnet.microsoft.com/download/dotnet/10.0
2. Download the macOS installer (choose ARM64 for Apple Silicon or x64 for Intel)
3. Run the installer package
4. Open a new terminal and verify: `dotnet --version`

---

## Step 2: Install Docker Desktop

### macOS

```bash
# Option 1: Using Homebrew
brew install --cask docker

# Option 2: Direct Download
# Visit: https://www.docker.com/products/docker-desktop
# Download and install Docker Desktop for Mac
```

**After Installation:**
1. Open Docker Desktop from Applications
2. Wait for the Docker engine to start (whale icon in menu bar)
3. Verify: `docker --version`

---

## Step 3: Install Ollama (Local AI)

### macOS

```bash
# Using Homebrew
brew install ollama

# Start Ollama service (in a terminal, leave it running)
ollama serve
```

**Download AI Models** (in a new terminal):

```bash
# Option 1: Llama-3-8B (Recommended for powerful machines)
ollama pull llama3:8b

# Option 2: Phi-4 (Recommended for laptops/lower RAM)
ollama pull phi4

# Download embedding model (required for vector search)
ollama pull all-minilm
```

**Test Ollama:**
```bash
ollama run llama3:8b "What is an AI agent?"
# Should generate a response
```

---

## Step 4: Get API Keys

### Serper.dev (Web Search)

1. Visit: https://serper.dev/signup
2. Sign up for a free account (2,500 searches/month)
3. Copy your API key from the dashboard
4. In the project directory, create `.env` file:
   ```bash
   cp .env.example .env
   ```
5. Edit `.env` and add your key:
   ```
   SERPER_API_KEY=your_actual_api_key_here
   ```

**Alternative: Bing Search API**
- If you prefer Microsoft's Bing Search, sign up at Azure Cognitive Services
- Update `WebSearchPlugin.cs` to use Bing endpoints

---

## Step 5: Initialize the .NET Project

**Once .NET SDK is installed**, run these commands in the project directory:

```bash
# Create solution
dotnet new sln -n NeuroSearch

# Create projects
dotnet new console -n NeuroSearch.Agent -o src/NeuroSearch.Agent
dotnet new classlib -n NeuroSearch.Plugins -o src/NeuroSearch.Plugins
dotnet new classlib -n NeuroSearch.Core -o src/NeuroSearch.Core
dotnet new xunit -n NeuroSearch.Tests -o tests/NeuroSearch.Tests

# Add projects to solution
dotnet sln add src/NeuroSearch.Agent/NeuroSearch.Agent.csproj
dotnet sln add src/NeuroSearch.Plugins/NeuroSearch.Plugins.csproj
dotnet sln add src/NeuroSearch.Core/NeuroSearch.Core.csproj
dotnet sln add tests/NeuroSearch.Tests/NeuroSearch.Tests.csproj

# Add project references
dotnet add src/NeuroSearch.Agent reference src/NeuroSearch.Plugins
dotnet add src/NeuroSearch.Agent reference src/NeuroSearch.Core
dotnet add src/NeuroSearch.Plugins reference src/NeuroSearch.Core

# Install NuGet packages - Agent
cd src/NeuroSearch.Agent
dotnet add package Microsoft.SemanticKernel --version 1.32.0
dotnet add package Microsoft.SemanticKernel.Connectors.Qdrant --version 1.32.0-alpha
dotnet add package Microsoft.SemanticKernel.Connectors.Ollama --version 1.32.0-alpha
dotnet add package Microsoft.Extensions.Configuration
dotnet add package Microsoft.Extensions.Configuration.Json
dotnet add package Microsoft.Extensions.Configuration.EnvironmentVariables
dotnet add package Microsoft.Extensions.Logging.Console

# Install NuGet packages - Plugins
cd ../NeuroSearch.Plugins
dotnet add package HtmlAgilityPack
dotnet add package System.Net.Http.Json

# Return to project root
cd ../..
```

---

## Step 6: Start Qdrant Vector Database

```bash
# From project directory
docker-compose up -d

# Verify Qdrant is running
curl http://localhost:6333/healthz
# Should return: {"title":"healthz OK","version":"1.x.x"}

# View Qdrant dashboard (optional)
open http://localhost:6333/dashboard
```

---

## Step 7: Build and Run

```bash
# Build the entire solution
dotnet build

# Run the agent
dotnet run --project src/NeuroSearch.Agent

# You should see:
# 🤖 NeuroSearch Agent Ready!
# Type your research query (or 'exit' to quit):
```

---

## Troubleshooting

### Issue: `dotnet: command not found`
**Solution:** Restart your terminal after installing .NET SDK, or add to PATH manually:
```bash
export PATH="$PATH:/usr/local/share/dotnet"
```

### Issue: Ollama connection refused
**Solution:** Make sure `ollama serve` is running in a separate terminal window.

### Issue: Docker containers won't start
**Solution:** 
1. Ensure Docker Desktop is running
2. Check port conflicts: `lsof -i :6333`
3. Try: `docker-compose down && docker-compose up -d`

### Issue: Qdrant connection errors
**Solution:**
```bash
# Check container logs
docker logs neurosearch-qdrant

# Restart Qdrant
docker-compose restart qdrant
```

---

## System Requirements

| Component | Minimum | Recommended |
|-----------|---------|-------------|
| **RAM** | 8 GB | 16 GB+ |
| **Storage** | 10 GB | 20 GB+ |
| **macOS** | 11 (Big Sur) | 13+ (Ventura) |
| **Processor** | Intel i5 / Apple M1 | Intel i7 / Apple M2+ |

**Note:** Llama-3-8B requires ~6GB RAM. Use Phi-4 (4GB) for lower-spec machines.

---

## Next Steps

After setup is complete:
1. Review the implementation plan: `implementation_plan.md`
2. Explore the codebase structure
3. Run example queries to test the agent
4. Customize plugins for your use case

**Happy Building! 🚀**
