# Project Structure Overview

This document explains the complete structure of the Azure DevOps Runner Operator in Go.

## Directory Structure

```
azdo-operator/
│
├── api/v1/                                    # Custom Resource Definitions (CRDs)
│   ├── runnerpool_types.go                   # ✅ RunnerPool CRD type definition
│   ├── groupversion_info.go                  # API group and version info
│   └── zz_generated.deepcopy.go              # Auto-generated deepcopy methods
│
├── cmd/
│   └── main.go                                # ✅ Application entry point with setup guidance
│
├── internal/                                  # Private application code
│   │
│   ├── azdo/                                  # Azure DevOps API integration
│   │   ├── types.go                          # ✅ COMPLETE - API types (Pool, Agent, Job)
│   │   ├── client.go                         # TODO - HTTP client with authentication
│   │   ├── pool.go                           # TODO - Pool management (GetPools, GetPoolByName)
│   │   ├── agent.go                          # TODO - Agent operations (GetAgents, UnregisterAgent)
│   │   ├── job.go                            # TODO - Job queries (GetJobRequests, GetQueuedJobsCount)
│   │   └── polling.go                        # TODO - Background polling service for auto-scaling
│   │
│   ├── controller/
│   │   ├── runnerpool_controller.go          # Partial - Main reconciliation loop
│   │   └── suite_test.go                     # Test suite setup
│   │
│   ├── kubernetes/                            # Kubernetes resource management
│   │   ├── pod_service.go                    # ✅ Created - Pod CRUD with guidance
│   │   └── pvc_service.go                    # ✅ Created - PVC management
│   │
│   └── webhook/                               # Admission webhooks
│       ├── runnerpool_validator.go           # ✅ Created - Validation logic
│       └── runnerpool_mutator.go             # ✅ Created - Default values
│
├── config/                                    # Kubernetes manifests
│   ├── crd/                                   # CRD definitions (auto-generated)
│   ├── default/                               # Default deployment config
│   ├── manager/                               # Operator deployment
│   ├── prometheus/                            # Prometheus monitoring
│   ├── rbac/                                  # RBAC permissions
│   ├── samples/                               # Example RunnerPool resources
│   └── network-policy/                        # Network policies
│
├── test/
│   ├── e2e/                                   # End-to-end tests
│   └── utils/                                 # Test utilities
│
├── hack/
│   └── boilerplate.go.txt                     # License header template
│
├── bin/                                       # Build tools
│   └── controller-gen-v0.17.2                # Code generator
│
├── go.mod                                     # Go module dependencies
├── go.sum                                     # Dependency checksums
├── Makefile                                   # Build automation
├── Dockerfile                                 # Container image
├── PROJECT                                    # Kubebuilder project metadata
├── LEARNING_GUIDE.md                          # ✅ Step-by-step learning path
└── README.md                                  # Project documentation
```

## File Purposes

### Core Application Files

#### `cmd/main.go`
- Entry point of the operator
- Initializes the manager, controllers, webhooks
- Sets up health checks and metrics
- Contains extensive comments on what to add where

#### `api/v1/runnerpool_types.go`
- Defines the RunnerPool Custom Resource
- Contains Spec (desired state) and Status (observed state)
- Already complete with all fields defined

### Azure DevOps Integration (`internal/azdo/`)

#### `types.go` ✅ COMPLETE
- Defines Go structs for Azure DevOps API responses
- `Pool`, `Agent`, `JobRequest`, `JobDefinition`
- Response wrappers: `PoolsResponse`, `AgentsResponse`, `JobRequestsResponse`

#### `client.go` TODO
**What to implement:**
- `NewClient()` - Constructor
- `TestConnection()` - Verify Azure DevOps connectivity
- `ExtractOrganizationName()` - Parse org from URL
- `makeRequest()` - Helper for authenticated HTTP requests

**Key concepts you'll learn:**
- HTTP client creation
- Basic authentication with PAT
- JSON marshaling/unmarshaling
- Error handling in Go

#### `pool.go` TODO
**What to implement:**
- `GetPools()` - List all agent pools
- `GetPoolByName()` - Find pool by name
- `GetPoolID()` - Get pool ID for API calls

**API Endpoint:** `/_apis/distributedtask/pools?api-version=7.0`

#### `agent.go` TODO
**What to implement:**
- `GetAgents()` - List agents in a pool
- `UnregisterAgent()` - Remove agent from pool
- `GetAgentByName()` - Find specific agent

**API Endpoint:** `/_apis/distributedtask/pools/{poolId}/agents?api-version=7.0`

#### `job.go` TODO
**What to implement:**
- `GetJobRequests()` - Get all job requests
- `GetQueuedJobsCount()` - Count queued jobs
- `GetQueuedJobsWithCapabilities()` - Jobs with specific demands
- `ExtractRequiredCapabilityFromDemands()` - Parse job requirements

**API Endpoint:** `/_apis/distributedtask/pools/{poolId}/jobrequests?api-version=7.0`

#### `polling.go` TODO
**What to implement:**
- `PollingService` struct - Background service
- `Start()` - Main polling loop
- `RegisterPool()` - Add pool to monitoring
- `UnregisterPool()` - Remove pool
- `pollPool()` - Core scaling logic
- `cleanupCompletedAgents()` - Remove finished pods
- `cleanupIdleAgents()` - TTL-based cleanup
- `ensureMinimumAgents()` - Maintain min agents
- `scaleUpForDemand()` - Create agents for queued jobs

**Key concepts:**
- Goroutines for background tasks
- Channels for communication
- sync.Map for concurrent access
- Time-based scheduling

### Kubernetes Integration (`internal/kubernetes/`)

#### `pod_service.go` ✅ Created with guidance
**What to implement:**
- `CreatePod()` - Create runner agent pod
- `DeletePod()` - Remove pod
- `GetAllRunnerPods()` - List all pods for pool
- `GetActivePods()` - Get running/pending pods
- `GetMinAgentPods()` - Get minimum agents
- `GetNextAvailableIndex()` - Find next agent index
- `UpdatePodLabels()` - Update pod labels
- `buildPodSpec()` - Build complete pod specification

**Key concepts:**
- Kubernetes Pod API
- Volume mounts
- Environment variables
- Security contexts
- Owner references
- Labels and selectors

#### `pvc_service.go` ✅ Created with guidance
**What to implement:**
- `CreatePVC()` - Create persistent volume claim
- `DeletePVC()` - Remove PVC
- `GetPVCsForAgent()` - Find PVCs for agent

### Controller (`internal/controller/`)

#### `runnerpool_controller.go` Partial implementation
**What to implement/complete:**
- Complete the `Reconcile()` function
- `getPATFromSecret()` - Read PAT from Kubernetes secret
- `updateAgentIndexTracking()` - Track agent indexes in status
- `updateStatus()` - Update resource status
- `cleanupResources()` - Cleanup on deletion
- Add finalizer logic
- Connect polling service

**Key concepts:**
- Reconciliation loop pattern
- Idempotency
- Error handling and requeuing
- Status updates
- Finalizers
- Owner references

### Webhooks (`internal/webhook/`)

#### `runnerpool_validator.go` ✅ Created with guidance
**What to implement:**
- `ValidateCreate()` - Validate on resource creation
- `ValidateUpdate()` - Validate on resource update
- `ValidateDelete()` - Validate on deletion
- Helper validation functions for each field

**Validation rules:**
- AzDoURL must be valid HTTP/HTTPS URL
- Pool name required
- PatSecretName required
- Image required
- MaxAgents >= MinAgents
- MaxAgents > 0
- Valid PVC configurations
- Valid environment variables

#### `runnerpool_mutator.go` ✅ Created with guidance
**What to implement:**
- `Default()` - Set default values
- Defaults:
  - MaxAgents = 10
  - MinAgents = 0
  - ImagePullPolicy = "IfNotPresent"
  - PollIntervalSeconds = 30
  - TTLIdleSeconds = 0

## Configuration Files (`config/`)

### `config/crd/` - Custom Resource Definition manifests (auto-generated)
### `config/rbac/` - RBAC permissions
- Generated from `// +kubebuilder:rbac` comments in code
- Permissions for pods, PVCs, secrets, etc.

### `config/manager/` - Operator deployment
- Deployment manifest for the operator itself

### `config/samples/` - Example resources
- Example RunnerPool YAML files

## How Components Work Together

```
1. User creates RunnerPool resource
   ↓
2. Webhook validates the resource
   ↓
3. Webhook sets default values
   ↓
4. Resource is stored in etcd
   ↓
5. Controller receives reconcile event
   ↓
6. Controller reads RunnerPool
   ↓
7. Controller gets PAT from secret
   ↓
8. Controller tests Azure DevOps connection
   ↓
9. Controller registers pool with PollingService
   ↓
10. PollingService polls Azure DevOps every N seconds
    ↓
11. If jobs queued: Create pods via PodService
    ↓
12. If idle timeout: Delete pods via PodService
    ↓
13. Update RunnerPool status with current state
    ↓
14. Loop back to step 10
```

## Implementation Priority

1. ✅ **Types** - Already complete
2. **Azure DevOps Client** - Foundation for everything else
3. **Pool/Agent/Job operations** - API interactions
4. **Kubernetes Services** - Pod and PVC management
5. **Controller** - Wire everything together
6. **Polling Service** - Auto-scaling logic
7. **Webhooks** - Validation and defaults
8. **Tests** - Ensure everything works

## Next Steps

1. Read `LEARNING_GUIDE.md` for detailed learning path
2. Start with `internal/azdo/client.go`
3. Follow the TODO comments in each file
4. Write tests as you implement
5. Run locally with `make run` to test

## Questions?

Each file has extensive comments explaining:
- What the function does
- What parameters mean
- What to return
- How to implement it
- Which Go patterns to use

Look for `TODO:` comments with step-by-step guides!
