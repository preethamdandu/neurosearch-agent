#!/usr/bin/env bash
# Phase 2: Cognitive stress test — multi-step reasoning smoke test
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR/src/NeuroSearch.Agent"

echo "=== PHASE 2: COGNITIVE STRESS TEST ==="
echo ""
echo "Test 2.1: Multi-step reasoning chain"
echo "Expected: multiple search calls and a coherent final answer"
echo ""
echo "Starting agent..."
echo ""

echo "Who is the CEO of the company that created the C# language? Find out where he went to college, and then tell me the mascot of that college." | dotnet run

echo ""
echo "=== TEST COMPLETE ==="
echo "Review console output for multiple search invocations and a correct final answer."
