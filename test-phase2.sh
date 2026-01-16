#!/bin/bash
# Phase 2: Cognitive Stress Test - Multi-Step Reasoning
# The Rubrik/Microsoft Interview Question

cd "/Users/preethamdandu/Desktop/c# and .net projects/NeuroSearch Agent/src/NeuroSearch.Agent"

echo "=== PHASE 2: COGNITIVE STRESS TEST ==="
echo ""
echo "Test 2.1: Multi-Step Reasoning Chain"
echo "Expected: 3+ search calls, correct answer (Pounce the Panther)"
echo ""
echo "Starting agent..."
echo ""

# The multi-step question
echo "Who is the CEO of the company that created the C# language? Find out where he went to college, and then tell me the mascot of that college." | dotnet run

echo ""
echo "=== TEST COMPLETE ==="
echo "Review console output for:"
echo "1. Multiple [🔍 SearchPlugin] calls"
echo "2. Reasoning chain visible"
echo "3. Correct final answer"
