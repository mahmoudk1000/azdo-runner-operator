package azdo

import (
	"context"
	"fmt"

	"github.com/microsoft/azure-devops-go-api/azuredevops/v7"
	"github.com/microsoft/azure-devops-go-api/azuredevops/v7/taskagent"
)

type Client struct {
	client          azuredevops.Client
	connection      *azuredevops.Connection
	taskAgentClient taskagent.Client
	organizationURL string
}

func NewClient(organizationURL, personalAccessToken string) (*Client, error) {
	azdoConnection := azuredevops.NewPatConnection(organizationURL, personalAccessToken)

	azdoClient := azuredevops.NewClient(azdoConnection, organizationURL)
	azdoTaskAgentClient, err := taskagent.NewClient(context.Background(), azdoConnection)
	if err != nil {
		return nil, fmt.Errorf("azure devops: failed to create task agent client: %w", err)
	}

	return &Client{
		client:          *azdoClient,
		connection:      azdoConnection,
		taskAgentClient: azdoTaskAgentClient,
		organizationURL: organizationURL,
	}, nil
}

func (c *Client) Close() {
	c.connection = nil
}
