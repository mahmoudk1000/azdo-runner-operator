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

// Package webhook contains validation and mutation webhooks for RunnerPool resources
// Webhooks run before resources are persisted to validate and modify them
package webhook

import (
	"context"
	"fmt"
	"net/url"
	"strings"

	"k8s.io/apimachinery/pkg/runtime"
	ctrl "sigs.k8s.io/controller-runtime"
	"sigs.k8s.io/controller-runtime/pkg/webhook"
	"sigs.k8s.io/controller-runtime/pkg/webhook/admission"

	opentoolsmfv1 "github.com/mahmoudk1000/azdo-runner-operator/api/v1"
)

// RunnerPoolValidator validates RunnerPool resources
// This implements the admission.CustomValidator interface
type RunnerPoolValidator struct {
	// TODO: Add any dependencies needed for validation (e.g., Kubernetes client)
}

// SetupWebhookWithManager registers the webhook with the manager
// This is called from main.go to enable webhook validation
// TODO: Implement webhook registration
func (v *RunnerPoolValidator) SetupWebhookWithManager(mgr ctrl.Manager) error {
	// TODO: Register the validating webhook
	// Use mgr.GetWebhookServer().Register() to register validation webhook
	return nil
}

// ValidateCreate validates a RunnerPool on creation
// Parameters:
//   - ctx: Context for cancellation
//   - obj: The RunnerPool being created
//
// Returns:
//   - admission.Warnings: Any warnings to show the user
//   - error: Validation error if the resource is invalid
//
// TODO: Implement create validation
func (v *RunnerPoolValidator) ValidateCreate(ctx context.Context, obj runtime.Object) (admission.Warnings, error) {
	runnerPool := obj.(*opentoolsmfv1.RunnerPool)
	
	// TODO: Validate required fields:
	// 1. AzDoURL must be a valid HTTP/HTTPS URL
	// 2. Pool name must not be empty
	// 3. PatSecretName must not be empty
	// 4. Image must not be empty
	// 5. MaxAgents must be >= MinAgents
	// 6. MaxAgents must be > 0
	//
	// Call helper functions for each validation
	
	if err := v.validateAzDoURL(runnerPool.Spec.AzDoURL); err != nil {
		return nil, err
	}
	
	// TODO: Add more validations
	
	return nil, nil
}

// ValidateUpdate validates a RunnerPool on update
// Parameters:
//   - ctx: Context
//   - oldObj: The existing RunnerPool
//   - newObj: The updated RunnerPool
//
// Returns warnings and error
// TODO: Implement update validation
func (v *RunnerPoolValidator) ValidateUpdate(ctx context.Context, oldObj, newObj runtime.Object) (admission.Warnings, error) {
	newRunnerPool := newObj.(*opentoolsmfv1.RunnerPool)
	
	// TODO: Validate updates
	// Most validations are the same as Create
	// But you might want to prevent changes to certain fields
	// or warn about disruptive changes
	
	return v.ValidateCreate(ctx, newRunnerPool)
}

// ValidateDelete validates deletion of a RunnerPool
// Usually this just returns nil unless you want to prevent deletion in certain conditions
// TODO: Implement delete validation
func (v *RunnerPoolValidator) ValidateDelete(ctx context.Context, obj runtime.Object) (admission.Warnings, error) {
	// TODO: Add any delete validation logic
	// For example, you might want to prevent deletion if agents are running jobs
	return nil, nil
}

// Helper validation functions

// validateAzDoURL validates the Azure DevOps URL format
// TODO: Implement URL validation
func (v *RunnerPoolValidator) validateAzDoURL(azDoURL string) error {
	if azDoURL == "" {
		return fmt.Errorf("azDoUrl is required")
	}
	
	// TODO: Parse URL and validate:
	// 1. Must be valid URL
	// 2. Must use http or https scheme
	// 3. Should be either dev.azure.com or visualstudio.com domain
	
	parsedURL, err := url.Parse(azDoURL)
	if err != nil {
		return fmt.Errorf("azDoUrl must be a valid URL: %w", err)
	}
	
	if parsedURL.Scheme != "http" && parsedURL.Scheme != "https" {
		return fmt.Errorf("azDoUrl must use http or https scheme")
	}
	
	return nil
}

// validateImage validates the container image reference
// TODO: Implement image validation
func (v *RunnerPoolValidator) validateImage(image string) error {
	if image == "" {
		return fmt.Errorf("image is required")
	}
	
	// TODO: Validate image format
	// 1. Must not contain spaces or tabs
	// 2. Should be a valid image reference (registry/repo:tag)
	
	if strings.ContainsAny(image, " \t") {
		return fmt.Errorf("image must not contain spaces or tabs")
	}
	
	return nil
}

// validateAgentCounts validates min/max agent configuration
// TODO: Implement agent count validation
func (v *RunnerPoolValidator) validateAgentCounts(minAgents, maxAgents int) error {
	// TODO: Validate:
	// 1. MaxAgents must be > 0
	// 2. MinAgents must be >= 0
	// 3. MinAgents must be <= MaxAgents
	
	if maxAgents <= 0 {
		return fmt.Errorf("maxAgents must be greater than 0")
	}
	
	if minAgents < 0 {
		return fmt.Errorf("minAgents must be >= 0")
	}
	
	if minAgents > maxAgents {
		return fmt.Errorf("minAgents (%d) must not exceed maxAgents (%d)", minAgents, maxAgents)
	}
	
	return nil
}

// validateExtraEnv validates the extra environment variables
// TODO: Implement env var validation
func (v *RunnerPoolValidator) validateExtraEnv(extraEnv []opentoolsmfv1.EnvVar) error {
	// TODO: Validate each env var:
	// 1. Name must not be empty
	// 2. Either Value or ValueFrom must be set, but not both
	// 3. If ValueFrom.SecretKeyRef is used, validate it's properly formed
	
	for i, env := range extraEnv {
		if env.Name == "" {
			return fmt.Errorf("extraEnv[%d].name is required", i)
		}
		
		// TODO: Add more validation
	}
	
	return nil
}

// validatePVCs validates PVC configurations
// TODO: Implement PVC validation
func (v *RunnerPoolValidator) validatePVCs(pvcs []opentoolsmfv1.PVCConfig) error {
	// TODO: Validate each PVC:
	// 1. Name must not be empty
	// 2. MountPath must not be empty
	// 3. Storage must be a valid quantity (e.g., "10Gi", "100Mi")
	// 4. StorageClass should exist (might need to query API)
	
	for i, pvc := range pvcs {
		if pvc.Name == "" {
			return fmt.Errorf("pvcs[%d].name is required", i)
		}
		
		if pvc.MountPath == "" {
			return fmt.Errorf("pvcs[%d].mountPath is required", i)
		}
		
		// TODO: Add more validation
	}
	
	return nil
}

// validateCertTrustStore validates certificate trust store configuration
// TODO: Implement cert validation
func (v *RunnerPoolValidator) validateCertTrustStore(certs []opentoolsmfv1.CertTrustStoreConfig) error {
	// TODO: Validate:
	// 1. SecretName must not be empty
	// 2. Secret should exist (might need to query API)
	
	for i, cert := range certs {
		if cert.SecretName == "" {
			return fmt.Errorf("certTrustStore[%d].secretName is required", i)
		}
	}
	
	return nil
}
