# 🎯 Quick Start - Your First Steps

Welcome! This guide will get you started implementing the Azure DevOps Runner Operator in Go.

## ✅ What's Already Done

The project structure is complete with:

- ✅ **Types defined** (`internal/azdo/types.go`) - All Azure DevOps API types
- ✅ **Learning guides** - Extensive documentation and comments
- ✅ **File structure** - All files created with function signatures and TODOs
- ✅ **Comments everywhere** - Every function has implementation guidance

## 🚀 Start Here (5 Minutes)

### 1. Understand the Structure

```bash
# Read the structure overview
cat STRUCTURE.md

# Read the learning guide
cat LEARNING_GUIDE.md
```

### 2. Set Up Your Environment

```bash
# Verify Go is installed
go version  # Should be 1.21+

# Download dependencies
go mod download

# Verify Kubernetes access
kubectl cluster-info
```

### 3. Run the Project (Without Implementation)

```bash
# Install CRDs
make install

# Run locally (it won't do much yet, but it runs!)
make run
```

You'll see it start, but it won't scale agents yet - that's what you'll implement!

## 📝 Your First Implementation (30 Minutes)

Let's implement the Azure DevOps client - the foundation of everything else.

### Step 1: Implement `internal/azdo/client.go`

Open the file and you'll see:

```go
func NewClient(httpClient *http.Client) *Client {
    // TODO: Initialize and return a Client
    // Remember to handle nil httpClient by using http.DefaultClient
    return nil
}
```

**Your task:** Replace `return nil` with actual implementation:

```go
func NewClient(httpClient *http.Client) *Client {
    if httpClient == nil {
        httpClient = http.DefaultClient
    }
    return &Client{
        httpClient: httpClient,
    }
}
```

### Step 2: Implement `TestConnection`

Find this TODO:

```go
func (c *Client) TestConnection(ctx context.Context, azDoURL, pat string) error {
    // TODO: Make a GET request to Azure DevOps to verify connectivity
    return nil
}
```

**Hints:**
1. Create an HTTP GET request to `{azDoURL}/_apis/connectionData?api-version=7.0`
2. Add Basic Auth header: `Authorization: Basic base64(":" + pat)`
3. Execute the request with `c.httpClient.Do(req.WithContext(ctx))`
4. Check if response status is 200
5. Return error if anything fails

### Step 3: Write a Test

Create `internal/azdo/client_test.go`:

```go
package azdo

import (
    "context"
    "testing"
)

func TestNewClient(t *testing.T) {
    client := NewClient(nil)
    if client == nil {
        t.Fatal("expected client, got nil")
    }
    if client.httpClient == nil {
        t.Error("expected httpClient to be set")
    }
}

// Add more tests...
```

### Step 4: Run Your Tests

```bash
go test ./internal/azdo/... -v
```

## 📚 Next 3 Hours

After completing the client basics:

1. **Implement pool operations** (`internal/azdo/pool.go`)
   - `GetPools()` - Use your `makeRequest` helper
   - `GetPoolByName()` - Filter the results
   - Test with real Azure DevOps API

2. **Implement agent operations** (`internal/azdo/agent.go`)
   - `GetAgents()` - Similar to GetPools
   - `UnregisterAgent()` - Use DELETE method

3. **Implement job operations** (`internal/azdo/job.go`)
   - `GetJobRequests()` - Get queued jobs
   - `GetQueuedJobsCount()` - Count them

## 🎯 Week 1 Goal

By the end of week 1, you should have:

- ✅ Azure DevOps client fully implemented
- ✅ All pool/agent/job operations working
- ✅ Tests passing
- ✅ Ability to query real Azure DevOps API

## 💡 Tips for Success

### 1. Use the Comments

Every function has detailed comments. Read them carefully:

```go
// GetPools retrieves all agent pools from Azure DevOps organization
// This is useful for listing available pools or verifying a pool exists
// Parameters:
//   - ctx: Context for cancellation and timeouts
//   - azDoURL: Azure DevOps organization URL
//   - pat: Personal Access Token
//
// Returns:
//   - []Pool: Slice of all pools in the organization
//   - error: Any error that occurred
//
// API Endpoint: GET {azDoURL}/_apis/distributedtask/pools?api-version=7.0
// TODO: Implement by calling makeRequest with PoolsResponse
func (c *Client) GetPools(ctx context.Context, azDoURL, pat string) ([]Pool, error) {
```

### 2. Follow the TODO Steps

Inside each function, there are numbered steps:

```go
// TODO: 
// 1. Build the URL: {azDoURL}/_apis/distributedtask/pools?api-version=7.0
// 2. Create a PoolsResponse variable to hold the result
// 3. Call makeRequest with GET method
// 4. Return the pools from response.Value
```

### 3. Test as You Go

Don't implement everything at once. Implement one function, write a test, verify it works, then move on.

### 4. Use the Reference

The C# operator is linked in the documentation. When stuck, look at how it's done there, then translate to Go.

### 5. Common Go Patterns You'll Use

**Error handling:**
```go
if err != nil {
    return nil, fmt.Errorf("failed to do something: %w", err)
}
```

**Context usage:**
```go
req, err := http.NewRequestWithContext(ctx, "GET", url, nil)
```

**JSON unmarshaling:**
```go
var response PoolsResponse
if err := json.Unmarshal(data, &response); err != nil {
    return nil, err
}
```

**String formatting:**
```go
url := fmt.Sprintf("%s/_apis/distributedtask/pools?api-version=7.0", azDoURL)
```

## 🆘 Getting Stuck?

### Problem: "I don't understand how to make HTTP requests in Go"

**Solution:** Check out:
- [Go by Example: HTTP Clients](https://gobyexample.com/http-clients)
- Look at `internal/azdo/client.go` - the `makeRequest` function is your template

### Problem: "Tests are failing"

**Solution:**
1. Read the error message carefully
2. Add `fmt.Printf` statements to debug
3. Use the Go debugger: `dlv test`

### Problem: "I'm lost in the structure"

**Solution:**
1. Read `STRUCTURE.md` again
2. Focus on one file at a time
3. Start with `internal/azdo/client.go` and don't move on until it works

## 📈 Progress Tracking

Create a checklist for yourself:

```
Week 1: Azure DevOps Client
[ ] client.go - NewClient, TestConnection, makeRequest
[ ] pool.go - GetPools, GetPoolByName, GetPoolID
[ ] agent.go - GetAgents, UnregisterAgent, GetAgentByName
[ ] job.go - GetJobRequests, GetQueuedJobsCount
[ ] Tests for all of the above

Week 2: Kubernetes Resources
[ ] pod_service.go - CreatePod, DeletePod, GetAllRunnerPods
[ ] pvc_service.go - CreatePVC, DeletePVC
[ ] Tests for pod and PVC services

Week 3: Controller
[ ] runnerpool_controller.go - Complete Reconcile loop
[ ] Add finalizers
[ ] Status updates
[ ] Integration tests

Week 4: Polling & Webhooks
[ ] polling.go - Background service
[ ] Webhooks - Validation and mutation
[ ] End-to-end tests
[ ] Deploy to cluster!
```

## 🎉 First Milestone

You've completed the first milestone when:

✅ You can run this code successfully:

```go
client := azdo.NewClient(nil)
pools, err := client.GetPools(context.Background(), "https://dev.azure.com/myorg", "my-pat")
if err != nil {
    log.Fatal(err)
}
fmt.Printf("Found %d pools\n", len(pools))
```

## 🚀 Ready to Start?

```bash
# Open your first file
code internal/azdo/client.go

# Keep the learning guide handy
code LEARNING_GUIDE.md

# Start implementing!
```

**Remember:** The goal is to learn Go, not just to finish. Take your time, understand each concept, and enjoy the journey!

---

**Questions?** All the answers are in the comments within the files. Read them carefully!

**Good luck! 🎓**
