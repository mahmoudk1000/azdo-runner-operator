
# AzDO Runner Operator

A Kubernetes operator that manages self-hosted Azure DevOps runners as custom resources in your cluster. Provides automated lifecycle management, intelligent scaling, persistent storage, and secure webhook operations.

## Quick Start

### Installation

Install using Helm:

```bash
helm repo add mahmoudk1000 https://mahmoudk1000.github.io/charts/
helm repo update
helm install azdo-operator mahmoudk1000/azdo-runner-operator -n azdo-operator --create-namespace
```

### Create Azure DevOps PAT Secret

```bash
kubectl create secret generic pat-token --from-literal=token=YOUR_PAT_TOKEN
```

### Deploy a Runner Pool

```yaml
apiVersion: devops.opentools.mf/v1
kind: RunnerPool
metadata:
  name: my-runners
spec:
  azDoUrl: https://dev.azure.com/my-org
  pool: my-pool
  patSecretName: pat-token
  image: ghcr.io/mahmoudk1000/azdo-runner-operator/agent:main
  maxAgents: 5
  minAgents: 1
```

## Features

### 🚀 Intelligent Agent Management
- **Indexed Agents**: StatefulSet-like naming (`agent-0`, `agent-1`) without StatefulSet complexity
- **Auto-Scaling**: Dynamic scaling based on Azure DevOps queue demand
- **Min/Max Limits**: Configurable agent count boundaries
- **TTL Management**: Automatic cleanup of idle agents

### 💾 Persistent Storage
- **PVC Support**: Attach persistent volumes to agents
- **Flexible Lifecycle**: Choose to preserve or delete storage with agents
- **Storage Reuse**: Agents automatically reconnect to existing storage
- **Multiple Volumes**: Support for multiple PVCs per agent

### 🔧 Environment Customization
- **Extra Environment Variables**: Inject custom env vars into agents
- **Secret References**: Support for both direct values and Kubernetes secrets
- **Capability-Aware Scheduling**: Route jobs to specialized agents

### 🔒 Security & Reliability
- **Admission Webhooks**: Validate and mutate resources on creation/update
- **Auto Certificate Rotation**: Seamless webhook certificate management
- **Health Monitoring**: Continuous agent health checking

## Configuration Reference

### Core Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `azDoUrl` | string | ✅ | Azure DevOps organization URL |
| `pool` | string | ✅ | Azure DevOps agent pool name |
| `patSecretName` | string | ✅ | Kubernetes secret containing PAT |
| `image` | string | ✅ | Container image for agents |
| `maxAgents` | int | ❌ | Maximum number of agents (default: 10) |
| `minAgents` | int | ❌ | Minimum number of agents (default: 0) |
| `ttlIdleSeconds` | int | ❌ | Seconds before idle agents are removed (default: 0) |

### Environment Variables

Inject custom environment variables into agents:

```yaml
spec:
  extraEnv:
    - name: CUSTOM_VAR
      value: "custom-value"
    - name: SECRET_VAR
      valueFrom:
        secretKeyRef:
          name: my-secret
          key: secret-key
```

### Persistent Storage

Configure persistent volumes for agents:

```yaml
spec:
  pvcs:
    - name: workspace
      mountPath: /workspace
      storage: 10Gi
      storageClass: fast-ssd
      createPvc: true
      deleteWithAgent: false  # Preserve storage for reuse
    - name: cache
      mountPath: /cache
      storage: 5Gi
      createPvc: true
      deleteWithAgent: true   # Remove with agent
```

#### PVC Configuration

| Field | Type | Description |
|-------|------|-------------|
| `name` | string | PVC name identifier |
| `mountPath` | string | Mount path in container |
| `storage` | string | Storage size (supports Gi, Mi, Ki) |
| `storageClass` | string | Kubernetes storage class |
| `createPvc` | bool | Whether operator should create the PVC |
| `optional` | bool | Continue if PVC creation fails |
| `deleteWithAgent` | bool | Delete PVC when agent is removed |

### Capability-Aware Agents

Route specific jobs to specialized agents:

```yaml
spec:
  capabilityAware: true
  capabilityImages:
    java: my-registry/agent:java
    nodejs: my-registry/agent:nodejs
    dotnet: my-registry/agent:dotnet
```

Use in Azure DevOps pipeline:

```yaml
pool:
  name: my-pool
  demands:
    - java  # Routes to Java-capable agent
```

## Examples

### Basic Runner Pool

```yaml
apiVersion: devops.opentools.mf/v1
kind: RunnerPool
metadata:
  name: basic-runners
spec:
  azDoUrl: https://dev.azure.com/my-org
  pool: default
  patSecretName: pat-token
  image: ghcr.io/mahmoudk1000/azdo-runner-operator/agent:main
  maxAgents: 3
```

### Advanced Configuration

```yaml
apiVersion: devops.opentools.mf/v1
kind: RunnerPool
metadata:
  name: advanced-runners
spec:
  azDoUrl: https://dev.azure.com/my-org
  pool: production
  patSecretName: pat-token
  image: ghcr.io/mahmoudk1000/azdo-runner-operator/agent:main
  maxAgents: 10
  minAgents: 2
  ttlIdleSeconds: 300
  capabilityAware: true
  capabilityImages:
    docker: my-registry/agent:docker
    kubernetes: my-registry/agent:k8s
  extraEnv:
    - name: BUILD_ENVIRONMENT
      value: production
    - name: API_KEY
      valueFrom:
        secretKeyRef:
          name: api-secrets
          key: production-key
  pvcs:
    - name: workspace
      mountPath: /workspace
      storage: 20Gi
      storageClass: premium-ssd
      createPvc: true
      deleteWithAgent: false
    - name: docker-cache
      mountPath: /var/lib/docker
      storage: 50Gi
      createPvc: true
      deleteWithAgent: true
```

## Status Information

Monitor your runner pools:

```bash
kubectl get runnerpools
```

```
NAME               STATUS      POOL         ORGANIZATION   QUEUED   AGENTS   RUNNING
advanced-runners   Connected   production   my-org         2        3/10     3
basic-runners      Connected   default      my-org         0        1/3      1
```

Detailed status:

```bash
kubectl describe runnerpool advanced-runners
```

## Troubleshooting

### Common Issues

**Agents not starting:**
- Verify PAT secret exists and is valid
- Check image pull policy and registry access
- Review agent pod logs: `kubectl logs -l app=azdo-runner`

**Storage issues:**
- Ensure storage class exists
- Check PVC creation permissions
- Verify storage quotas

**Webhook errors:**
- Check operator logs for certificate issues
- Verify webhook service is running
- Ensure proper RBAC permissions

### Useful Commands

```bash
# View operator logs
kubectl logs -n azdo-operator deployment/azdo-runner-operator

# Check runner pod status
kubectl get pods -l runner-pool=my-runners

# Inspect PVC usage
kubectl get pvc -l runner-pool=my-runners

# View webhook configuration
kubectl get validatingwebhookconfigurations
kubectl get mutatingwebhookconfigurations
```

## License

This project is licensed under the terms specified in [LICENSE](LICENSE).
