
# AzDORunner Operator

AzDORunner Operator is a Kubernetes operator designed to manage self-hosted Azure DevOps runners as custom resources within your Kubernetes cluster. It automates the lifecycle of runner pools, including provisioning, scaling, health checking, and secure webhook management, making it easier to integrate Azure DevOps pipelines with Kubernetes-native infrastructure.

---

## Installation

You can install the AzDORunner Operator using Helm from the official chart repository:

```sh
helm repo add mahmoudk1000 https://mahmoudk1000.github.io/charts/
helm repo update
helm install my-azdo-operator mahmoudk1000/azdo-runner-operator -n azdo-operator --create-namespace
```

Replace `my-azdo-runner` with your desired release name.

For advanced configuration, see the [values.yaml](chart/azdo-runner-operator/values.yaml) file in this repository.

---

## RunnerPool CRD: Capabilities, Configuration, and Usage

## Example: Creating the Azure DevOps PAT Secret

Before deploying a RunnerPool, you need to create a Kubernetes secret containing your Azure DevOps Personal Access Token (PAT). Here is an example manifest:

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: pat-token
  namespace: default
type: Opaque
data:
  token: <base64_token>
```

Replace `<base64_token>` with your PAT encoded in base64. For example, to encode your token:

```sh
echo -n "YOUR_PAT_TOKEN" | base64
```

Then use the output in the `token` field above.

The core of the operator is the RunnerPool CRD, defined in `V1AzDORunnerEntity.cs`. This resource represents a pool of Azure DevOps runners and supports both standard and capability-aware scheduling.

### Spec Fields

| Field                | Type      | Required | Description                                                                               |
|----------------------|-----------|----------|-------------------------------------------------------------------------------------------|
| `AzDoUrl`            | string    | Yes      | Azure DevOps organization or server URL.                                                  |
| `Pool`               | string    | Yes      | Name of the Azure DevOps agent pool.                                                      |
| `PatSecretName`      | string    | Yes      | Name of the Kubernetes secret containing the Azure DevOps PAT.                            |
| `Image`              | string    | Yes      | Default image to use for the runners.                                                     |
| `ImagePullPolicy`    | string    | No       | Image pull policy (`Always`, `IfNotPresent`, `Never`). Default: `IfNotPresent`            |
| `CapabilityAware`    | bool      | No       | If true, runners are scheduled based on required capabilities. Default: false             |
| `CapabilityImages`   | map       | No       | Map of capability name to image override.                                                 |
| `TtlIdleSeconds`     | int       | No       | Time (seconds) to keep idle agents before scaling down. Default: 0                        |
| `MinAgents`          | int       | No       | Minimum number of agents to keep running. Default: 0 (can't be set higher than MaxAgents) |
| `MaxAgents`          | int       | No       | Maximum number of agents. Default: 10, must be at least 1                                 |
| `PollIntervalSeconds`| int       | No       | Polling interval in seconds. Default: 5, minimum: 5                                       |

#### Required Fields

- `AzDoUrl`
- `Pool`
- `PatSecretName`
- `Image`

#### Optional Fields

- `ImagePullPolicy`, `CapabilityAware`, `CapabilityImages`, `TtlIdleSeconds`, `MinAgents`, `MaxAgents`, `PollIntervalSeconds`

### Status Fields

- `ConnectionStatus`: Connection state to Azure DevOps (e.g., Connected, Disconnected)
- `OrganizationName`: Name of the Azure DevOps organization
- `AgentsSummary`: Summary of agents (e.g., "2/3")
- `Active`: Whether the pool is active
- `QueuedJobs`: Number of queued jobs
- `RunningAgents`: Number of running agents
- `LastPolled`: Last time the pool was polled
- `LastError`: Last error message, if any
- `Agents`: List of agent details
- `Conditions`: List of status conditions

---

## Capability-Aware and Standard RunnerPools

### Capability-Aware RunnerPool

When `CapabilityAware` is enabled, the operator will schedule runners based on required capabilities. You can define custom images for each capability using the `CapabilityImages` map. Your Azure DevOps pipeline must specify `demands` that match the capability key.

**Example CRD YAML:**

```yaml
apiVersion: devops.opentools.mf/v1
kind: RunnerPool
metadata:
  name: example-runnerpool
  namespacew: default
spec:
  azDoUrl: https://dev.azure.com/my-org
  pool: AzDO
  patSecretName: pat-token
  image: ghcr.io/mahmoudk1000/azdo-runner-operator/agent:main
  capabilityAware: true
  capabilityImages:
    java: ghcr.io/mahmoudk1000/azdo-runner-operator/agent:latest-java
    nodejs: ghcr.io/mahmoudk1000/azdo-runner-operator/agent:latest-nodejs
    dotnet: ghcr.io/mahmoudk1000/azdo-runner-operator/agent:latest-dotnet
  maxAgents: 5
  minAgents: 0
  ttlIdleSeconds: 60
  pollIntervalSeconds: 10
  imagePullPolicy: IfNotPresent
```

**How it works:**

- The `CapabilityImages` map can hold any key/value pairs. The key is a capability name (e.g., `gpu`, `mykeyword`), and the value is the image to use for agents with that capability.
- When `CapabilityAware` is enabled, your Azure DevOps pipeline **must** set `demands` matching the capability key (e.g., `demands: - mykeyword`) for the correct agent/image to be spawned.

**Example Azure DevOps Pipeline:**

```yaml
pool:
  name: my-azdo-pool
  demands:
    - mykeyword

steps:
  - script: echo "This job will run on an agent with the 'mykeyword' capability, using the image specified in CapabilityImages."
    displayName: Run on custom capability agent
```

**Behavior:**

- The controller schedules runners based on required capabilities (e.g., only on nodes with GPUs).
- Jobs in Azure DevOps that require specific capabilities will be matched to appropriate runners.

### Standard (Non-Capability-Aware) RunnerPool

If `CapabilityAware` is false or omitted, runners are scheduled without regard to node capabilities. Any available runner can pick up any job, regardless of requirements.

**Example CRD YAML:**

```yaml
spec:
  pool: generic-pool
  azDoUrl: my-org-link
  capabilityAware: false
```

**Behavior:**

- Runners are scheduled without regard to node capabilities.
- Any available runner can pick up any job, regardless of requirements.

---

## Operator Architecture & Services

---

## Agents: Agent Factory and Available Agent Images

The AzDORunner Operator uses an agent factory pattern to manage and provision Azure DevOps runner agents dynamically based on the configuration in your RunnerPool CRD. The agent factory is responsible for selecting and launching the appropriate agent image for each job, supporting both standard and capability-aware scheduling.

### Agent Factory Overview

- **Agent Factory**: The operator's agent factory logic determines which container image to use for each runner pod. It supports:
  - **Standard agents**: Use the default image specified in the RunnerPool CRD.
  - **Capability-aware agents**: If `capabilityAware` is enabled, the factory matches Azure DevOps pipeline `demands` to the `capabilityImages` map in the CRD, launching the correct image for each required capability.
- The agent factory ensures that the right environment is provided for each job, whether it requires a generic runner or a specialized one (e.g., with Java, Node.js, or .NET preinstalled).

### Available Agent Images

The repository includes a base agent implementation and supports building custom agent images. The `agent/` directory contains:

- `agent/Dockerfile`: The base Dockerfile for building a generic Azure DevOps agent image.
- `agent/start.sh`: The entrypoint script used by the agent container to register and start the runner.

#### Official and Example Images

You can use the provided Dockerfile to build your own agent image, or use prebuilt images published to container registries. Example images referenced in the CRD examples include:

- `ghcr.io/mahmoudk1000/azdo-runner-operator/agent:main` (generic base agent)
- `ghcr.io/mahmoudk1000/azdo-runner-operator/agent:latest-java` (Java-enabled agent)
- `ghcr.io/mahmoudk1000/azdo-runner-operator/agent:latest-nodejs` (Node.js-enabled agent)
- `ghcr.io/mahmoudk1000/azdo-runner-operator/agent:latest-dotnet` (.NET-enabled agent)

You can extend the base Dockerfile to create your own custom images with additional tools or capabilities as needed.

#### How Agent Selection Works

- For **standard RunnerPools**, all agents use the default image specified in the CRD.
- For **capability-aware RunnerPools**, the agent factory matches the pipeline's `demands` to the `capabilityImages` map and launches the corresponding image.
- If a demand does not match any key in `capabilityImages`, the default image is used.

---

### Architecture Overview

- **Controller**: Watches for changes to custom resources (RunnerPools) and reconciles the desired state with the actual state in the cluster.
- **CRD Entity**: Defines the schema for the RunnerPool custom resource, including required and optional fields, validation, and supported behaviors.
- **Services**: Internal services handle Azure DevOps API communication, Kubernetes Pod management, health checks, and certificate management.
- **Webhooks**: Admission webhooks for validation and mutation of RunnerPool resources, with automated certificate rotation for secure communication.

### Controller & Reconciliation Logic

The controller (`AzDORunnerController`) is responsible for:

- Watching for creation, updates, and deletion of RunnerPool CRDs.
- Reconciling the desired state (as defined in the CRD) with the actual state in the cluster:
  - Creating or deleting runner pods as needed.
  - Managing runner registration with Azure DevOps.
  - Handling capability-aware scheduling if enabled.
  - Cleaning up resources on deletion (using a finalizer).
- Reacting to changes in the CRD spec or status, and updating the status subresource with current information.

**Reconciliation Flow:**

1. Fetch the RunnerPool CRD instance.
2. Validate and mutate (if needed) via webhooks.
3. Check the current state of runner pods and Azure DevOps pool.
4. Create, update, or delete pods to match the desired count and configuration.
5. Update the CRD status.
6. Handle finalization logic on deletion.

### Internal Services

- **AzureDevOpsService**: Handles communication with Azure DevOps REST APIs for pool and agent management.
- **KubernetesPodService**: Manages runner pod lifecycle in the cluster.
- **HealthCheckService**: Monitors runner pod health and updates CRD status.
- **WebhookCertificateManager/BackgroundService**: Manages TLS certificates for webhooks, including automatic rotation.
- **AzureDevOpsPollingService**: Polls Azure DevOps for pool/agent status and triggers reconciliation if needed.

---

## Webhooks: Validation, Mutation, and Certificate Management

### Validation Webhook

- Ensures CRD spec is valid (e.g., required fields are set, values are within allowed ranges).
- Rejects invalid or incomplete RunnerPool resources.

### Mutating Webhook

- Sets default values for optional fields if not provided.
- Can inject additional labels or environment variables.

### Certificate Rotation

- Webhooks use TLS for secure communication with the Kubernetes API server.
- Certificates are managed and rotated automatically by the `WebhookCertificateManager` and `WebhookCertificateBackgroundService`.
- Rotation ensures webhooks remain trusted and available without manual intervention.

---

## Troubleshooting

- Check the status subresource of the RunnerPool for errors or conditions.
- Review logs from the operator pod for reconciliation or webhook errors.
- Ensure required secrets and service accounts are present and correctly referenced.

---

## Bugs & Known Issues

- The webhooks will reject CRDs with invalid values (such as out-of-range numbers or unsupported enum values) at creation or update time.
- However, the error reason returned by the webhook may be generic or show as "unknown reason" in the Kubernetes events or API response, rather than a detailed validation message.

## License

See [LICENSE](LICENSE).
