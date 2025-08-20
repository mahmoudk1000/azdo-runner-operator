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

# Get capability info from environment or runtime type
CAPABILITY=${AZP_CAPABILITY:-${RUNTIME_TYPE:-"base"}}
echo "Agent Capability: $CAPABILITY"

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
echo "   Capability: $CAPABILITY"
echo "   Runtime Type: ${RUNTIME_TYPE:-base}"
echo "   Mode: $RUN_MODE"

# Function to add capabilities from file
add_capabilities_from_file() {
  if [ -f "/azp/capabilities.txt" ]; then
    echo "Adding capabilities from capabilities.txt:"
    while IFS='=' read -r cap_name cap_value; do
      if [ -n "$cap_name" ] && [ -n "$cap_value" ]; then
        AGENT_CONFIG_ARGS+=(--addcapability "$cap_name" "$cap_value")
        echo "   $cap_name=$cap_value"
      fi
    done < /azp/capabilities.txt
  fi
}

echo "Configuring Azure Pipelines agent..."

# Build agent configuration with capability labels
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

# Add primary capability if it's not 'base'
if [ "$CAPABILITY" != "base" ]; then
  AGENT_CONFIG_ARGS+=(--addcapability "$CAPABILITY" "true")
  echo "   Adding primary capability: $CAPABILITY=true"
fi

# Add runtime-specific capabilities
add_capabilities_from_file

# Add general capabilities
AGENT_CONFIG_ARGS+=(--addcapability "capability-aware" "true")
AGENT_CONFIG_ARGS+=(--addcapability "azdo-runner-operator" "true")

echo "   Adding capability: capability-aware=true"
echo "   Adding capability: azdo-runner-operator=true"

# Configure the agent
echo "Running agent configuration..."
if ./config.sh "${AGENT_CONFIG_ARGS[@]}"; then
    echo "Agent configured successfully"
  else
    echo "Agent configuration failed"
    exit 1
fi

echo "Agent version: $(./bin/Agent.Listener --version 2>/dev/null || echo 'unknown')"

# Show registered capabilities for debugging
echo "Registered capabilities:"
if [ -f ".agent" ]; then
  grep -i "usercapabilities" .agent || echo "No user capabilities found"
fi

# Run agent based on mode
if [ "$RUN_MODE" = "once" ]; then
  echo "Running agent in one-time mode (--once)"
  ./run-docker.sh --once
else
  echo "Running agent in continuous mode"
  ./run-docker.sh
fi