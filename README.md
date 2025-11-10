
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
- **Certificate Trust Store**: Mount custom CA certificates and TLS secrets
- **Health Monitoring**: Continuous agent health checking

## Configuration Reference

### Core Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `azDoUrl` | string | true | Azure DevOps organization URL |
| `pool` | string | true | Azure DevOps agent pool name |
| `patSecretName` | string | true | Kubernetes secret containing PAT |
| `image` | string | true | Container image for agents |
| `maxAgents` | int | false | Maximum number of agents (default: 10) |
| `minAgents` | int | false | Minimum number of agents (default: 0) |
| `ttlIdleSeconds` | int | false | Seconds before idle agents are removed (default: 0) |
| `initContainer` | object | false | Init container configuration for permission setup |
| `securityContext` | object | false | Security context for agent container (runAsUser, runAsGroup, fsGroup) |
| `certTrustStore` | array | false | List of TLS secrets to mount as trusted certificates |

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

### Init Container for Permission Management

Configure an init container to adjust volume permissions for the runner user:

```yaml
spec:
  initContainer:
    image: busybox:latest
  securityContext:
    runAsUser: 1000    # UID for agent container
    runAsGroup: 1000   # GID for agent container
    fsGroup: 1000      # File system group ownership
```

**How it works:**

The init container **always runs as root (UID 0)** with a script that automatically adjusts permissions on all mounted volumes. It executes `chown` and `chmod` commands to set ownership to the specified `securityContext.runAsUser:runAsGroup` for each PVC mount path.

The **agent container** runs with the configured security context as a non-root user (default: azureuser, UID 1000), ensuring secure execution while maintaining access to properly configured volumes.

**Configuration:**
- `initContainer.image`: Image used for the init container (default: `busybox:latest`)
- `securityContext.runAsUser`: UID for the agent container (default: 1000)
- `securityContext.runAsGroup`: GID for the agent container (default: 1000)
- `securityContext.fsGroup`: File system group ownership (default: 1000)
- Init container security: Always runs as root to modify permissions (not configurable)
- Agent container security: Runs as the specified non-root user with no privilege escalation

**Note:** The security context values should match the UID/GID of the user in your agent Dockerfile. By default, the agent runs as `azureuser` with UID:GID 1000:1000.

### Certificate Trust Store

Mount custom CA certificates and TLS secrets into agent pods:

```yaml
spec:
  certTrustStore:
    - secretName: my-ca-cert
    - secretName: company-root-ca
    - secretName: proxy-cert
```

Certificates are mounted at `/etc/ssl/certs/{secretName}` as read-only volumes.

#### Use Cases

- Corporate CA certificates for internal services
- Proxy certificates for firewall environments
- Custom root CAs for private certificate authorities
- Client certificates for mutual TLS authentication

#### Creating Certificate Secrets

```bash
# Create from certificate files
kubectl create secret generic my-ca-cert \
  --from-file=tls.crt=/path/to/ca-certificate.crt \
  --from-file=tls.key=/path/to/ca-certificate.key

# Create TLS secret
kubectl create secret tls company-root-ca \
  --cert=/path/to/company-root-ca.crt \
  --key=/path/to/company-root-ca.key
```

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

### Runner Pool with Custom Certificates

```yaml
apiVersion: devops.opentools.mf/v1
kind: RunnerPool
metadata:
  name: corporate-runners
spec:
  azDoUrl: https://dev.azure.com/my-org
  pool: secure-pool
  patSecretName: pat-token
  image: ghcr.io/mahmoudk1000/azdo-runner-operator/agent:main
  maxAgents: 5
  minAgents: 1
  certTrustStore:
    - secretName: corporate-ca-bundle
    - secretName: proxy-certificates
  extraEnv:
    - name: SSL_CERT_DIR
      value: "/etc/ssl/certs"
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
  initContainer:
    image: busybox:latest
  securityContext:
    runAsUser: 1000
    runAsGroup: 1000
    fsGroup: 1000
  certTrustStore:
    - secretName: corporate-ca
    - secretName: proxy-cert
    - secretName: internal-root-ca
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

**Certificate trust store issues:**

- Verify certificate secrets exist in the correct namespace
- Check secret contains valid certificate data
- Ensure agent image supports certificate installation
- Review agent logs for SSL/TLS connection errors

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
