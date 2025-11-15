# Azure DevOps Operator - Go Learning Guide

Welcome! This operator is structured to help you learn Go and Kubernetes operator development step by step.

## 📚 Learning Path

### Phase 1: Understanding the Structure (Week 1)
1. **Start with `api/v1/runnerpool_types.go`**
   - Learn about Go structs and tags
   - Understand Kubernetes Custom Resource Definitions (CRDs)
   - See how to define your API types

2. **Read `cmd/main.go`**
   - Understand the entry point of a Go application
   - Learn about dependency injection and initialization
   - See how controller-runtime manager works

3. **Explore `internal/controller/runnerpool_controller.go`**
   - Learn about the Reconcile loop pattern
   - Understand error handling and requeuing
   - See how to interact with Kubernetes API

### Phase 2: Implementing Core Logic (Week 2-3)
4. **Implement Azure DevOps Client (`internal/azdo/client.go`)**
   - Learn HTTP clients in Go
   - Practice error handling
   - Understand context and cancellation

5. **Build Pod Service (`internal/kubernetes/pod_service.go`)**
   - Learn Kubernetes client-go library
   - Practice working with Kubernetes resources
   - Understand label selectors and field selectors

6. **Create Polling Service (`internal/azdo/polling.go`)**
   - Learn about goroutines and channels
   - Understand background tasks
   - Practice concurrent programming

### Phase 3: Advanced Features (Week 4)
7. **Implement Webhooks (`internal/webhook/`)**
   - Learn about admission webhooks
   - Practice validation logic
   - Understand mutation patterns

8. **Add Status Management**
   - Learn about status sub-resources
   - Practice updating resource status
   - Understand optimistic concurrency

## 🎯 Key Go Concepts You'll Learn

### 1. Structs and Methods
```go
type Client struct {
    httpClient *http.Client
}

func (c *Client) GetPools(ctx context.Context) ([]Pool, error) {
    // Implementation here
}
```

### 2. Interfaces
```go
type AzDoService interface {
    GetPools(ctx context.Context) ([]Pool, error)
    GetAgents(ctx context.Context, poolID int) ([]Agent, error)
}
```

### 3. Error Handling
```go
if err != nil {
    return fmt.Errorf("failed to get pools: %w", err)
}
```

### 4. Context
```go
ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
defer cancel()
```

### 5. Goroutines and Channels
```go
go func() {
    for {
        select {
        case <-ticker.C:
            // Do periodic work
        case <-ctx.Done():
            return
        }
    }
}()
```

## 📖 Recommended Resources

### Go Basics
- [A Tour of Go](https://go.dev/tour/)
- [Effective Go](https://go.dev/doc/effective_go)
- [Go by Example](https://gobyexample.com/)

### Kubernetes Operator Development
- [Kubebuilder Book](https://book.kubebuilder.io/)
- [controller-runtime Documentation](https://pkg.go.dev/sigs.k8s.io/controller-runtime)
- [client-go Documentation](https://pkg.go.dev/k8s.io/client-go)

### Azure DevOps API
- [Azure DevOps REST API Reference](https://learn.microsoft.com/en-us/rest/api/azure/devops/)

## 🔨 Development Workflow

1. **Run tests**: `make test`
2. **Build operator**: `make build`
3. **Install CRDs**: `make install`
4. **Run locally**: `make run`
5. **Deploy to cluster**: `make deploy`

## 💡 Tips for Learning

1. **Start small**: Implement one function at a time
2. **Read errors carefully**: Go's error messages are descriptive
3. **Use the debugger**: Learn to use `dlv` (Delve debugger)
4. **Write tests**: Practice test-driven development
5. **Read the source**: Look at similar operators for inspiration

## 🎓 Code Implementation Order

Follow this order to implement the operator:

1. ✅ Define types in `api/v1/runnerpool_types.go`
2. ✅ Implement Azure DevOps client (`internal/azdo/client.go`)
3. ✅ Implement pool operations (`internal/azdo/pool.go`)
4. ✅ Implement agent operations (`internal/azdo/agent.go`)
5. ✅ Implement job operations (`internal/azdo/job.go`)
6. ✅ Implement pod service (`internal/kubernetes/pod_service.go`)
7. ✅ Implement PVC service (`internal/kubernetes/pvc_service.go`)
8. ✅ Implement basic reconciler (`internal/controller/runnerpool_controller.go`)
9. ✅ Add polling service (`internal/azdo/polling.go`)
10. ✅ Add webhooks (`internal/webhook/`)
11. ✅ Add status updates
12. ✅ Add cleanup logic
13. ✅ Add metrics and observability

## 🐛 Common Pitfalls

1. **Forgetting to return errors**: Always check and propagate errors
2. **Not using contexts**: Always pass context for cancellation
3. **Race conditions**: Be careful with shared state in goroutines
4. **Infinite reconciliation**: Always return appropriate RequeueAfter
5. **Not updating status**: Remember to update status separately from spec

## 🚀 Next Steps

Once you've implemented the basic operator:
- Add metrics with Prometheus
- Add better logging with structured logging
- Add leader election for HA
- Add finalizers for cleanup
- Add more tests and integration tests
