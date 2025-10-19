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

package v1

import (
	"time"

	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
)

type RunnerPoolSpec struct {
	//+kubebuilder:validation:Pattern=`^https:\/\/[a-zA-Z0-9.-]+\/[a-zA-Z0-9_.-]+$`
	AzURL         string `json:"azUrl"`
	Pool          string `json:"pool"`
	PATSecretName string `json:"patSecretName"`

	// +kubebuilder:validation:Pattern=`^[a-z0-9]+([\-\.]{1}[a-z0-9]+)*$`
	Image string `json:"image"`

	// +kubebuilder:default="IfNotPresent"
	// +kubebuilder:validation:Enum=Always;Never;IfNotPresent
	ImagePullPolicy string `json:"imagePullPolicy,omitempty"`

	// +kubebuilder:default=5
	// +kubebuilder:validation:Minimum=1
	MaxAgents int `json:"maxAgents,omitempty"`

	// +kubebuilder:default=0
	// +kubebuilder:validation:Minimum=0
	MinAgents int `json:"minAgents,omitempty"`

	// +kubebuilder:default=10
	TtlIdleSeconds    int               `json:"ttlIdleSeconds,omitempty"`
	CapabilityAware   bool              `json:"capabilityAware,omitempty"`
	Capabilities      map[string]string `json:"capabilities,omitempty"`
	InitContainerSpec InitContainerSpec `json:"initContainerSpec,omitempty"`
	SecurityContext   SecurityContext   `json:"securityContext,omitempty"`
	SecretTrustStore  []CertTrsutStore  `json:"certTrustStore,omitempty"`
	ExtraEnv          []ExtraEnv        `json:"extraEnv,omitempty"`
	Storage           []StorageSpec     `json:"storage,omitempty"`
}

type InitContainerSpec struct {
	Image string `json:"image,omitempty"`
}

type SecurityContext struct {
	// +kubebuilder:default=1001
	RunAsUser int64 `json:"runAsUser,omitempty"`

	// +kubebuilder:default=1001
	RunAsGroup int64 `json:"runAsGroup,omitempty"`
	FSGroup    int64 `json:"fsGroup,omitempty"`
	Privileged bool  `json:"privileged,omitempty"`
}

type CertTrsutStore struct {
	SecretName string `json:"secretName,omitempty"`
}

type ExtraEnv struct {
	Name      string           `json:"name"`
	Value     *string          `json:"value,omitempty"`
	ValueFrom *ValueFromSource `json:"valueFrom,omitempty"`
}

type ValueFromSource struct {
	SecretKeyRef    *SecretKeyRef    `json:"secretKeyRef,omitempty"`
	ConfigMapKeyRef *ConfigMapKeyRef `json:"configMapKeyRef,omitempty"`
}

type SecretKeyRef struct {
	Name string `json:"name"`
	Key  string `json:"key"`
}

type ConfigMapKeyRef struct {
	Name string `json:"name"`
}

type StorageSpec struct {
	Name string `json:"name"`

	// +kubebuilder:validation:Pattern=`^\/[a-zA-Z0-9\/._-]+$`
	MountPath string `json:"mountPath"`

	// +kubebuilder:validation:Pattern=`^[a-z0-9]+([\-\.]{1}[a-z0-9]+)*$`
	Size             string `json:"size,omitempty"`
	StorageClass     string `json:"storageClass,omitempty"`
	ClaimName        string `json:"claimName,omitempty"`
	DeleteWithAgents bool   `json:"deleteWithAgents,omitempty"`
}

type RunnerPoolStatus struct {
	Conditions       []metav1.Condition `json:"condition,omitempty"`
	ConnectionStatus string             `json:"connectionStatus,omitempty"`
	OrganizationName string             `json:"organizationName,omitempty"`
	PoolName         string             `json:"poolName,omitempty"`
	LastPolled       time.Time          `json:"lastPolled,omitempty"`
	LastError        string             `json:"lastError,omitempty"`
}

// +kubebuilder:object:root=true
// +kubebuilder:subresource:status

// RunnerPool is the Schema for the runnerpools API.
type RunnerPool struct {
	metav1.TypeMeta   `json:",inline"`
	metav1.ObjectMeta `json:"metadata,omitempty"`

	Spec   RunnerPoolSpec   `json:"spec,omitempty"`
	Status RunnerPoolStatus `json:"status,omitempty"`
}

// +kubebuilder:object:root=true

type RunnerPoolList struct {
	metav1.TypeMeta `json:",inline"`
	metav1.ListMeta `json:"metadata,omitempty"`
	Items           []RunnerPool `json:"items"`
}

func init() {
	SchemeBuilder.Register(&RunnerPool{}, &RunnerPoolList{})
}
