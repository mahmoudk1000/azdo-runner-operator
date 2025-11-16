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
	"sigs.k8s.io/controller-runtime/pkg/controller/controllerutil"
	"sigs.k8s.io/controller-runtime/pkg/log"

	opentoolsmfv1 "github.com/mahmoudk1000/azdo-runner-operator/api/v1"
	"github.com/mahmoudk1000/azdo-runner-operator/internal/azdo"
	"github.com/mahmoudk1000/azdo-runner-operator/internal/kubernetes"
)

var finalizer = opentoolsmfv1.GroupVersion.Group + "/finalizer"

// RunnerPoolReconciler reconciles a RunnerPool object
// This is the core of the operator - it implements the reconciliation loop
type RunnerPoolReconciler struct {
	client.Client
	Scheme *runtime.Scheme
	// TODO: Add your service dependencies here:
	AzDoClient *azdo.Client
	PodService *kubernetes.PodService
	// PollingService *azdo.PollingService
}

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

	patToken, err := r.getPATToken(ctx, runnerPool)
	if err != nil {
		log.Error(err, "Failed to get PAT from secret", "secret", runnerPool.Spec.PATSecretName)
		runnerPool.Status.LastError = fmt.Sprintf("failed to get PAT token: %v", err)
		runnerPool.Status.ConnectionStatus = "Error"
		if statusErr := r.Status().Update(ctx, runnerPool); statusErr != nil {
			log.Error(statusErr, "Failed to update status")
		}
		return ctrl.Result{RequeueAfter: 30 * time.Second}, err
	}

	r.AzDoClient, err = azdo.NewClient(runnerPool.Spec.AzURL, patToken)
	if err != nil {
		log.Error(err, "Failed to create AzDo client")
		runnerPool.Status.LastError = fmt.Sprintf("failed to create AzDO client: %v", err)
		runnerPool.Status.ConnectionStatus = "Error"
		if statusErr := r.Status().Update(ctx, runnerPool); statusErr != nil {
			log.Error(statusErr, "Failed to update status")
		}
		return ctrl.Result{RequeueAfter: 30 * time.Second}, err
	} else {
		runnerPool.Status.ConnectionStatus = "Connected"
		runnerPool.Status.LastError = ""
		runnerPool.Status.LastPolled = metav1.Now()
		if statusErr := r.Status().Update(ctx, runnerPool); statusErr != nil {
			log.Info("status update after successful client creation failed")
		}
	}
	defer r.AzDoClient.Close()

	if !runnerPool.DeletionTimestamp.IsZero() {
		log.Info("RunnerPool is being deleted, cleaning up resources")
		pool, err := r.AzDoClient.GetPool(ctx, runnerPool.Spec.Pool)
		if err != nil {
			log.Error(err, "failed to get pool ID during deletion")
			runnerPool.Status.LastError = "failed to get pool ID during deletion"
			if statusErr := r.Status().Update(ctx, runnerPool); statusErr != nil {
				log.Error(statusErr, "Failed to update status")
			}
		}
		agents, err := r.AzDoClient.ListAgents(ctx, *pool.Id)
		if err != nil {
			log.Error(err, "failed to fetch agents during deletion")
		}
		for _, a := range *agents {
			err := r.AzDoClient.DeleteAgent(ctx, *pool.Id, *a.Id)
			if err != nil {
				log.Error(err, "failed to delete agent during deletion", "agentID", *a.Id)
			}
			err = r.PodService.DeletePod(ctx, req.Namespace, *a.Name)
			if err != nil {
				log.Error(err, "failed to delete pod during deletion", "podName", *a.Name)
			}
		}

		if controllerutil.RemoveFinalizer(runnerPool, finalizer) {
			if statusErr := r.Status().Update(ctx, runnerPool); statusErr != nil {
				log.Error(statusErr, "Failed to update status during finalizer removal")
			}
		}

		return ctrl.Result{}, nil
	}

	// TODO: Step 4 - Add finalizer if not present
	// Finalizers prevent deletion until cleanup is complete
	// 1. Check if finalizer exists using controllerutil.ContainsFinalizer
	// 2. If not present, add it using controllerutil.AddFinalizer
	// 3. Update the resource in Kubernetes API
	// 4. Return if update fails
	if !controllerutil.ContainsFinalizer(runnerPool, finalizer) {
		controllerutil.AddFinalizer(runnerPool, finalizer)
		if statusErr := r.Status().Update(ctx, runnerPool); statusErr != nil {
			log.Error(statusErr, "failed to add finalizer to RunnerPool", runnerPool.Name)
			return ctrl.Result{}, nil
		}
	}

	// TODO: Step 5 - Poll Azure DevOps for pool information
	// Query Azure DevOps to get current state of the runner pool
	// 1. Create a new PollingService with the AzDoClient
	// 2. Call Poll() with the pool name from runnerPool.Spec.Pool
	// 3. If error occurs:
	//    - Log the error
	//    - Update status with ConnectionStatus = "Error" and LastError
	//    - Update the status in Kubernetes
	//    - Requeue after 30 seconds
	// 4. Store the poll result for next step

	// TODO: Step 6 - Update status with poll results
	// Update the RunnerPool status with information from Azure DevOps
	// 1. Call updateStatus helper function with poll result
	// 2. If error occurs, log it and return the error
	// 3. This keeps Kubernetes status in sync with Azure DevOps state

	// TODO: Step 7 - Register with polling service
	// The polling service handles continuous monitoring and scaling
	// 1. Call RegisterPool on the PollingService
	// 2. Pass namespace, name, PAT token, and poll interval
	// 3. Convert PollIntervalSeconds to time.Duration
	// 4. This starts background monitoring for this RunnerPool

	// TODO: Step 8 - Update agent index tracking in status
	// Keep track of which agent indexes are in use
	// 1. Call updateAgentIndexTracking helper function
	// 2. This tracks which agent numbers (0, 1, 2, etc.) are assigned
	// 3. Prevents duplicate agent names in Azure DevOps
	// 4. Log any errors but don't fail reconciliation

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

// SetupWithManager sets up the controller with the Manager.
func (r *RunnerPoolReconciler) SetupWithManager(mgr ctrl.Manager) error {
	return ctrl.NewControllerManagedBy(mgr).
		For(&opentoolsmfv1.RunnerPool{}).
		Named("runnerpool").
		Complete(r)
}
