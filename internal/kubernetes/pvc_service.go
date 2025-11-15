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

// Package kubernetes - pvc_service.go handles PersistentVolumeClaim operations
package kubernetes

import (
	"context"

	corev1 "k8s.io/api/core/v1"
	"sigs.k8s.io/controller-runtime/pkg/client"
	
	opentoolsmfv1 "github.com/mahmoudk1000/azdo-runner-operator/api/v1"
)

// PVCService handles PersistentVolumeClaim operations for runner agents
// PVCs provide persistent storage for agent workspaces, caches, etc.
type PVCService struct {
	client client.Client
	// TODO: Add logger
}

// NewPVCService creates a new PVC service
// TODO: Implement constructor
func NewPVCService(client client.Client) *PVCService {
	return nil
}

// CreatePVC creates a PersistentVolumeClaim for an agent
// Parameters:
//   - ctx: Context for cancellation
//   - runnerPool: The RunnerPool resource
//   - pvcConfig: PVC configuration from RunnerPool spec
//   - agentIndex: The agent index this PVC is for
//
// Returns:
//   - *corev1.PersistentVolumeClaim: The created PVC
//   - error: Any error that occurred
//
// TODO: Implement PVC creation
func (s *PVCService) CreatePVC(ctx context.Context, runnerPool *opentoolsmfv1.RunnerPool, pvcConfig opentoolsmfv1.PVCConfig, agentIndex int) (*corev1.PersistentVolumeClaim, error) {
	// TODO: Create PVC with:
	// 1. Name: {runnerPool.Name}-{pvcConfig.Name}-{agentIndex}
	// 2. Storage size: from pvcConfig.Storage
	// 3. Storage class: from pvcConfig.StorageClass
	// 4. Labels: runner-pool, agent-index
	// 5. Owner reference: runnerPool
	return nil, nil
}

// DeletePVC deletes a PVC
// This is called when an agent is removed and deleteWithAgent is true
// TODO: Implement PVC deletion
func (s *PVCService) DeletePVC(ctx context.Context, namespace, name string) error {
	// TODO: Delete PVC using client.Delete()
	return nil
}

// GetPVCsForAgent gets all PVCs associated with a specific agent
// TODO: Implement PVC querying by agent index
func (s *PVCService) GetPVCsForAgent(ctx context.Context, runnerPool *opentoolsmfv1.RunnerPool, agentIndex int) ([]corev1.PersistentVolumeClaim, error) {
	// TODO: List PVCs with labels matching runner-pool and agent-index
	return nil, nil
}
