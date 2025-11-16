/*
Copyright 2025 mahmoudk1000.

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

// Package controller contains the core reconciliation logic for the operator
// The reconciler watches RunnerPool resources and ensures actual state matches desired state
package controller

import (
	"context"
	"fmt"
	"time"

	corev1 "k8s.io/api/core/v1"
	"k8s.io/apimachinery/pkg/api/errors"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/runtime"
	ctrl "sigs.k8s.io/controller-runtime"
	"sigs.k8s.io/controller-runtime/pkg/client"
	"sigs.k8s.io/controller-runtime/pkg/log"

	opentoolsmfv1 "github.com/mahmoudk1000/azdo-runner-operator/api/v1"
	"github.com/mahmoudk1000/azdo-runner-operator/internal/azdo"
)

// RunnerPoolReconciler reconciles a RunnerPool object
// This is the core of the operator - it implements the reconciliation loop
type RunnerPoolReconciler struct {
	// Client is the Kubernetes client for CRUD operations
	client.Client

	// Scheme is the runtime scheme containing all known types
	Scheme *runtime.Scheme

	// TODO: Add your service dependencies here:
	// AzDoClient     *azdo.Client
	// PodService     *kubernetes.PodService
	// PVCService     *kubernetes.PVCService
	// PollingService *azdo.PollingService
}

// RBAC permissions - these generate RBAC manifests
// +kubebuilder:rbac:groups=opentools.mf.opentools.mf,resources=runnerpools,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups=opentools.mf.opentools.mf,resources=runnerpools/status,verbs=get;update;patch
// +kubebuilder:rbac:groups=opentools.mf.opentools.mf,resources=runnerpools/finalizers,verbs=update
// TODO: Add RBAC for Pods, PVCs, Secrets
// +kubebuilder:rbac:groups="",resources=pods,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups="",resources=persistentvolumeclaims,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups="",resources=secrets,verbs=get;list;watch

// Reconcile is the main reconciliation loop
// This function is called whenever a RunnerPool resource changes
// Parameters:
//   - ctx: Context for cancellation
//   - req: The reconciliation request containing namespace/name of the resource
//
// Returns:
//   - ctrl.Result: Contains requeue information
//   - error: Any error that occurred
//
// The reconciliation loop should be idempotent - calling it multiple times
// with the same input should produce the same result
func (r *RunnerPoolReconciler) Reconcile(
	ctx context.Context,
	req ctrl.Request,
) (ctrl.Result, error) {
	log := log.FromContext(ctx)

	log.Info("Reconciling RunnerPool", "namespace", req.Namespace, "name", req.Name)

	// TODO: Step 1 - Fetch the RunnerPool resource
	// Get the RunnerPool from Kubernetes API
	runnerPool := &opentoolsmfv1.RunnerPool{}
	err := r.Get(ctx, req.NamespacedName, runnerPool)
	if err != nil {
		if errors.IsNotFound(err) {
			log.Info("RunnerPool resource not found. Ignoring since object must be deleted.")
			return ctrl.Result{}, nil
		}
		log.Error(err, "failed to get RunnerPool")
		return ctrl.Result{}, err
	}

	// TODO: Step 2 - Handle deletion (finalizers)
	// Check if the resource is being deleted
	if !runnerPool.DeletionTimestamp.IsZero() {
		// Resource is being deleted
		// TODO: Implement cleanup logic:
		// 1. Unregister all agents from Azure DevOps
		// 2. Delete all pods
		// 3. Delete PVCs if configured to do so
		// 4. Unregister from polling service
		// 5. Remove finalizer so resource can be deleted

		log.Info("RunnerPool is being deleted, cleaning up resources")

		// TODO: Call cleanup methods

		return ctrl.Result{}, nil
	}

	// TODO: Step 3 - Add finalizer if not present
	// Finalizers prevent deletion until cleanup is complete
	// if !controllerutil.ContainsFinalizer(&runnerPool, finalizerName) {
	//     controllerutil.AddFinalizer(&runnerPool, finalizerName)
	//     if err := r.Update(ctx, &runnerPool); err != nil {
	//         return ctrl.Result{}, err
	//     }
	// }

	// TODO: Step 4 - Get PAT from secret
	// Read the Personal Access Token from the referenced secret
	// pat, err := r.getPATFromSecret(ctx, &runnerPool)
	// if err != nil {
	//     log.Error(err, "Failed to get PAT from secret")
	//     // Update status to show error
	//     return ctrl.Result{RequeueAfter: 30 * time.Second}, nil
	// }

	patToken, err := r.getPATToken(ctx, runnerPool)
	if err != nil {
		log.Error(err, "secret: ", runnerPool.Spec.PATSecretName, "does not exist or is invalid")
		return ctrl.Result{RequeueAfter: 30 * time.Second}, err
	}

	azdoClient, err := azdo.NewClient(runnerPool.Spec.AzURL, patToken)
	if err != nil {
		log.Error(err, "failed to create AzDO client")
		runnerPool.Status.LastError = fmt.Sprintf("failed to get PAT token: %v", err)
		runnerPool.Status.ConnectionStatus = "Error"
		return ctrl.Result{}, err
	}

	azdoPolling := azdo.NewPollingService(azdoClient)

	pollResult, err := azdoPolling.Poll(ctx, runnerPool.Spec.Pool)
	if err != nil {
		log.Error(err, "failed to poll AzDO for runner pool info")
		runnerPool.Status.ConnectionStatus = "Error"
		runnerPool.Status.LastError = err.Error()
		return ctrl.Result{RequeueAfter: 30 * time.Second}, err
	}

	if err := r.updateStatus(ctx, runnerPool, pollResult); err != nil {
		return ctrl.Result{}, err
	}

	// TODO: Step 5 - Test Azure DevOps connection
	// Verify we can connect to Azure DevOps with the provided credentials
	// if err := r.AzDoClient.TestConnection(ctx, runnerPool.Spec.AzDoURL, pat); err != nil {
	//     log.Error(err, "Failed to connect to Azure DevOps")
	//     // Update status
	//     return ctrl.Result{RequeueAfter: 30 * time.Second}, nil
	// }

	// TODO: Step 6 - Register with polling service
	// The polling service handles the continuous monitoring and scaling
	// r.PollingService.RegisterPool(
	//     runnerPool.Namespace,
	//     runnerPool.Name,
	//     pat,
	//     time.Duration(runnerPool.Spec.PollIntervalSeconds) * time.Second,
	// )

	// TODO: Step 7 - Update agent index tracking in status
	// Keep track of which agent indexes are in use
	// if err := r.updateAgentIndexTracking(ctx, &runnerPool); err != nil {
	//     log.Error(err, "Failed to update agent index tracking")
	// }

	// TODO: Step 8 - Update status
	// Update the status subresource with current state
	// if err := r.updateStatus(ctx, &runnerPool); err != nil {
	//     log.Error(err, "Failed to update status")
	//     return ctrl.Result{}, err
	// }

	log.Info("Reconciliation completed successfully")

	// Don't requeue - the polling service handles continuous monitoring
	// But requeue after some time as a safety net
	return ctrl.Result{RequeueAfter: 5 * time.Minute}, nil
}

func (r *RunnerPoolReconciler) getPATToken(
	ctx context.Context,
	rp *opentoolsmfv1.RunnerPool,
) (string, error) {
	var secret corev1.Secret
	secretKey := client.ObjectKey{
		Name:      rp.Spec.PATSecretName,
		Namespace: rp.Namespace,
	}

	if err := r.Get(ctx, secretKey, &secret); err != nil {
		return "", fmt.Errorf("failed to get PAT secret %s: %w", rp.Spec.PATSecretName, err)
	}

	token, ok := secret.Data["token"]
	if !ok {
		return "", fmt.Errorf("PAT secret %s is missing 'token' key", rp.Spec.PATSecretName)
	}

	return string(token), nil
}

func (r *RunnerPoolReconciler) updateStatus(
	ctx context.Context,
	rp *opentoolsmfv1.RunnerPool,
	pollResult *azdo.PollResult,
) error {
	rp.Status.ConnectionStatus = "Connected"
	rp.Status.OrganizationName = pollResult.OrganizationName
	rp.Status.PoolName = pollResult.PoolName
	rp.Status.LastPolled = metav1.Now()
	rp.Status.LastError = ""

	return r.Status().Update(ctx, rp)
}

// SetupWithManager sets up the controller with the Manager.
func (r *RunnerPoolReconciler) SetupWithManager(mgr ctrl.Manager) error {
	return ctrl.NewControllerManagedBy(mgr).
		For(&opentoolsmfv1.RunnerPool{}).
		Named("runnerpool").
		Complete(r)
}
