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
	"context"
	"fmt"

	"k8s.io/apimachinery/pkg/runtime"
	ctrl "sigs.k8s.io/controller-runtime"
	logf "sigs.k8s.io/controller-runtime/pkg/log"
	"sigs.k8s.io/controller-runtime/pkg/webhook"
	"sigs.k8s.io/controller-runtime/pkg/webhook/admission"

	opentoolsmfv1 "github.com/mahmoudk1000/azdo-runner-operator/api/v1"
)

// nolint:unused
// log is for logging in this package.
var runnerpoollog = logf.Log.WithName("runnerpool-resource")

// SetupRunnerPoolWebhookWithManager registers the webhook for RunnerPool in the manager.
func SetupRunnerPoolWebhookWithManager(mgr ctrl.Manager) error {
	return ctrl.NewWebhookManagedBy(mgr).For(&opentoolsmfv1.RunnerPool{}).
		WithValidator(&RunnerPoolCustomValidator{}).
		WithDefaulter(&RunnerPoolCustomDefaulter{}).
		Complete()
}

// TODO(user): EDIT THIS FILE!  THIS IS SCAFFOLDING FOR YOU TO OWN!

// +kubebuilder:webhook:path=/mutate-opentools-mf-opentools-mf-v1-runnerpool,mutating=true,failurePolicy=fail,sideEffects=None,groups=opentools.mf.opentools.mf,resources=runnerpools,verbs=create;update,versions=v1,name=mrunnerpool-v1.kb.io,admissionReviewVersions=v1

// RunnerPoolCustomDefaulter struct is responsible for setting default values on the custom resource of the
// Kind RunnerPool when those are created or updated.
//
// NOTE: The +kubebuilder:object:generate=false marker prevents controller-gen from generating DeepCopy methods,
// as it is used only for temporary operations and does not need to be deeply copied.
type RunnerPoolCustomDefaulter struct {
	// TODO(user): Add more fields as needed for defaulting
}

var _ webhook.CustomDefaulter = &RunnerPoolCustomDefaulter{}

// Default implements webhook.CustomDefaulter so a webhook will be registered for the Kind RunnerPool.
func (d *RunnerPoolCustomDefaulter) Default(ctx context.Context, obj runtime.Object) error {
	runnerpool, ok := obj.(*opentoolsmfv1.RunnerPool)

	if !ok {
		return fmt.Errorf("expected an RunnerPool object but got %T", obj)
	}
	runnerpoollog.Info("Defaulting for RunnerPool", "name", runnerpool.GetName())

	// TODO(user): fill in your defaulting logic.

	return nil
}

// TODO(user): change verbs to "verbs=create;update;delete" if you want to enable deletion validation.
// NOTE: The 'path' attribute must follow a specific pattern and should not be modified directly here.
// Modifying the path for an invalid path can cause API server errors; failing to locate the webhook.
// +kubebuilder:webhook:path=/validate-opentools-mf-opentools-mf-v1-runnerpool,mutating=false,failurePolicy=fail,sideEffects=None,groups=opentools.mf.opentools.mf,resources=runnerpools,verbs=create;update,versions=v1,name=vrunnerpool-v1.kb.io,admissionReviewVersions=v1

// RunnerPoolCustomValidator struct is responsible for validating the RunnerPool resource
// when it is created, updated, or deleted.
//
// NOTE: The +kubebuilder:object:generate=false marker prevents controller-gen from generating DeepCopy methods,
// as this struct is used only for temporary operations and does not need to be deeply copied.
type RunnerPoolCustomValidator struct {
	// TODO(user): Add more fields as needed for validation
}

var _ webhook.CustomValidator = &RunnerPoolCustomValidator{}

// ValidateCreate implements webhook.CustomValidator so a webhook will be registered for the type RunnerPool.
func (v *RunnerPoolCustomValidator) ValidateCreate(
	ctx context.Context,
	obj runtime.Object,
) (admission.Warnings, error) {
	runnerpool, ok := obj.(*opentoolsmfv1.RunnerPool)
	if !ok {
		return nil, fmt.Errorf("expected a RunnerPool object but got %T", obj)
	}
	runnerpoollog.Info("Validation for RunnerPool upon creation", "name", runnerpool.GetName())

	// TODO(user): fill in your validation logic upon object creation.

	return nil, nil
}

// ValidateUpdate implements webhook.CustomValidator so a webhook will be registered for the type RunnerPool.
func (v *RunnerPoolCustomValidator) ValidateUpdate(
	ctx context.Context,
	oldObj, newObj runtime.Object,
) (admission.Warnings, error) {
	runnerpool, ok := newObj.(*opentoolsmfv1.RunnerPool)
	if !ok {
		return nil, fmt.Errorf("expected a RunnerPool object for the newObj but got %T", newObj)
	}
	runnerpoollog.Info("Validation for RunnerPool upon update", "name", runnerpool.GetName())

	// TODO(user): fill in your validation logic upon object update.

	return nil, nil
}

// ValidateDelete implements webhook.CustomValidator so a webhook will be registered for the type RunnerPool.
func (v *RunnerPoolCustomValidator) ValidateDelete(
	ctx context.Context,
	obj runtime.Object,
) (admission.Warnings, error) {
	runnerpool, ok := obj.(*opentoolsmfv1.RunnerPool)
	if !ok {
		return nil, fmt.Errorf("expected a RunnerPool object but got %T", obj)
	}
	runnerpoollog.Info("Validation for RunnerPool upon deletion", "name", runnerpool.GetName())

	// TODO(user): fill in your validation logic upon object deletion.

	return nil, nil
}
