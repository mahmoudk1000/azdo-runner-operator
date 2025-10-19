package azdo

import (
	"context"
	"fmt"
	"net/http"
	"strconv"

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
