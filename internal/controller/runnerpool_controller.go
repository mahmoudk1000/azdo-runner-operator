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

package controller

import (
	"context"
	"fmt"
	"time"

	corev1 "k8s.io/api/core/v1"
	"k8s.io/apimachinery/pkg/runtime"
	"k8s.io/apimachinery/pkg/types"
	ctrl "sigs.k8s.io/controller-runtime"
	"sigs.k8s.io/controller-runtime/pkg/client"
	"sigs.k8s.io/controller-runtime/pkg/log"

	opentoolsmfv1 "github.com/mahmoudk1000/azdo-runner-operator/api/v1"
	"github.com/mahmoudk1000/azdo-runner-operator/internal/azdo"
)

// RunnerPoolReconciler reconciles a RunnerPool object
type RunnerPoolReconciler struct {
	client.Client
	Scheme *runtime.Scheme
}

// +kubebuilder:rbac:groups=opentools.mf.opentools.mf,resources=runnerpools,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups=opentools.mf.opentools.mf,resources=runnerpools/status,verbs=get;update;patch
// +kubebuilder:rbac:groups=opentools.mf.opentools.mf,resources=runnerpools/finalizers,verbs=update

func (r *RunnerPoolReconciler) Reconcile(
	ctx context.Context,
	req ctrl.Request,
) (ctrl.Result, error) {
	log := log.FromContext(ctx)

	var runnerPool opentoolsmfv1.RunnerPool
	if err := r.Get(ctx, types.NamespacedName{Namespace: req.Namespace, Name: runnerPool.Name}, &runnerPool); err != nil {
		return ctrl.Result{}, client.IgnoreNotFound(err)
	}

	if !runnerPool.ObjectMeta.DeletionTimestamp.IsZero() {
		r.Delete(ctx, &runnerPool)
		return ctrl.Result{}, nil
	}

	patToken, err := r.getPATToken(ctx, &runnerPool)
	if err != nil {
		log.Error(err, "secret: ", runnerPool.Spec.PATSecretName, "does not exist or is invalid")
	}

	azdoClient, err := azdo.NewClient(runnerPool.Spec.AzURL, patToken)
	if err != nil {
		log.Error(err, "failed to create AzDO client")
		runnerPool.Status.LastError = fmt.Sprintf("failed to get PAT token: %v", err)
		runnerPool.Status.ConnectionStatus = "Error"
	}

	azdoPolling := azdo.NewPollingService(azdoClient)

	pollResult, err := azdoPolling.Poll(ctx, runnerPool.Spec.Pool)
	if err != nil {
		log.Error(err, "failed to poll AzDO for runner pool info")
		runnerPool.Status.ConnectionStatus = "Error"
		runnerPool.Status.LastError = err.Error()
		return ctrl.Result{RequeueAfter: 30 * time.Second}, err
	}

	if err := r.updateStatus(ctx, &runnerPool, pollResult); err != nil {
		return ctrl.Result{}, err
	}

	return ctrl.Result{}, nil
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
	rp.Status.LastPolled = time.Now()
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
