# NeuroSearch Agent — multi-stage Native AOT container
#
# Build stage: installs clang (required by .NET Native AOT linker on Linux),
# then publishes a stripped linux-arm64 AOT binary.
#
# Runtime stage: mcr.microsoft.com/dotnet/nightly/runtime-deps:10.0-noble-chiseled
#   — minimal Ubuntu Noble Chiseled image that satisfies Native AOT glibc deps.
#   Contains no SDK, no .NET runtime, no shell — just libc and ld.
#
# Build:
#   docker build -t neurosearch-agent:aot .
#
# Startup benchmark (connect host network so Ollama/Qdrant are reachable):
#   docker run --rm --network host neurosearch-agent:aot --startup-benchmark
#
# NOTE: This container is NOT deployed to Azure. Containerising ≠ deploying.

FROM --platform=linux/arm64 mcr.microsoft.com/dotnet/sdk:10.0 AS build

# Install clang — required by the .NET Native AOT ILCompiler on Linux
RUN apt-get update && apt-get install -y --no-install-recommends clang zlib1g-dev && rm -rf /var/lib/apt/lists/*

WORKDIR /src

# Copy project files first for layer caching
COPY src/NeuroSearch.Core/NeuroSearch.Core.csproj        ./NeuroSearch.Core/
COPY src/NeuroSearch.Plugins/NeuroSearch.Plugins.csproj  ./NeuroSearch.Plugins/
COPY src/NeuroSearch.Agent/NeuroSearch.Agent.csproj       ./NeuroSearch.Agent/
COPY Directory.Build.props .

# Restore (cached if project files unchanged)
RUN dotnet restore ./NeuroSearch.Agent/NeuroSearch.Agent.csproj -r linux-arm64 --no-cache

# Copy full source
COPY src/NeuroSearch.Core/    ./NeuroSearch.Core/
COPY src/NeuroSearch.Plugins/ ./NeuroSearch.Plugins/
COPY src/NeuroSearch.Agent/   ./NeuroSearch.Agent/

# Publish Native AOT with symbols stripped
# On Linux there is no .dSYM; StripSymbols removes debug info from the binary itself.
RUN dotnet publish ./NeuroSearch.Agent/NeuroSearch.Agent.csproj \
    -c Release \
    -r linux-arm64 \
    --self-contained true \
    /p:PublishAot=true \
    /p:StripSymbols=true \
    -o /app/publish

# ── runtime stage ──────────────────────────────────────────────────────────────
FROM --platform=linux/arm64 mcr.microsoft.com/dotnet/nightly/runtime-deps:10.0-noble-chiseled AS final

WORKDIR /app

# Copy only the stripped AOT binary and config — no SDK, no PDBs
COPY --from=build /app/publish/NeuroSearch.Agent   .
COPY --from=build /app/publish/appsettings.json    .

ENV DOTNET_EnableDiagnostics=0

ENTRYPOINT ["/app/NeuroSearch.Agent"]
