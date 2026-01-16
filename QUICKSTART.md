# NeuroSearch Agent - Quick Start Commands

## Current Installation Status

### ✅ Completed
- Docker (v28.4.0)
- Ollama app downloaded and extracted to `/Applications/Ollama.app`

### 🔄 In Progress  
- .NET SDK (waiting for password)
- Ollama symlink setup (waiting for password)

---

## Step 1: Complete Password Prompts

You have 2 terminal commands waiting for your password. Please check your terminal and enter your password for:

1. `.NET SDK installation` - Enter password when prompted
2. `Ollama installation` - Enter password when prompted

---

## Step 2: Verify Installations

After entering passwords, verify everything is installed:

```bash
# Check .NET
dotnet --version
# Should show: 10.0.102 or similar

# Check Ollama (use full path if symlink failed)
/Applications/Ollama.app/Contents/Resources/ollama --version
# Should show: ollama version 0.x.x

# Check Docker
docker --version
# Should show: Docker version 28.4.0
```

---

## Step 3: Start Ollama Service

Open a new terminal and run:

```bash
# Option 1: Using the app
open /Applications/Ollama.app

# Option 2: Using CLI (in terminal, keep it running)
/Applications/Ollama.app/Contents/Resources/ollama serve
```

Keep this terminal open - Ollama needs to run in the background!

---

## Step 4: Download AI Models

In a NEW terminal window:

```bash
# Download Llama 3 (main LLM) - ~4.7GB
/Applications/Ollama.app/Contents/Resources/ollama pull llama3:8b

# Download embedding model - ~46MB
/Applications/Ollama.app/Contents/Resources/ollama pull all-minilm

# Verify models are downloaded
/Applications/Ollama.app/Contents/Resources/ollama list
```

This will take 5-10 minutes depending on your internet speed.

---

## Step 5: Start Qdrant Vector Database

```bash
cd "/Users/preethamdandu/Desktop/c# and .net projects/NeuroSearch Agent"
docker-compose up -d

# Verify Qdrant is running
curl http://localhost:6333/healthz
# Should return: {"title":"healthz OK","version":"1.x.x"}
```

---

## Step 6: Set Up API Key

```bash
cd "/Users/preethamdandu/Desktop/c# and .net projects/NeuroSearch Agent"

# Copy environment template
cp .env.example .env

# Edit .env file and add your Serper API key
# Get free key from: https://serper.dev/signup
nano .env  # or use any text editor
```

In the `.env` file, replace `your_serper_api_key_here` with your actual API key.

---

## Step 7: Build the Project

```bash
cd "/Users/preethamdandu/Desktop/c# and .net projects/NeuroSearch Agent"

# Restore NuGet packages and build
dotnet build

# This will download all dependencies and compile the project
```

---

## Step 8: Run NeuroSearch Agent!

```bash
dotnet run --project src/NeuroSearch.Agent
```

You should see the ASCII banner and prompt:
```
╔═══════════════════════════════════════════════════════════════╗
║                    NEURO SEARCH AGENT                         ║
╚═══════════════════════════════════════════════════════════════╝

🤖 Ready! Type your research query (or 'exit' to quit):

You: _
```

---

## Example Queries to Try

1. **Simple search**:
   ```
   Find the latest AI news
   ```

2. **Multi-step with memory**:
   ```
   Research Tesla Optimus robot and save key features to memory
   ```

3. **Memory recall**:
   ```
   What did we save about Tesla robots?
   ```

---

## Troubleshooting

### Ollama not found
If `ollama` command doesn't work, use the full path:
```bash
/Applications/Ollama.app/Contents/Resources/ollama [command]
```

Or create an alias in `~/.zshrc`:
```bash
alias ollama='/Applications/Ollama.app/Contents/Resources/ollama'
```

### .NET not found after installation
Close and reopen your terminal, or run:
```bash
export PATH="$PATH:/usr/local/share/dotnet"
```

### Qdrant connection failed
```bash
# Restart Qdrant
docker-compose down
docker-compose up -d
```

### Build errors
Make sure you're in the project directory:
```bash
cd "/Users/preethamdandu/Desktop/c# and .net projects/NeuroSearch Agent"
dotnet clean
dotnet build
```

---

## Once Everything Works

After successful run:
1. Test with example queries
2. Measure performance (it shows timing after each query)
3. Update resume with actual metrics
4. Record a demo for your portfolio!

---

**Next**: Once you've entered the passwords and verified installations, let me know and I'll help you test the agent!
