#!/usr/bin/env bash

set -e

echo "Starting Azure DevOps Agent (On-Demand Mode)"

if [ -z "$AZP_URL" ]; then
  echo "error: missing AZP_URL environment variable"
  exit 1
fi

if [ -z "$AZP_TOKEN" ]; then
  echo "error: missing AZP_TOKEN environment variable"
  exit 1
fi

if [ -z "$AZP_POOL" ]; then
  echo "error: missing AZP_POOL environment variable"
  exit 1
fi

if [ -z "$AZP_AGENT_NAME" ]; then
  AZP_AGENT_NAME=$(hostname)
fi

echo "Agent Configuration:"
echo "   Agent Name: $AZP_AGENT_NAME"
echo "   Organization URL: $AZP_URL"
echo "   Pool: $AZP_POOL"
echo "   Mode: On-demand (--once)"

echo "Configuring Azure Pipelines agent..."

# Configure the agent
if ./config.sh \
  --unattended \
  --agent "$AZP_AGENT_NAME" \
  --url "$AZP_URL" \
  --auth PAT \
  --token "$AZP_TOKEN" \
  --pool "$AZP_POOL" \
  --work "_work" \
  --replace \
  --acceptTeeEula; then
    echo "Agent configured"
  else
    echo "Agent configuration failed"
fi

echo "Agent version: $(./bin/Agent.Listener --version 2>/dev/null || echo 'unknown')"

./run-docker.sh --once