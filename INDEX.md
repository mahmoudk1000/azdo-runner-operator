# Azure DevOps Runner Operator - Documentation Index

## 📚 Documentation Guide

This project has comprehensive documentation to help you learn Go and Kubernetes operators. Here's where to find everything:

### 🎯 Start Here

1. **[QUICKSTART.md](QUICKSTART.md)** - Begin here! Your first 5 minutes to 3 hours
   - Environment setup
   - First implementation (Azure DevOps client)
   - Quick wins to get you started

2. **[LEARNING_GUIDE.md](LEARNING_GUIDE.md)** - Complete learning path
   - Week-by-week implementation plan
   - Go concepts explained
   - Resources and tutorials
   - Common pitfalls and solutions

3. **[STRUCTURE.md](STRUCTURE.md)** - Project structure explained
   - What each file does
   - Implementation priorities
   - How components work together
   - API endpoints reference

### 📖 Code Documentation

Every single file has extensive inline documentation:

#### Azure DevOps Integration
- `internal/azdo/types.go` - ✅ **COMPLETE** - Study this first
- `internal/azdo/client.go` - HTTP client template with TODOs
- `internal/azdo/pool.go` - Pool operations with guidance
- `internal/azdo/agent.go` - Agent management with hints
- `internal/azdo/job.go` - Job queries with examples
- `internal/azdo/polling.go` - Background service with patterns

#### Kubernetes Management
- `internal/kubernetes/pod_service.go` - Pod CRUD with step-by-step guides
- `internal/kubernetes/pvc_service.go` - PVC management with examples

#### Controller
- `internal/controller/runnerpool_controller.go` - Reconciliation loop with detailed steps

#### Webhooks
- `internal/webhook/runnerpool_validator.go` - Validation logic with rules
- `internal/webhook/runnerpool_mutator.go` - Default values with examples

#### Entry Point
- `cmd/main.go` - Application startup with initialization guide

### 🎓 Learning Resources

#### Go Language
- [A Tour of Go](https://go.dev/tour/) - Interactive tutorial
- [Go by Example](https://gobyexample.com/) - Code examples
- [Effective Go](https://go.dev/doc/effective_go) - Best practices

#### Kubernetes Operators
- [Kubebuilder Book](https://book.kubebuilder.io/) - Official guide
- [controller-runtime](https://pkg.go.dev/sigs.k8s.io/controller-runtime) - Framework docs
- [Operator Pattern](https://kubernetes.io/docs/concepts/extend-kubernetes/operator/) - Concepts

#### Azure DevOps
- [REST API Reference](https://learn.microsoft.com/en-us/rest/api/azure/devops/)
- [Distributed Task API](https://learn.microsoft.com/en-us/rest/api/azure/devops/distributedtask)

### 🔍 Finding Things

#### "How do I implement X?"
→ Look in the file for X, read the TODO comments

#### "What's the overall structure?"
→ Read [STRUCTURE.md](STRUCTURE.md)

#### "How do I get started?"
→ Read [QUICKSTART.md](QUICKSTART.md)

#### "What should I build first?"
→ Follow [LEARNING_GUIDE.md](LEARNING_GUIDE.md)

#### "I'm stuck on Go syntax"
→ Check the inline comments, they often have examples

#### "How does the C# version do it?"
→ Check the [original repo](https://github.com/mahmoudk1000/azdo-runner-operator)

### 📋 Quick Reference

#### Implementation Order

```
1. ✅ types.go (already done)
2. client.go → pool.go → agent.go → job.go
3. pod_service.go → pvc_service.go
4. runnerpool_controller.go (complete it)
5. polling.go
6. runnerpool_validator.go → runnerpool_mutator.go
```

#### Key Commands

```bash
# Install CRDs
make install

# Run locally
make run

# Run tests
make test

# Build binary
make build

# Build and push image
make docker-build docker-push IMG=your-registry/azdo-operator:tag

# Deploy to cluster
make deploy IMG=your-registry/azdo-operator:tag
```

#### Project Structure at a Glance

```
azdo-operator/
├── QUICKSTART.md           ← Start here!
├── LEARNING_GUIDE.md       ← Complete learning path
├── STRUCTURE.md            ← Project structure explained
├── INDEX.md                ← This file
│
├── cmd/main.go             ← Entry point
├── api/v1/                 ← CRD definitions
├── internal/
│   ├── azdo/               ← Azure DevOps integration
│   ├── controller/         ← Reconciliation logic
│   ├── kubernetes/         ← K8s resource management
│   └── webhook/            ← Admission webhooks
└── config/                 ← Kubernetes manifests
```

### 💡 Tips

1. **Read the comments** - Every function has detailed guidance
2. **Follow the TODOs** - They break down implementation into steps
3. **Test as you go** - Write tests for each function
4. **One file at a time** - Don't try to implement everything at once
5. **Use the learning guide** - It has the optimal order

### 🎯 Goals by Week

**Week 1:** Azure DevOps client working, can query API  
**Week 2:** Kubernetes services working, can create/delete pods  
**Week 3:** Controller working, basic reconciliation  
**Week 4:** Polling service, webhooks, full operator working

### 📞 Getting Help

**Stuck on Go?** → [Go documentation](https://go.dev/doc/)  
**Stuck on Kubernetes?** → [Kubernetes docs](https://kubernetes.io/docs/)  
**Stuck on Operators?** → [Kubebuilder book](https://book.kubebuilder.io/)  
**Stuck on Azure DevOps API?** → [API docs](https://learn.microsoft.com/en-us/rest/api/azure/devops/)

### ✅ Success Criteria

You'll know you've succeeded when:

- [ ] All TODO comments are implemented
- [ ] All tests pass (`make test`)
- [ ] Operator runs locally (`make run`)
- [ ] RunnerPool resources can be created
- [ ] Pods are created/deleted based on queue
- [ ] Agents appear in Azure DevOps
- [ ] Status is updated correctly
- [ ] Webhooks validate resources

### 🚀 Ready?

```bash
# Step 1: Read the quick start
cat QUICKSTART.md

# Step 2: Open your first file
code internal/azdo/client.go

# Step 3: Start coding!
```

---

## 📝 Documentation Files

| File | Purpose | When to Read |
|------|---------|--------------|
| **QUICKSTART.md** | Get started in 5 minutes | **Start here** |
| **LEARNING_GUIDE.md** | Complete learning path | Read after quickstart |
| **STRUCTURE.md** | Project organization | Reference as needed |
| **INDEX.md** | This file | Navigation |
| **README.md** | Project overview | General information |

---

**Happy learning! 🎓 You've got this! 💪**

Start with [QUICKSTART.md](QUICKSTART.md) and begin your Go journey!
