#!/usr/bin/env bash
# Measure NeuroSearch agent cold-start + RSS for JIT vs Native AOT publishes.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

RID="${RID:-osx-arm64}"
ARTIFACTS="$ROOT/artifacts"
JIT_OUT="$ARTIFACTS/jit"
AOT_OUT="$ARTIFACTS/aot"
LOG="$ROOT/MEASUREMENTS_RAW.log"

mkdir -p "$ARTIFACTS"
: > "$LOG"

echo "=== NeuroSearch Cold Start / Memory Measurement ===" | tee -a "$LOG"
echo "UTC=$(date -u +%Y-%m-%dT%H:%M:%SZ)" | tee -a "$LOG"
echo "HOST=$(hostname)" | tee -a "$LOG"
echo "DOTNET=$(dotnet --version)" | tee -a "$LOG"
echo "RID=$RID" | tee -a "$LOG"
echo "ARCH=$(uname -m)" | tee -a "$LOG"
echo | tee -a "$LOG"

measure_binary() {
  local label="$1"
  local bin="$2"
  local runs="${3:-5}"
  local workdir
  workdir="$(dirname "$bin")"

  if [[ ! -x "$bin" ]]; then
    echo "${label}_ERROR=binary_not_found path=$bin" | tee -a "$LOG"
    return 1
  fi

  local size_bytes
  size_bytes=$(stat -f%z "$bin" 2>/dev/null || stat -c%s "$bin")
  echo "${label}_MAIN_BINARY_BYTES=$size_bytes" | tee -a "$LOG"
  echo "${label}_PUBLISH_DIR_BYTES=$(du -sk "$workdir" | awk '{print $1*1024}')" | tee -a "$LOG"

  # Warm once from the publish directory
  (cd "$workdir" && "$bin" --startup-benchmark >/dev/null 2>&1) || true

  local times=()
  local mems=()
  for ((i=1; i<=runs; i++)); do
    local tmp_log
    tmp_log=$(mktemp)

    set +e
    # macOS: /usr/bin/time -l reports max RSS in bytes on stderr
    (cd "$workdir" && /usr/bin/time -l "$bin" --startup-benchmark) >"$tmp_log" 2>"${tmp_log}.time"
    local rc=$?
    set -e

    local startup_ms
    startup_ms=$(grep -E '^STARTUP_MS=' "$tmp_log" | tail -1 | cut -d= -f2 || true)
    local ready
    ready=$(grep -E '^READY$' "$tmp_log" || true)
    local rss
    rss=$(grep -E 'maximum resident set size' "${tmp_log}.time" | awk '{print $1}' || echo "0")

    echo "${label}_RUN${i}_STARTUP_MS=${startup_ms:-MISSING}" | tee -a "$LOG"
    echo "${label}_RUN${i}_READY=${ready:-NO}" | tee -a "$LOG"
    echo "${label}_RUN${i}_MAX_RSS_BYTES=${rss}" | tee -a "$LOG"
    echo "${label}_RUN${i}_EXIT=${rc}" | tee -a "$LOG"

    if [[ -n "${startup_ms:-}" ]]; then
      times+=("$startup_ms")
    fi
    mems+=("$rss")

    rm -f "$tmp_log" "${tmp_log}.time"
  done

  if ((${#times[@]} > 0)); then
    local sum=0
    local min=${times[0]}
    local max=${times[0]}
    for t in "${times[@]}"; do
      sum=$((sum + t))
      (( t < min )) && min=$t
      (( t > max )) && max=$t
    done
    local avg=$((sum / ${#times[@]}))
    echo "${label}_STARTUP_AVG_MS=${avg}" | tee -a "$LOG"
    echo "${label}_STARTUP_MIN_MS=${min}" | tee -a "$LOG"
    echo "${label}_STARTUP_MAX_MS=${max}" | tee -a "$LOG"
  fi

  if ((${#mems[@]} > 0)); then
    local msum=0
    for m in "${mems[@]}"; do msum=$((msum + m)); done
    local mavg=$((msum / ${#mems[@]}))
    echo "${label}_MAX_RSS_AVG_BYTES=${mavg}" | tee -a "$LOG"
    echo "${label}_MAX_RSS_AVG_MB=$(python3 -c "print(round($mavg/1024/1024, 2))")" | tee -a "$LOG"
  fi
}

echo "--- Publishing JIT (PublishAot=false, self-contained) ---" | tee -a "$LOG"
dotnet publish "$ROOT/src/NeuroSearch.Agent/NeuroSearch.Agent.csproj" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -o "$JIT_OUT" \
  /p:PublishAot=false \
  /p:PublishSingleFile=false \
  2>&1 | tee -a "$LOG" | tail -15

echo | tee -a "$LOG"
echo "--- Publishing Native AOT (PublishAot=true) ---" | tee -a "$LOG"
set +e
dotnet publish "$ROOT/src/NeuroSearch.Agent/NeuroSearch.Agent.csproj" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -o "$AOT_OUT" \
  /p:PublishAot=true \
  2>&1 | tee "$ROOT/AOT_PUBLISH.log" | tail -20
AOT_RC=${PIPESTATUS[0]}
set -e
echo "AOT_PUBLISH_EXIT=$AOT_RC" | tee -a "$LOG"

echo | tee -a "$LOG"
echo "--- Measuring JIT ---" | tee -a "$LOG"
measure_binary "JIT" "$JIT_OUT/NeuroSearch.Agent" 5 || true

if [[ "$AOT_RC" -eq 0 ]]; then
  echo | tee -a "$LOG"
  echo "--- Measuring AOT ---" | tee -a "$LOG"
  measure_binary "AOT" "$AOT_OUT/NeuroSearch.Agent" 5 || true
else
  echo "AOT_MEASURE_SKIPPED=true reason=publish_failed see=AOT_PUBLISH.log" | tee -a "$LOG"
fi

echo | tee -a "$LOG"
echo "RAW_LOG=$LOG" | tee -a "$LOG"
echo "Done."
