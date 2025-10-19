# KARA (Kubernetes Azure Runner Autoscaler)

A Kubernetes operator built in Go to automatically scale Azure DevOps self-hosted agents based on pipeline workload.

## Description

KARA (Kubernetes Azure Runner Autoscaler) bridges the gap between Azure DevOps (ADO) and Kubernetes. By continuously monitoring ADO agent pool queues, KARA dynamically provisions and de-provisions self-hosted agent pods within your Kubernetes cluster.

Instead of maintaining a static pool of VMs or running agents that sit idle, this operator ensures you always have exactly the compute you need. It drastically reduces CI/CD pending times during peak deployment hours while scaling down to zero to save infrastructure costs when queues are empty.

## Getting Started

### Prerequisites

- go version v1.23.0+
- docker version 17.03+.
- kubectl version v1.11.3+.
- Access to a Kubernetes v1.11.3+ cluster.

### To Deploy on the cluster

**Build and push your image to the location specified by `IMG`:**

```sh
make docker-build docker-push IMG=<some-registry>/kara:tag

```

**NOTE:** This image ought to be published in the personal registry you specified.
And it is required to have access to pull the image from the working environment.
Make sure you have the proper permission to the registry if the above commands don’t work.

**Install the CRDs into the cluster:**

```sh
make install

```

**Deploy the Manager to the cluster with the image specified by `IMG`:**

```sh
make deploy IMG=<some-registry>/kara:tag

```

> **NOTE**: If you encounter RBAC errors, you may need to grant yourself cluster-admin
> privileges or be logged in as admin.

**Create instances of your solution**
You can apply the samples (examples) from the config/sample:

```sh
kubectl apply -k config/samples/

```

> **NOTE**: Ensure that the samples have default values to test them out.

### To Uninstall

**Delete the instances (CRs) from the cluster:**

```sh
kubectl delete -k config/samples/

```

**Delete the APIs(CRDs) from the cluster:**

```sh
make uninstall

```

**UnDeploy the controller from the cluster:**

```sh
make undeploy

```

## Project Distribution

Following are the options to release and provide this solution to the users.

### By providing a bundle with all YAML files

1. Build the installer for the image built and published in the registry:

```sh
make build-installer IMG=<some-registry>/kara:tag

```

**NOTE:** The makefile target mentioned above generates an 'install.yaml'
file in the dist directory. This file contains all the resources built
with Kustomize, which are necessary to install this project without its
dependencies.

1. Using the installer

Users can just run `kubectl apply -f <URL for YAML BUNDLE>` to install
the project, i.e.:

```sh
kubectl apply -f [https://raw.githubusercontent.com/](https://raw.githubusercontent.com/)<org>/kara/<tag or branch>/dist/install.yaml

```

### By providing a Helm Chart

1. Build the chart using the optional helm plugin

```sh
kubebuilder edit --plugins=helm/v1-alpha

```

1. See that a chart was generated under `dist/chart`, and users
can obtain this solution from there.

**NOTE:** If you change the project, you need to update the Helm Chart
using the same command above to sync the latest changes. Furthermore,
if you create webhooks, you need to use the above command with
the `--force` flag and manually ensure that any custom configuration
previously added to `dist/chart/values.yaml` or `dist/chart/manager/manager.yaml`
is manually re-applied afterwards.

## Contributing

Contributions are what make the open-source community such an amazing place to learn, inspire, and create. Any contributions you make are **greatly appreciated**.

If you have a suggestion that would make KARA better, please fork the repo and create a pull request. You can also simply open an issue with the tag "enhancement".

1. Fork the Project
2. Create your Feature Branch (`git checkout -b feature/AmazingFeature`)
3. Commit your Changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the Branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

**NOTE:** Run `make help` for more information on all potential `make` targets.

More information on how operators work can be found via the [Kubebuilder Documentation](https://book.kubebuilder.io/introduction.html).

## License

Copyright 2026 mahmoudk1000.

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

```
http://www.apache.org/licenses/LICENSE-2.0
```

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
