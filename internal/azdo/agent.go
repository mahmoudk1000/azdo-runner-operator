package azdo

import (
	"context"
	"fmt"

	"github.com/microsoft/azure-devops-go-api/azuredevops/v7/taskagent"
)

func (c *Client) ListAgents(ctx context.Context, poolId int) (*[]taskagent.TaskAgent, error) {
	agents, err := c.taskAgentClient.GetAgents(ctx, taskagent.GetAgentsArgs{
		PoolId: &poolId,
	})
	if err != nil {
		return nil, fmt.Errorf("azure devops: failed to list agents in pool id %d: %w", poolId, err)
	}

	return agents, nil
}

func (c *Client) GetAgent(ctx context.Context, poolId, agentId int) (*taskagent.TaskAgent, error) {
	agent, err := c.taskAgentClient.GetAgent(ctx, taskagent.GetAgentArgs{
		PoolId:  &poolId,
		AgentId: &agentId,
	})
	if err != nil {
		return nil, fmt.Errorf("failed to get agent %d in pool %d: %w", agentId, poolId, err)
	}

	return agent, nil
}

func (c *Client) DeleteAgent(ctx context.Context, poolId, agentId int) error {
	err := c.taskAgentClient.DeleteAgent(ctx, taskagent.DeleteAgentArgs{
		PoolId:  &poolId,
		AgentId: &agentId,
	})

	if err != nil {
		return fmt.Errorf(
			"azure devops: failed to delete agent %d in pool id %d: %w",
			agentId,
			poolId,
			err,
		)
	}

	return nil
}

func (c *Client) CountAgentsByStatus(ctx context.Context, poolId int) (map[string]int, error) {
	registerdAgents, err := c.taskAgentClient.GetAgents(ctx, taskagent.GetAgentsArgs{
		PoolId: &poolId,
	})
	if err != nil {
		return nil, fmt.Errorf("azure devops: failed to list agents in pool id %d: %w", poolId, err)
	}

	statusCount := make(map[string]int)
	if registerdAgents != nil {
		for _, agent := range *registerdAgents {
			if agent.Status != nil {
				statusCount[string(*agent.Status)]++
			}
		}
	}

	return statusCount, nil
}
