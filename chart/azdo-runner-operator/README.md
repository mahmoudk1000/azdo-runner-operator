# azdo-runner-operator Helm Chart

This Helm chart deploys the Azure DevOps Runner Operator to your Kubernetes cluster.

## Installation

Add the chart repository and install the chart:

```sh
helm repo add mahmoudk1000 https://mahmoudk1000.github.io/charts/
helm install my-azdo-operator mahmoudk1000/azdo-runner-operator
```

To customize values, create a `values.yaml` file and pass it with `-f values.yaml`.

## Values

| Key                | Type    | Default                                                      | Description                                                                 |
|--------------------|---------|--------------------------------------------------------------|-----------------------------------------------------------------------------|
| nameOverride       | string  | ""                                                           | Override the name of the chart                                              |
| fullnameOverride   | string  | ""                                                           | Override the full name of the chart                                         |
| image.repository   | string  | ghcr.io/mahmoudk1000/azdo-runner-operator/operator           | Image repository                                                            |
| image.pullPolicy   | string  | IfNotPresent                                                 | Image pull policy                                                           |
| image.tag          | string  | latest                                                       | Image tag                                                                   |
| imagePullSecrets   | list    | []                                                           | Secrets for pulling images from private registries                          |
| podAnnotations     | object  | {}                                                           | Kubernetes annotations for the pod                                          |
| podLabels          | object  | {}                                                           | Kubernetes labels for the pod                                               |
| podSecurityContext | object  | {}                                                           | Security context for the pod                                                |
| resources          | object  | {}                                                           | Resource requests and limits for the pod                                    |
| extraVolumes       | list    | []                                                           | Additional volumes for the deployment                                       |
| extraVolumeMounts  | list    | []                                                           | Additional volume mounts for the deployment                                 |
| nodeSelector       | object  | {}                                                           | Node selector for pod assignment                                            |
| tolerations        | list    | []                                                           | Tolerations for pod assignment                                              |
| affinity           | object  | {}                                                           | Affinity rules for pod assignment                                           |
| extraEnv           | list    | []                                                           | Additional environment variables for the pod                                |
| hostAliases        | list    | []                                                           | Custom dns records                                                          |

## Example: Custom values.yaml

```yaml
image:
  repository: ghcr.io/mahmoudk1000/azdo-runner-operator/operator
  tag: v1.0.0
  pullPolicy: Always
extraEnv:
- name: EXAMPLE_ENV
  value: "example"
```

## References

- [Kubernetes Image Pull Secrets](https://kubernetes.io/docs/tasks/configure-pod-container/pull-image-private-registry/)
- [Kubernetes Annotations](https://kubernetes.io/docs/concepts/overview/working-with-objects/annotations/)
- [Kubernetes Labels](https://kubernetes.io/docs/concepts/overview/working-with-objects/labels/)
