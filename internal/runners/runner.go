// File: runners/runner.go
// Description: Manages Kubernetes pods for Azure DevOps runner agents.
// Handles pod creation, deletion, listing, and lifecycle for autoscaling.
// Dependencies: controller-runtime client, corev1 API, api/v1 types.
package runners

import (
	"context"
	"fmt"
	"maps"
	"strconv"

	corev1 "k8s.io/api/core/v1"
	"k8s.io/apimachinery/pkg/api/resource"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"sigs.k8s.io/controller-runtime/pkg/client"

	karav1 "github.com/mahmoudk1000/azdo-runner-operator/api/v1"
)

// Runner manages all pod-related operations for runner agents.
// Each Azure DevOps agent runs in a separate Kubernetes pod.
type Runner struct {
	client client.Client
}

// NewPodService creates a new Runner service with the given Kubernetes client.
func NewPodService(client client.Client) *Runner {
	return &Runner{
		client: client,
	}
}

// CreatePod creates a new runner agent pod for the given RunnerPool.
// When capabilityAware mode is enabled, capabilities map defines what
// capabilities this agent will register (e.g. {"docker": "true"}).
// imageOverride allows using a different image than Spec.Image for
// capability-specific agents.
func (s *Runner) CreatePod(
	ctx context.Context,
	runnerPool *karav1.RunnerPool,
	index int,
	isMinAgent bool,
	capability string,
	capabilities map[string]string,
	imageOverride string,
) (*corev1.Pod, error) {
	pod := s.buildPodSpec(runnerPool, index, isMinAgent, capability, capabilities, imageOverride)

	err := s.client.Create(ctx, pod)
	if err != nil {
		return nil, fmt.Errorf("failed to create pod %s: %w", pod.Name, err)
	}
	return pod, nil
}

// DeletePod deletes a runner agent pod by name.
func (s *Runner) DeletePod(ctx context.Context, namespace, name string) error {
	pod := &corev1.Pod{}
	err := s.client.Get(ctx, client.ObjectKey{Name: name, Namespace: namespace}, pod)
	if err != nil {
		if client.IgnoreNotFound(err) != nil {
			return fmt.Errorf("failed to get pod %s/%s for deletion: %w", namespace, name, err)
		}
		return nil // Pod already gone
	}

	err = s.client.Delete(ctx, pod)
	if err != nil {
		return fmt.Errorf("failed to delete pod %s/%s: %w", namespace, name, err)
	}
	return nil
}

// GetAllRunnerPods lists all pods belonging to a RunnerPool.
func (s *Runner) GetAllRunnerPods(
	ctx context.Context,
	runnerPool *karav1.RunnerPool,
) ([]corev1.Pod, error) {
	podList := &corev1.PodList{}
	err := s.client.List(ctx, podList,
		client.InNamespace(runnerPool.Namespace),
		client.MatchingLabels{"runner-pool": runnerPool.Name},
	)
	if err != nil {
		return nil, fmt.Errorf("failed to list pods for RunnerPool %s: %w", runnerPool.Name, err)
	}
	return podList.Items, nil
}

// GetActivePods returns all running or pending pods for a RunnerPool.
func (s *Runner) GetActivePods(
	ctx context.Context,
	runnerPool *karav1.RunnerPool,
) ([]corev1.Pod, error) {
	allPods, err := s.GetAllRunnerPods(ctx, runnerPool)
	if err != nil {
		return nil, err
	}

	active := make([]corev1.Pod, 0)
	for _, pod := range allPods {
		if pod.Status.Phase == corev1.PodRunning || pod.Status.Phase == corev1.PodPending {
			active = append(active, pod)
		}
	}
	return active, nil
}

// GetMinAgentPods returns all pods marked as minimum agents.
func (s *Runner) GetMinAgentPods(
	ctx context.Context,
	runnerPool *karav1.RunnerPool,
) ([]corev1.Pod, error) {
	allPods, err := s.GetAllRunnerPods(ctx, runnerPool)
	if err != nil {
		return nil, err
	}

	minPods := make([]corev1.Pod, 0)
	for _, pod := range allPods {
		if pod.Labels != nil && pod.Labels["min-agent"] == "true" {
			minPods = append(minPods, pod)
		}
	}
	return minPods, nil
}

// GetNextAvailableIndex finds the next available agent index number.
func (s *Runner) GetNextAvailableIndex(
	ctx context.Context,
	runnerPool *karav1.RunnerPool,
) (int, error) {
	allPods, err := s.GetAllRunnerPods(ctx, runnerPool)
	if err != nil {
		return 0, err
	}

	used := make(map[int]bool)
	for _, pod := range allPods {
		if pod.Labels != nil {
			if idxStr, ok := pod.Labels["agent-index"]; ok {
				if idx, err := strconv.Atoi(idxStr); err == nil {
					used[idx] = true
				}
			}
		}
	}

	// Find smallest unused index
	idx := 0
	for used[idx] {
		idx++
	}
	return idx, nil
}

func (s *Runner) UpdatePodLabels(
	ctx context.Context,
	namespace, name string,
	labels map[string]string,
) error {
	pod := &corev1.Pod{}
	if err := s.client.Get(ctx, client.ObjectKey{Name: name, Namespace: namespace}, pod); err != nil {
		return fmt.Errorf("failed to get pod %s/%s for label update: %w", namespace, name, err)
	}

	if pod.Labels == nil {
		pod.Labels = make(map[string]string)
	}

	maps.Copy(pod.Labels, labels)

	if err := s.client.Update(ctx, pod); err != nil {
		return fmt.Errorf("failed to update labels on pod %s/%s: %w", namespace, name, err)
	}
	return nil
}

func (s *Runner) buildPodSpec(
	runnerPool *karav1.RunnerPool,
	index int,
	isMinAgent bool,
	capability string,
	capabilities map[string]string,
	imageOverride string,
) *corev1.Pod {
	agentName := fmt.Sprintf("%s-agent-%d", runnerPool.Name, index)

	// Build labels
	labels := map[string]string{
		"runner-pool": runnerPool.Name,
		"agent-index": strconv.Itoa(index),
		"min-agent":   strconv.FormatBool(isMinAgent),
	}
	if capability != "" {
		labels["capability"] = capability
	}

	// Determine image: use override if provided, otherwise Spec.Image
	image := runnerPool.Spec.Image
	if imageOverride != "" {
		image = imageOverride
	}

	// Build container with optional capability args
	container := s.buildContainer(runnerPool, agentName, image, capabilities)

	// Build volumes and mounts
	volumes := []corev1.Volume{}
	volumeMounts := []corev1.VolumeMount{}

	for _, storage := range runnerPool.Spec.Storage {
		vol, mount := s.buildStorageVolume(&storage, agentName)
		volumes = append(volumes, vol)
		volumeMounts = append(volumeMounts, mount)
	}

	// Add certificate trust store volumes
	for _, cert := range runnerPool.Spec.SecretTrustStore {
		vol, mount := s.buildCertTrustVolume(&cert, agentName)
		volumes = append(volumes, vol)
		volumeMounts = append(volumeMounts, mount)
	}

	container.VolumeMounts = volumeMounts
	pod := &corev1.Pod{
		ObjectMeta: metav1.ObjectMeta{
			Name:      agentName,
			Namespace: runnerPool.Namespace,
			Labels:    labels,
		},
		Spec: corev1.PodSpec{
			SecurityContext: &corev1.PodSecurityContext{
				RunAsUser:  &runnerPool.Spec.SecurityContext.RunAsUser,
				RunAsGroup: &runnerPool.Spec.SecurityContext.RunAsGroup,
				FSGroup:    &runnerPool.Spec.SecurityContext.FSGroup,
			},
			Containers: []corev1.Container{container},
			Volumes:    volumes,
		},
	}

	// Add init container if configured
	if runnerPool.Spec.InitContainerSpec.Image != "" {
		pod.Spec.InitContainers = []corev1.Container{
			{
				Name:            "init",
				Image:           runnerPool.Spec.InitContainerSpec.Image,
				ImagePullPolicy: corev1.PullAlways,
			},
		}
	}

	// Set owner reference for garbage collection
	s.setOwnerReference(pod, runnerPool)

	return pod
}

// buildContainer creates a container spec with optional capability arguments.
// The Azure Pipelines Agent accepts --capability <name> --capabilityValue <value> flags.
func (s *Runner) buildContainer(
	runnerPool *karav1.RunnerPool,
	agentName string,
	image string,
	capabilities map[string]string,
) corev1.Container {
	container := corev1.Container{
		Name:            "azdo-runner",
		Image:           image,
		ImagePullPolicy: corev1.PullPolicy(runnerPool.Spec.ImagePullPolicy),
		SecurityContext: s.buildContainerSecurityContext(runnerPool),
	}

	// Set command and args
	container.Env = s.buildEnvVars(runnerPool, agentName, capabilities)

	if len(capabilities) > 0 {
		// Capability-aware: use explicit command with capability args
		args := []string{"/azp/bin/Agent.Listener", "run"}
		for capName, capValue := range capabilities {
			args = append(args, "--capability", capName, "--capabilityValue", capValue)
		}
		container.Command = []string{}
		container.Args = args
	} else {
		// Legacy: no capabilities, just run
		container.Args = []string{"run"}
	}

	return container
}

// buildEnvVars constructs the environment variables for the runner agent container.
// When capabilities are provided, they are passed as Agent.<name> env vars
// as an alternative to command-line --capability flags.
func (s *Runner) buildEnvVars(runnerPool *karav1.RunnerPool, agentName string, capabilities map[string]string) []corev1.EnvVar {
	envs := []corev1.EnvVar{
		{
			Name:  "AZP_URL",
			Value: runnerPool.Spec.AzURL,
		},
		{
			Name:  "AZP_POOL",
			Value: runnerPool.Spec.Pool,
		},
		{
			Name:  "AZP_AGENTNAME",
			Value: agentName,
		},
		{
			Name: "AZP_TOKEN",
			ValueFrom: &corev1.EnvVarSource{
				SecretKeyRef: &corev1.SecretKeySelector{
					LocalObjectReference: corev1.LocalObjectReference{
						Name: runnerPool.Spec.PATSecretName,
					},
					Key: "token",
				},
			},
		},
		{
			Name:  "AZP_WORK",
			Value: "_work",
		},
	}

	// Add capabilities as env vars as fallback
	for name, value := range capabilities {
		envs = append(envs, corev1.EnvVar{
			Name:  "Agent." + name,
			Value: value,
		})
	}

	// Add extra environment variables from spec
	for _, extra := range runnerPool.Spec.ExtraEnv {
		envVar := corev1.EnvVar{
			Name: extra.Name,
		}
		if extra.Value != nil {
			envVar.Value = *extra.Value
		}
		if extra.ValueFrom != nil {
			if extra.ValueFrom.SecretKeyRef != nil {
				envVar.ValueFrom = &corev1.EnvVarSource{
					SecretKeyRef: &corev1.SecretKeySelector{
						LocalObjectReference: corev1.LocalObjectReference{
							Name: extra.ValueFrom.SecretKeyRef.Name,
						},
						Key: extra.ValueFrom.SecretKeyRef.Key,
					},
				}
			} else if extra.ValueFrom.ConfigMapKeyRef != nil {
				envVar.ValueFrom = &corev1.EnvVarSource{
					ConfigMapKeyRef: &corev1.ConfigMapKeySelector{
						LocalObjectReference: corev1.LocalObjectReference{
							Name: extra.ValueFrom.ConfigMapKeyRef.Name,
						},
						Key: extra.Name, // Use env name as key if not specified
					},
				}
			}
		}
		envs = append(envs, envVar)
	}

	return envs
}

// buildContainerSecurityContext creates the security context for the runner container.
func (s *Runner) buildContainerSecurityContext(runnerPool *karav1.RunnerPool) *corev1.SecurityContext {
	return &corev1.SecurityContext{
		Privileged: &runnerPool.Spec.SecurityContext.Privileged,
	}
}

// buildStorageVolume creates a volume and volume mount from a StorageSpec.
func (s *Runner) buildStorageVolume(storage *karav1.StorageSpec, agentName string) (corev1.Volume, corev1.VolumeMount) {
	volume := corev1.Volume{
		Name: storage.Name,
	}
	volumeMount := corev1.VolumeMount{
		Name:      storage.Name,
		MountPath: storage.MountPath,
	}

	if storage.ClaimName != "" {
		// Use existing PVC by name
		volume.PersistentVolumeClaim = &corev1.PersistentVolumeClaimVolumeSource{
			ClaimName: storage.ClaimName,
		}
	} else if storage.Size != "" {
		// Create ephemeral PVC with size — PVC resource must be created separately
		// by the controller before pod creation. For now, fall back to emptyDir.
		size, err := resource.ParseQuantity(storage.Size)
		if err == nil {
			volume.PersistentVolumeClaim = &corev1.PersistentVolumeClaimVolumeSource{
				ClaimName: fmt.Sprintf("%s-%s", agentName, storage.Name),
			}
			_ = size // size would be used in PVC creation (handled elsewhere)
		} else {
			// Parse failed — use emptyDir as fallback
			volume.EmptyDir = &corev1.EmptyDirVolumeSource{}
		}
	} else {
		// No size specified — use emptyDir as fallback
		volume.EmptyDir = &corev1.EmptyDirVolumeSource{}
	}

	return volume, volumeMount
}

// buildCertTrustVolume creates a volume and mount for certificate trust stores.
func (s *Runner) buildCertTrustVolume(cert *karav1.CertTrsutStore, agentName string) (corev1.Volume, corev1.VolumeMount) {
	volumeName := fmt.Sprintf("%s-%s-cert", agentName, cert.SecretName)
	volume := corev1.Volume{
		Name: volumeName,
		VolumeSource: corev1.VolumeSource{
			Secret: &corev1.SecretVolumeSource{
				SecretName: cert.SecretName,
			},
		},
	}
	volumeMount := corev1.VolumeMount{
		Name:      volumeName,
		MountPath: fmt.Sprintf("/usr/local/share/ca-certificates/%s", cert.SecretName),
		ReadOnly:  true,
	}
	return volume, volumeMount
}

// setOwnerReference sets the RunnerPool as the owner of the pod for garbage collection.
func (s *Runner) setOwnerReference(pod *corev1.Pod, runnerPool *karav1.RunnerPool) {
	ref := metav1.OwnerReference{
		APIVersion:         runnerPool.APIVersion,
		Kind:               runnerPool.Kind,
		Name:               runnerPool.Name,
		UID:                runnerPool.UID,
		Controller:         func() *bool { b := true; return &b }(),
		BlockOwnerDeletion: func() *bool { b := true; return &b }(),
	}
	pod.OwnerReferences = append(pod.OwnerReferences, ref)
}
