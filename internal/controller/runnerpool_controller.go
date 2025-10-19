package controller

import (
	"context"
	"fmt"
	"sort"
	"strings"
	"time"

	corev1 "k8s.io/api/core/v1"
	"k8s.io/apimachinery/pkg/api/errors"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/runtime"
	ctrl "sigs.k8s.io/controller-runtime"
	"sigs.k8s.io/controller-runtime/pkg/client"
	"sigs.k8s.io/controller-runtime/pkg/controller/controllerutil"
	"sigs.k8s.io/controller-runtime/pkg/log"

	karav1 "github.com/mahmoudk1000/azdo-runner-operator/api/v1"
	"github.com/mahmoudk1000/azdo-runner-operator/internal/devops"
	"github.com/mahmoudk1000/azdo-runner-operator/internal/runners"
)

var finalizer = karav1.GroupVersion.Group + "/finalizer"

// RunnerPoolReconciler reconciles a RunnerPool object
// This is the core of the operator — it implements the reconciliation loop
// for autoscaling Azure DevOps runner agents as Kubernetes pods.
type RunnerPoolReconciler struct {
	client.Client
	Scheme     *runtime.Scheme
	AzDoClient *devops.Client
	PodService *runners.Runner
}

// +kubebuilder:rbac:groups=kara.felukka.org,resources=runnerpools,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups=kara.felukka.org,resources=runnerpools/status,verbs=get;update;patch
// +kubebuilder:rbac:groups=kara.felukka.org,resources=runnerpools/finalizers,verbs=update
// +kubebuilder:rbac:groups="",resources=pods,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups="",resources=persistentvolumeclaims,verbs=get;list;watch;create;update;patch;delete
// +kubebuilder:rbac:groups="",resources=secrets,verbs=get;list;watch

// Reconcile is the main reconciliation loop.
// This function is called whenever a RunnerPool resource changes.
// The reconciliation loop is idempotent — calling it multiple times
// with the same input should produce the same result.
func (r *RunnerPoolReconciler) Reconcile(
	ctx context.Context,
	req ctrl.Request,
) (ctrl.Result, error) {
	log := log.FromContext(ctx)

	log.Info("Reconciling RunnerPool", "namespace", req.Namespace, "name", req.Name)

	// Step 1: Get the RunnerPool resource
	runnerPool := &karav1.RunnerPool{}
	err := r.Get(ctx, req.NamespacedName, runnerPool)
	if err != nil {
		if errors.IsNotFound(err) {
			log.Info("RunnerPool resource not found. Ignoring since object must be deleted.")
			return ctrl.Result{}, nil
		}
		log.Error(err, "failed to get RunnerPool")
		return ctrl.Result{}, err
	}

	// Step 2: Get the PAT token from the referenced secret
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

	// Step 3: Create the Azure DevOps client
	r.AzDoClient, err = devops.NewClient(runnerPool.Spec.AzURL, patToken)
	if err != nil {
		log.Error(err, "Failed to create AzDo client")
		runnerPool.Status.LastError = fmt.Sprintf("failed to create AzDO client: %v", err)
		runnerPool.Status.ConnectionStatus = "Error"
		if statusErr := r.Status().Update(ctx, runnerPool); statusErr != nil {
			log.Error(statusErr, "Failed to update status")
		}
		return ctrl.Result{RequeueAfter: 30 * time.Second}, err
	}
	defer r.AzDoClient.Close()

	runnerPool.Status.ConnectionStatus = "Connected"
	runnerPool.Status.LastError = ""
	runnerPool.Status.LastPolled = metav1.Now()
	if statusErr := r.Status().Update(ctx, runnerPool); statusErr != nil {
		log.Error(statusErr, "Failed to update connection status")
	}

	// Step 4 (deletion): Check if the RunnerPool is being deleted
	if !runnerPool.DeletionTimestamp.IsZero() {
		log.Info("RunnerPool is being deleted, cleaning up resources")
		if err := r.cleanupDeletion(ctx, runnerPool, req.Namespace, req.Name); err != nil {
			log.Error(err, "failed to clean up resources during deletion")
			return ctrl.Result{}, err
		}

		if controllerutil.RemoveFinalizer(runnerPool, finalizer) {
			if updateErr := r.Update(ctx, runnerPool); updateErr != nil {
				log.Error(updateErr, "Failed to remove finalizer from RunnerPool")
				return ctrl.Result{}, updateErr
			}
		}
		log.Info("RunnerPool deleted successfully")
		return ctrl.Result{}, nil
	}

	// Step 4 (creation/update): Add finalizer if not present
	if !controllerutil.ContainsFinalizer(runnerPool, finalizer) {
		if !controllerutil.AddFinalizer(runnerPool, finalizer) {
			return ctrl.Result{}, fmt.Errorf("failed to add finalizer to RunnerPool %s", runnerPool.Name)
		}
		if updateErr := r.Update(ctx, runnerPool); updateErr != nil {
			log.Error(updateErr, "failed to add finalizer to RunnerPool")
			return ctrl.Result{}, updateErr
		}
		log.Info("Added finalizer to RunnerPool")
		// Return to allow the update to take effect before proceeding
		return ctrl.Result{}, nil
	}

	// Step 5: Poll Azure DevOps for pool information and job queue
	pollingService := devops.NewPollingService(r.AzDoClient)
	pollResult, err := pollingService.Poll(ctx, runnerPool.Spec.Pool)
	if err != nil {
		log.Error(err, "failed to poll Azure DevOps")
		runnerPool.Status.LastError = fmt.Sprintf("polling failed: %v", err)
		runnerPool.Status.ConnectionStatus = "Error"
		if statusErr := r.Status().Update(ctx, runnerPool); statusErr != nil {
			log.Error(statusErr, "Failed to update status after polling error")
		}
		return ctrl.Result{RequeueAfter: 30 * time.Second}, err
	}

	// Step 6: Update status with poll results
	runnerPool.Status.PoolName = pollResult.PoolName
	runnerPool.Status.OrganizationName = pollResult.OrganizationName
	runnerPool.Status.LastPolled = metav1.Now()
	runnerPool.Status.LastError = ""
	runnerPool.Status.ConnectionStatus = "Connected"

	// Set conditions based on poll results
	setCondition(&runnerPool.Status, karav1.ConditionTypeAvailable, "True",
		"PollSuccessful", fmt.Sprintf("Pool %s has %d online agents, %d queued jobs",
			pollResult.PoolName, pollResult.OnlineAgents, pollResult.QueuedJobs))
	if statusErr := r.Status().Update(ctx, runnerPool); statusErr != nil {
		log.Error(statusErr, "Failed to update status with poll results")
	}
	log.Info("Poll results",
		"pool", pollResult.PoolName,
		"queuedJobs", pollResult.QueuedJobs,
		"runningJobs", pollResult.RunningJobs,
		"totalAgents", pollResult.TotalAgents,
		"onlineAgents", pollResult.OnlineAgents)

	// Step 7 & 8: Scale agents based on queued jobs and min/max constraints
	if err := r.reconcileAgents(ctx, runnerPool, pollResult); err != nil {
		log.Error(err, "failed to reconcile agents")
		runnerPool.Status.LastError = fmt.Sprintf("agent reconciliation failed: %v", err)
		if statusErr := r.Status().Update(ctx, runnerPool); statusErr != nil {
			log.Error(statusErr, "Failed to update status after agent reconciliation error")
		}
		// Requeue to retry
		return ctrl.Result{RequeueAfter: 15 * time.Second}, nil
	}

	// Success — requeue after a short interval for continuous scaling
	return ctrl.Result{RequeueAfter: 15 * time.Second}, nil
}

// cleanupDeletion removes all Azure DevOps agents and Kubernetes pods
// when a RunnerPool is being deleted.
func (r *RunnerPoolReconciler) cleanupDeletion(
	ctx context.Context,
	runnerPool *karav1.RunnerPool,
	namespace, name string,
) error {
	log := log.FromContext(ctx)

	// Get the pool and list all agents
	pool, err := r.AzDoClient.GetPool(ctx, runnerPool.Spec.Pool)
	if err != nil {
		// Pool might already be gone — just clean up pods
		log.Error(err, "failed to get pool ID during deletion, cleaning up pods only")
	} else if pool.Id != nil {
		// List all AzDO agents and delete matching ones
		agents, err := r.AzDoClient.ListAgents(ctx, *pool.Id)
		if err != nil {
			log.Error(err, "failed to list agents during deletion")
		} else {
			for _, a := range *agents {
				if a.Name != nil && strings.HasPrefix(*a.Name, name+"-agent-") {
					if delErr := r.AzDoClient.DeleteAgent(ctx, *pool.Id, *a.Id); delErr != nil {
						log.Error(delErr, "failed to delete AzDO agent during cleanup", "agentName", *a.Name)
					}
				}
			}
		}
	}

	// Delete all runner pods for this pool
	allPods, err := r.PodService.GetAllRunnerPods(ctx, runnerPool)
	if err != nil {
		log.Error(err, "failed to list runner pods during deletion")
	}

	for _, pod := range allPods {
		if delErr := r.PodService.DeletePod(ctx, namespace, pod.Name); delErr != nil {
			log.Error(delErr, "failed to delete pod during cleanup", "podName", pod.Name)
		} else {
			log.Info("deleted runner pod during cleanup", "podName", pod.Name)
		}
	}

	return nil
}

// reconcileAgents ensures the correct number of runner pods are running
// based on queued jobs, minAgents, and maxAgents constraints.
// In capability-aware mode, agents are scaled per-capability based on
// job demands returned by the AzDO polling service.
func (r *RunnerPoolReconciler) reconcileAgents(
	ctx context.Context,
	runnerPool *karav1.RunnerPool,
	pollResult *devops.PollResult,
) error {
	log := log.FromContext(ctx)

	activePods, err := r.PodService.GetActivePods(ctx, runnerPool)
	if err != nil {
		return fmt.Errorf("failed to get active pods: %w", err)
	}

	minAgents := runnerPool.Spec.MinAgents
	maxAgents := runnerPool.Spec.MaxAgents

	// Detect capability-aware mode
	if runnerPool.Spec.CapabilityAware && len(runnerPool.Spec.CapabilityImages) > 0 {
		log.Info("Reconciling with capability-aware mode",
			"capabilities", len(runnerPool.Spec.CapabilityImages),
			"demands", pollResult.QueuedJobsByCapability)
		return r.reconcileCapabilities(ctx, runnerPool, activePods, pollResult, minAgents, maxAgents)
	}

	// Legacy mode: single image, total count scaling
	log.Info("Reconciling in legacy mode (non-capability-aware)")
	return r.reconcileLegacy(ctx, runnerPool, activePods, pollResult, minAgents, maxAgents)
}

// reconcileCapabilities scales agents per capability based on job demands.
// For each capability with pending jobs, it ensures enough agents with that
// capability are running, using the image from CapabilityImages.
func (r *RunnerPoolReconciler) reconcileCapabilities(
	ctx context.Context,
	runnerPool *karav1.RunnerPool,
	activePods []corev1.Pod,
	pollResult *devops.PollResult,
	minAgents, maxAgents int,
) error {
	log := log.FromContext(ctx)

	// Count current agents per capability
	currentByCap := make(map[string]int)
	for _, pod := range activePods {
		if cap, ok := pod.Labels["capability"]; ok {
			currentByCap[cap]++
		}
	}

	totalCurrent := len(activePods)
	totalCreated := 0
	totalDeleted := 0

	// Process each defined capability
	for capName, capImage := range runnerPool.Spec.CapabilityImages {
		demand := pollResult.QueuedJobsByCapability[capName]
		currentCount := currentByCap[capName]

		// Calculate desired count: 1 agent per queued job, but at least 0
		// Cap at maxAgents / numCapabilities to distribute fairly
		numCaps := len(runnerPool.Spec.CapabilityImages)
		capMax := maxAgents / numCaps
		if capMax == 0 {
			capMax = maxAgents
		}

		desired := currentCount + demand
		if desired > capMax {
			desired = capMax
		}

		// Track total desired to enforce overall max
		totalDesiredSoFar := totalCreated
		for _, name := range sortedCapabilityNames(runnerPool.Spec.CapabilityImages) {
			if name == capName {
				break
			}
			totalDesiredSoFar += currentByCap[name]
		}
		totalDesiredSoFar += desired

		if totalDesiredSoFar > maxAgents {
			adjustment := max(0, maxAgents-totalDesiredSoFar)
			desired = currentCount + demand - adjustment
			if desired < 0 {
				desired = currentCount
			}
		}

		log.Info("Capability scaling decision",
			"capability", capName,
			"demand", demand,
			"current", currentCount,
			"desired", desired,
			"image", capImage)

		// Scale up: create new pods with this capability
		for i := currentCount; i < desired; i++ {
			idx, err := r.PodService.GetNextAvailableIndex(ctx, runnerPool)
			if err != nil {
				return fmt.Errorf("failed to get next available index: %w", err)
			}
			_, err = r.PodService.CreatePod(ctx, runnerPool, idx, i < minAgents, capName,
				map[string]string{capName: "true"}, capImage)
			if err != nil {
				return fmt.Errorf("failed to create pod for capability %s: %w", capName, err)
			}
			totalCreated++
			log.Info("Created capability-aware agent pod",
				"capability", capName,
				"image", capImage)
		}

		// Scale down: delete excess pods
		for i := desired; i < currentCount; i++ {
			// Find a pod with this capability label to delete
			for _, pod := range activePods {
				if pod.Labels["capability"] == capName {
					podName := pod.Name
					log.Info("Deleting excess capability agent pod",
						"capability", capName,
						"podName", podName)
					if err := r.PodService.DeletePod(ctx, runnerPool.Namespace, podName); err != nil {
						log.Error(err, "failed to delete excess pod", "podName", podName)
					}
					totalDeleted++
					break // One pod per iteration
				}
			}
		}
	}

	// Handle default/minAgents: if total desired < minAgents, add generic agents
	totalDesired := 0
	for _, count := range currentByCap {
		totalDesired += count
	}
	totalDesired += totalCreated - totalDeleted

	if totalDesired < minAgents {
		defaultImage := runnerPool.Spec.Image
		needed := minAgents - totalDesired
		for i := 0; i < needed; i++ {
			_, err := r.PodService.CreatePod(ctx, runnerPool, totalCurrent+totalCreated+i, true, "", nil, defaultImage)
			if err != nil {
				return fmt.Errorf("failed to create default agent pod: %w", err)
			}
			log.Info("Created default agent pod for minAgents", "count", needed)
		}
	}

	if totalCreated > 0 || totalDeleted > 0 {
		log.Info("Capability-aware reconciliation complete",
			"created", totalCreated,
			"deleted", totalDeleted,
			"totalDesired", totalDesired)
	}

	return nil
}

// reconcileLegacy is the original single-image scaling logic for backward compatibility.
func (r *RunnerPoolReconciler) reconcileLegacy(
	ctx context.Context,
	runnerPool *karav1.RunnerPool,
	activePods []corev1.Pod,
	pollResult *devops.PollResult,
	minAgents, maxAgents int,
) error {
	log := log.FromContext(ctx)

	currentAgentCount := len(activePods)
	queuedJobs := pollResult.QueuedJobs

	// Calculate desired agent count
	desiredCount := currentAgentCount
	desiredCount = max(desiredCount, minAgents)
	desiredCount = min(desiredCount, maxAgents)
	neededForJobs := minAgents + queuedJobs
	if neededForJobs > desiredCount && neededForJobs <= maxAgents {
		desiredCount = neededForJobs
	}

	log.Info("Agent reconciliation (legacy mode)",
		"current", currentAgentCount,
		"desired", desiredCount,
		"min", minAgents,
		"max", maxAgents,
		"queuedJobs", queuedJobs)

	// Scale up: create new pods
	for i := currentAgentCount; i < desiredCount; i++ {
		isMinAgent := i < minAgents
		var capability string
		if runnerPool.Spec.CapabilityAware && len(runnerPool.Spec.Capabilities) > 0 {
			// Legacy capability mode: assign first capability
			for _, cap := range runnerPool.Spec.Capabilities {
				capability = cap
				break
			}
		}

		log.Info("Creating runner agent pod",
			"index", i,
			"isMinAgent", isMinAgent,
			"capability", capability)

		_, err := r.PodService.CreatePod(ctx, runnerPool, i, isMinAgent, capability, nil, "")
		if err != nil {
			return fmt.Errorf("failed to create pod for agent index %d: %w", i, err)
		}
		log.Info("Created runner agent pod", "index", i)
	}

	// Scale down: delete excess non-min-agent pods
	for i := desiredCount; i < currentAgentCount; i++ {
		if i >= minAgents {
			podName := fmt.Sprintf("%s-agent-%d", runnerPool.Name, i)
			log.Info("Deleting excess runner agent pod", "podName", podName)
			if err := r.PodService.DeletePod(ctx, runnerPool.Namespace, podName); err != nil {
				log.Error(err, "failed to delete excess pod", "podName", podName)
			}
		}
	}

	return nil
}

// sortedCapabilityNames returns capability names sorted for deterministic ordering.
func sortedCapabilityNames(m map[string]string) []string {
	names := make([]string, 0, len(m))
	for k := range m {
		names = append(names, k)
	}
	sort.Strings(names)
	return names
}

// setCondition updates the RunnerPool status conditions.
func setCondition(status *karav1.RunnerPoolStatus, condType karav1.ConditionType, statusVal metav1.ConditionStatus, reason, message string) {
	cond := metav1.Condition{
		Type:               string(condType),
		Status:             statusVal,
		LastTransitionTime: metav1.Now(),
		Reason:             reason,
		Message:            message,
	}

	// Update or add the condition
	for i, c := range status.Conditions {
		if c.Type == string(condType) {
			if c.Status != cond.Status {
				status.Conditions[i] = cond
			}
			return
		}
	}
	status.Conditions = append(status.Conditions, cond)
}

// getPATToken reads the Azure DevOps PAT from a Kubernetes secret.
func (r *RunnerPoolReconciler) getPATToken(
	ctx context.Context,
	rp *karav1.RunnerPool,
) (string, error) {
	var secret corev1.Secret
	secretKey := client.ObjectKey{
		Name:      rp.Spec.PATSecretName,
		Namespace: rp.Namespace,
	}

	if err := r.Get(ctx, secretKey, &secret); err != nil {
		return "", fmt.Errorf("failed to get PAT secret %s/%s: %w", rp.Namespace, rp.Spec.PATSecretName, err)
	}

	token, ok := secret.Data["token"]
	if !ok {
		return "", fmt.Errorf("PAT secret %s/%s is missing 'token' key", rp.Namespace, rp.Spec.PATSecretName)
	}

	return string(token), nil
}

// SetupWithManager sets up the controller with the Manager.
func (r *RunnerPoolReconciler) SetupWithManager(mgr ctrl.Manager) error {
	return ctrl.NewControllerManagedBy(mgr).
		For(&karav1.RunnerPool{}).
		Named("runnerpool").
		Complete(r)
}
