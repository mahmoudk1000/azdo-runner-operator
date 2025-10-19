package azdo

import (
	"context"
	"fmt"

	"github.com/microsoft/azure-devops-go-api/azuredevops/v7/taskagent"
)

func (c *Client) GetPool(ctx context.Context, poolName string) (*taskagent.TaskAgentPool, error) {
	pools, err := c.taskAgentClient.GetAgentPools(ctx, taskagent.GetAgentPoolsArgs{
		PoolName: &poolName,
	})
	if err != nil {
		return nil, fmt.Errorf("azure devops: failed to get agent pool name %s: %w", poolName, err)
	}

	return &(*pools)[0], nil
}

func (c *Client) GetPoolByID(ctx context.Context, poolId int) (*taskagent.TaskAgentPool, error) {
	pool, err := c.taskAgentClient.GetAgentPool(ctx, taskagent.GetAgentPoolArgs{
		PoolId: &poolId,
	})

	if err != nil {
		return nil, fmt.Errorf("azure devops: failed to get agent pool id %d: %w", poolId, err)
	}

	return pool, nil
}
