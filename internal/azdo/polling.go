package azdo

import (
	"context"
	"fmt"
	"time"

	"sigs.k8s.io/controller-runtime/pkg/log"
)

type PollingService struct {
	client *Client
}

func NewPollingService(client *Client) *PollingService {
	return &PollingService{
		client: client,
	}
}

type PollResult struct {
	PoolID           int
	PoolName         string
	OrganizationName string
	QueuedJobs       int
	RunningJobs      int
	TotalAgents      int
	OnlineAgents     int
	OfflineAgents    int
	AgentsByStatus   map[string]int
	Timestamp        time.Time
}

func (p *PollingService) Poll(ctx context.Context, poolName string) (*PollResult, error) {
	log := log.FromContext(ctx)

	pool, err := p.client.GetPool(ctx, poolName)
	if err != nil {
		return nil, fmt.Errorf("polling: failed to get pool '%s': %w", poolName, err)
	}
	if pool.Id == nil {
		return nil, fmt.Errorf("polling: pool ID is nil for pool '%s'", poolName)
	}

	poolId := *pool.Id

	// TODO: call  GetJObQueue

	agentByStatus, err := p.client.CountAgentsByStatus(ctx, poolId)
	if err != nil {
		return nil, fmt.Errorf(
			"polling: failed to count agents by status for pool ID %d: %w",
			poolId,
			err,
		)
	}

	totalAgents := 0
	onlineAgents := agentByStatus["Online"]
	offlineAgents := agentByStatus["Offline"]

	for _, agent := range agentByStatus {
		totalAgents += agent
	}

	result := &PollResult{
		PoolID:           poolId,
		PoolName:         poolName,
		OrganizationName: p.client.organizationURL,
		// QueuedJobs:       jobInfo.QueuedJobs,
		// RunningJobs:      jobInfo.RunningJobs,
		TotalAgents:    totalAgents,
		OnlineAgents:   onlineAgents,
		OfflineAgents:  offlineAgents,
		AgentsByStatus: agentByStatus,
		Timestamp:      time.Now(),
	}

	log.Info("Azure DevOps poll completed",
		"pool", poolName,
		"queuedJobs", result.QueuedJobs,
		"runningJobs", result.RunningJobs,
		"totalAgents", result.TotalAgents)

	return result, nil
}
