#!/usr/bin/env bash

set -e

# Determine run mode based on arguments
RUN_MODE="continuous"
if [ "$1" = "--once" ]; then
  RUN_MODE="once"
  echo "Starting Azure DevOps Agent (One-time Mode)"
else
  echo "Starting Azure DevOps Agent (Continuous Mode)"
fi

CAPABILITY=${AZP_CAPABILITY:-${RUNTIME_TYPE:-"base"}}
echo "Agent Capability: $CAPABILITY"

echo "Agent Configuration:"
echo "   Agent Name: $AZP_AGENT_NAME"
echo "   Organization URL: $AZP_URL"
echo "   Pool: $AZP_POOL"
echo "   Capability: $CAPABILITY"
echo "   Runtime Type: ${RUNTIME_TYPE:-base}"
echo "   Mode: $RUN_MODE"

add_capabilities_from_file() {
  if [ -f "/azp/capabilities.txt" ]; then
    echo "Setting environment variables for capabilities from capabilities.txt:"
    while IFS='=' read -r cap_name cap_value; do
      if [ -n "$cap_name" ] && [ -n "$cap_value" ]; then
        export "$cap_name"="$cap_value"
        echo "   Set environment variable: $cap_name=$cap_value"
      fi
    done < /azp/capabilities.txt
  fi
}

echo "Configuring Azure Pipelines agent..."

AGENT_CONFIG_ARGS=(
  --unattended
  --agent "$AZP_AGENT_NAME"
  --url "$AZP_URL"
  --auth PAT
  --token "$AZP_TOKEN"
  --pool "$AZP_POOL"
  --work "_work"
  --replace
  --acceptTeeEula
)

if [ "$CAPABILITY" != "base" ]; then
  export "$CAPABILITY"="true"
  echo "   Set environment variable: $CAPABILITY=true"
fi

add_capabilities_from_file

echo "Running agent configuration..."
if ./config.sh "${AGENT_CONFIG_ARGS[@]}"; then
    echo "Agent configured successfully"
  else
    echo "Agent configuration failed"
    exit 1
fi

echo "Agent version: $(./bin/Agent.Listener --version 2>/dev/null || echo 'unknown')"

if [ "$RUN_MODE" = "once" ]; then
  echo "Running agent in one-time mode (--once)"
  ./run-docker.sh --once
else
  echo "Running agent in continuous mode"
  ./run-docker.sh
fi