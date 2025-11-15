/*
Copyright 2025.

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

// Package webhook - runnerpool_mutator.go handles mutation of RunnerPool resources
// Mutation webhooks can set default values and modify resources before they're stored
package webhook

import (
	"context"

	"k8s.io/apimachinery/pkg/runtime"
	ctrl "sigs.k8s.io/controller-runtime"
	"sigs.k8s.io/controller-runtime/pkg/webhook/admission"

	opentoolsmfv1 "github.com/mahmoudk1000/azdo-runner-operator/api/v1"
)

// RunnerPoolMutator mutates (modifies) RunnerPool resources
// This implements the admission.CustomDefaulter interface
type RunnerPoolMutator struct {
	// TODO: Add dependencies if needed
}

// SetupWebhookWithManager registers the mutating webhook
// TODO: Implement webhook registration
func (m *RunnerPoolMutator) SetupWebhookWithManager(mgr ctrl.Manager) error {
	// TODO: Register the mutating webhook with the manager
	return nil
}

// Default sets default values for RunnerPool
// This is called before validation, so you can fill in missing optional fields
// Parameters:
//   - ctx: Context
//   - obj: The RunnerPool object to mutate
//
// Returns error if mutation fails
// TODO: Implement defaulting logic
func (m *RunnerPoolMutator) Default(ctx context.Context, obj runtime.Object) error {
	runnerPool := obj.(*opentoolsmfv1.RunnerPool)
	
	// TODO: Set default values for optional fields:
	// 1. If MaxAgents is 0, set it to 10
	// 2. If MinAgents is not set, set it to 0
	// 3. If ImagePullPolicy is empty, set it to "IfNotPresent"
	// 4. If PollIntervalSeconds is 0, set it to 30
	// 5. If TTLIdleSeconds is not set, set it to 0 (no cleanup)
	// 6. Set any other sensible defaults
	
	if runnerPool.Spec.MaxAgents == 0 {
		runnerPool.Spec.MaxAgents = 10
	}
	
	// TODO: Add more defaults
	
	return nil
}
