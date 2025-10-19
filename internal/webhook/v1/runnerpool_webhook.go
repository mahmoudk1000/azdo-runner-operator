// File: webhook/v1/runnerpool_webhook.go
// Description: Mutating and validating webhooks for RunnerPool CRD.
// Defaults apply sensible values for optional fields; validation
// rejects malformed specs (bad URLs, missing required fields).
// Dependencies: api/v1 types, admissionregistration webhook interfaces.
package v1

import (
	"context"
	"fmt"
	"net/url"
	"strings"

	"k8s.io/apimachinery/pkg/runtime"
	ctrl "sigs.k8s.io/controller-runtime"
	logf "sigs.k8s.io/controller-runtime/pkg/log"
	"sigs.k8s.io/controller-runtime/pkg/webhook"
	"sigs.k8s.io/controller-runtime/pkg/webhook/admission"

	karav1 "github.com/mahmoudk1000/azdo-runner-operator/api/v1"
)

// nolint:unused
// log is for logging in this package.
var runnerpoollog = logf.Log.WithName("runnerpool-resource")

// SetupRunnerPoolWebhookWithManager registers the webhook for RunnerPool in the manager.
func SetupRunnerPoolWebhookWithManager(mgr ctrl.Manager) error {
	return ctrl.NewWebhookManagedBy(mgr).For(&karav1.RunnerPool{}).
		WithValidator(&RunnerPoolCustomValidator{}).
		WithDefaulter(&RunnerPoolCustomDefaulter{}).
		Complete()
}

// +kubebuilder:webhook:path=/mutate-kara-felukka-org-v1-runnerpool,mutating=true,failurePolicy=fail,sideEffects=None,groups=kara.felukka.org,resources=runnerpools,verbs=create;update,versions=v1,name=mrunnerpool-v1.kb.io,admissionReviewVersions=v1

// RunnerPoolCustomDefaulter sets default values on RunnerPool resources.
//
// +kubebuilder:object:generate=false
type RunnerPoolCustomDefaulter struct{}

var _ webhook.CustomDefaulter = &RunnerPoolCustomDefaulter{}

// Default applies default values to the RunnerPool spec.
func (d *RunnerPoolCustomDefaulter) Default(ctx context.Context, obj runtime.Object) error {
	runnerpool, ok := obj.(*karav1.RunnerPool)
	if !ok {
		return fmt.Errorf("expected a RunnerPool object but got %T", obj)
	}
	runnerpoollog.Info("Defaulting for RunnerPool", "name", runnerpool.GetName())

	// Set default ImagePullPolicy
	if runnerpool.Spec.ImagePullPolicy == "" {
		runnerpool.Spec.ImagePullPolicy = "IfNotPresent"
	}

	// Set default MinAgents if not specified
	if runnerpool.Spec.MinAgents == 0 {
		runnerpool.Spec.MinAgents = 0
	}

	// Set default MaxAgents if not specified
	if runnerpool.Spec.MaxAgents == 0 {
		runnerpool.Spec.MaxAgents = 5
	}

	// Set default security context
	if runnerpool.Spec.SecurityContext.RunAsUser == 0 {
		runnerpool.Spec.SecurityContext.RunAsUser = 1001
	}
	if runnerpool.Spec.SecurityContext.RunAsGroup == 0 {
		runnerpool.Spec.SecurityContext.RunAsGroup = 1001
	}

	// Default TTL
	if runnerpool.Spec.TtlIdleSeconds == 0 {
		runnerpool.Spec.TtlIdleSeconds = 10
	}

	return nil
}

// +kubebuilder:webhook:path=/validate-kara-felukka-org-v1-runnerpool,mutating=false,failurePolicy=fail,sideEffects=None,groups=kara.felukka.org,resources=runnerpools,verbs=create;update,versions=v1,name=vrunnerpool-v1.kb.io,admissionReviewVersions=v1

// RunnerPoolCustomValidator validates RunnerPool resources.
//
// +kubebuilder:object:generate=false
type RunnerPoolCustomValidator struct{}

var _ webhook.CustomValidator = &RunnerPoolCustomValidator{}

// ValidateCreate validates a new RunnerPool resource.
func (v *RunnerPoolCustomValidator) ValidateCreate(
	ctx context.Context,
	obj runtime.Object,
) (admission.Warnings, error) {
	runnerpool, ok := obj.(*karav1.RunnerPool)
	if !ok {
		return nil, fmt.Errorf("expected a RunnerPool object but got %T", obj)
	}
	runnerpoollog.Info("Validation for RunnerPool upon creation", "name", runnerpool.GetName())

	return v.validateSpec(runnerpool)
}

// ValidateUpdate validates an updated RunnerPool resource.
func (v *RunnerPoolCustomValidator) ValidateUpdate(
	ctx context.Context,
	oldObj, newObj runtime.Object,
) (admission.Warnings, error) {
	runnerpool, ok := newObj.(*karav1.RunnerPool)
	if !ok {
		return nil, fmt.Errorf("expected a RunnerPool object but got %T", newObj)
	}
	old, ok := oldObj.(*karav1.RunnerPool)
	if !ok {
		return nil, fmt.Errorf("expected old object to be RunnerPool but got %T", oldObj)
	}
	runnerpoollog.Info("Validation for RunnerPool upon update", "name", runnerpool.GetName())

	warnings, err := v.validateSpec(runnerpool)
	if err != nil {
		return warnings, err
	}

	// Check for immutable fields
	if old.Spec.AzURL != runnerpool.Spec.AzURL {
		return nil, fmt.Errorf("field spec.azUrl is immutable")
	}
	if old.Spec.Pool != runnerpool.Spec.Pool {
		return nil, fmt.Errorf("field spec.pool is immutable")
	}
	if old.Spec.PATSecretName != runnerpool.Spec.PATSecretName {
		return nil, fmt.Errorf("field spec.patSecretName is immutable")
	}

	return nil, nil
}

// ValidateDelete is not enforced for RunnerPool — deletions are handled by the finalizer.
func (v *RunnerPoolCustomValidator) ValidateDelete(
	ctx context.Context,
	obj runtime.Object,
) (admission.Warnings, error) {
	runnerpool, ok := obj.(*karav1.RunnerPool)
	if !ok {
		return nil, fmt.Errorf("expected a RunnerPool object but got %T", obj)
	}
	runnerpoollog.Info("Validation for RunnerPool upon deletion", "name", runnerpool.GetName())
	return nil, nil
}

// validateSpec performs common validation on the RunnerPool spec.
func (v *RunnerPoolCustomValidator) validateSpec(rp *karav1.RunnerPool) (admission.Warnings, error) {
	var warnings admission.Warnings
	var errMsgs []string

	// Validate AzDO URL format
	if rp.Spec.AzURL != "" {
		parsedURL, err := url.Parse(rp.Spec.AzURL)
		if err != nil {
			errMsgs = append(errMsgs, fmt.Sprintf("spec.azUrl is not a valid URL: %v", err))
		} else if parsedURL.Scheme != "https" {
			errMsgs = append(errMsgs, "spec.azUrl must use https scheme")
		} else if parsedURL.Host == "" {
			errMsgs = append(errMsgs, "spec.azUrl must have a valid host")
		}
	}

	// Validate pool name is not empty
	if strings.TrimSpace(rp.Spec.Pool) == "" {
		errMsgs = append(errMsgs, "spec.pool is required and must not be empty")
	}

	// Validate PAT secret name
	if strings.TrimSpace(rp.Spec.PATSecretName) == "" {
		errMsgs = append(errMsgs, "spec.patSecretName is required and must not be empty")
	}

	// Validate image is not empty
	if strings.TrimSpace(rp.Spec.Image) == "" {
		errMsgs = append(errMsgs, "spec.image is required and must not be empty")
	}

	// Validate agent counts
	if rp.Spec.MaxAgents < 0 {
		errMsgs = append(errMsgs, "spec.maxAgents must be >= 0")
	}
	if rp.Spec.MinAgents < 0 {
		errMsgs = append(errMsgs, "spec.minAgents must be >= 0")
	}
	if rp.Spec.MinAgents > rp.Spec.MaxAgents {
		errMsgs = append(errMsgs, fmt.Sprintf("spec.minAgents (%d) must not exceed spec.maxAgents (%d)",
			rp.Spec.MinAgents, rp.Spec.MaxAgents))
	}

	// Validate extra env names
	for _, env := range rp.Spec.ExtraEnv {
		if strings.TrimSpace(env.Name) == "" {
			errMsgs = append(errMsgs, "extra env vars must have a non-empty name")
		}
		if env.Value == nil && env.ValueFrom == nil {
			warnings = append(warnings, fmt.Sprintf("extra env var %q has no value or valueFrom", env.Name))
		}
	}

	if len(errMsgs) > 0 {
		return warnings, fmt.Errorf("validation failed: %s", strings.Join(errMsgs, "; "))
	}

	return warnings, nil
}
