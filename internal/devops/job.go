package devops

import (
	"context"
	"fmt"
	"net/http"
	"strconv"
	"strings"

	"github.com/microsoft/azure-devops-go-api/azuredevops/v7"
	"github.com/microsoft/azure-devops-go-api/azuredevops/v7/taskagent"
)

type GetAgentRequestArgs struct {
	PoolId *int
}

func (c *Client) GetJobQueue(
	ctx context.Context,
	poolId int,
) (*[]taskagent.TaskAgentJobRequest, error) {
	jobs, err := c.GetAgentRequestsForPool(ctx, GetAgentRequestArgs{
		PoolId: &poolId,
	})
	if err != nil {
		return nil, err
	}
	return jobs, nil
}

func (c *Client) GetQueuedJobsCount(ctx context.Context, poolId int) (int, error) {
	type JobsCount struct {
		Count int `json:"count"`
	}
	req, err := http.NewRequest(
		"GET",
		c.organizationURL+"/_apis/distributedtask/pools/"+strconv.Itoa(
			poolId,
		)+"/jobrequests?api-version=7.0-preview",
		nil,
	)
	if err != nil {
		return 0, err
	}

	resp, err := c.client.SendRequest(req)
	if err != nil {
		return 0, err
	}

	var count JobsCount
	err = c.client.UnmarshalBody(resp, &count)
	if err != nil {
		return 0, err
	}
	return count.Count, nil
}

func (c *Client) GetQueuedJobsByDemand(
	ctx context.Context,
	poolId int,
	demandName string,
) ([]*taskagent.TaskAgentJobRequest, error) {
	jobsReqs, err := c.GetAgentRequestsForPool(ctx, GetAgentRequestArgs{
		PoolId: &poolId,
	})
	if err != nil {
		return nil, fmt.Errorf("failed to get agent requests for pool %d: %w", poolId, err)
	}

	filtered := []*taskagent.TaskAgentJobRequest{}
	for _, job := range *jobsReqs {
		for _, demand := range *job.Demands {
			if demand == demandName {
				filtered = append(filtered, &job)
				break
			}
		}
	}

	return filtered, nil
}

func (c *Client) GetAgentRequestsForPool(
	ctx context.Context,
	args GetAgentRequestArgs,
) (*[]taskagent.TaskAgentJobRequest, error) {
	if args.PoolId == nil {
		return nil, &azuredevops.ArgumentNilError{ArgumentName: "args.AgentCloudId"}
	}

	req, err := http.NewRequest(
		"GET",
		c.organizationURL+"/_apis/distributedtask/pools/"+strconv.Itoa(
			*args.PoolId,
		)+"/jobrequests?api-version=7.0",
		nil,
	)
	if err != nil {
		return nil, err
	}

	resp, err := c.client.SendRequest(req)
	if err != nil {
		return nil, err
	}

	var responseValue []taskagent.TaskAgentJobRequest
	err = c.client.UnmarshalBody(resp, &responseValue)
	return &responseValue, err
}

// GetQueuedJobsByCapability fetches queued jobs and returns a count of demands
// grouped by capability name. System capabilities (Agent.* prefix) are excluded.
func (c *Client) GetQueuedJobsByCapability(
	ctx context.Context,
	poolId int,
) (map[string]int, error) {
	jobs, err := c.GetAgentRequestsForPool(ctx, GetAgentRequestArgs{
		PoolId: &poolId,
	})
	if err != nil {
		return nil, fmt.Errorf("failed to get job requests for pool %d: %w", poolId, err)
	}

	demands := make(map[string]int)
	if jobs == nil {
		return demands, nil
	}

	for _, job := range *jobs {
		if job.Demands == nil {
			continue
		}
		for _, demand := range *job.Demands {
			// Demands are formatted as "<name> <operator> <value>"
			// e.g. "docker -equals true", "Agent.OS -equals Linux"
			demandStr, ok := demand.(string)
			if !ok {
				continue
			}
			parts := splitDemand(demandStr)
			if len(parts) == 0 {
				continue
			}
			capName := parts[0]
			// Skip system/internal capabilities that start with "Agent."
			if !strings.HasPrefix(capName, "Agent.") {
				demands[capName]++
			}
		}
	}

	return demands, nil
}

// splitDemand splits a demand string by whitespace and returns the parts.
// e.g. "docker -equals true" -> ["docker", "-equals", "true"]
func splitDemand(demand string) []string {
	parts := strings.Fields(demand)
	return parts
}
