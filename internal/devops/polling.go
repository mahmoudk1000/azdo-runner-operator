// File: devops/polling.go
// Description: PollingService queries Azure DevOps for pool state, agent counts,
// and queued job information. Results are used by the controller for autoscaling.
// Dependencies: devops.Client, controller-runtime logger.
package devops

import (
	"context"
	"fmt"
	"time"

	"sigs.k8s.io/controller-runtime/pkg/log"
)

// PollingService periodically queries Azure DevOps for runner pool state.
type PollingService struct {
	client *Client
}

// NewPollingService creates a new PollingService wrapping the given AzDO client.
func NewPollingService(client *Client) *PollingService {
	return &PollingService{
		client: client,
	}
}

// PollResult holds the snapshot of Azure DevOps pool state from a single poll.
type PollResult struct {
	PoolID                 int
	PoolName               string
	OrganizationName       string
	QueuedJobs             int
	RunningJobs            int
	TotalAgents            int
	OnlineAgents           int
	OfflineAgents          int
	AgentsByStatus         map[string]int
	QueuedJobsByCapability map[string]int // capability name -> count of queued jobs requiring it
	Timestamp              time.Time
}

// Poll queries Azure DevOps for the current state of a runner pool.
// It returns pool info, agent counts, and queued job metrics.
func (p *PollingService) Poll(ctx context.Context, poolName string) (*PollResult, error) {
	log := log.FromContext(ctx)

	// Get pool info
	pool, err := p.client.GetPool(ctx, poolName)
	if err != nil {
		return nil, fmt.Errorf("polling: failed to get pool '%s': %w", poolName, err)
	}
	if pool.Id == nil {
		return nil, fmt.Errorf("polling: pool ID is nil for pool '%s'", poolName)
	}

	poolId := *pool.Id

	// Query the job queue for this pool
	queuedCount, err := p.client.GetQueuedJobsCount(ctx, poolId)
	if err != nil {
		// Non-fatal: pool may not have jobs queued yet
		log.Info("polling: could not query job queue, assuming zero jobs", "poolId", poolId, "error", err)
		queuedCount = 0
	}

	// Extract per-capability job demands from the job queue
	queuedByCap, err := p.client.GetQueuedJobsByCapability(ctx, poolId)
	if err != nil {
		// Non-fatal: if we can't parse demands, fall back to empty map
		log.Info("polling: could not extract capability demands", "poolId", poolId, "error", err)
		queuedByCap = make(map[string]int)
	}

	// Count agents by status
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
	runningAgents := agentByStatus["Running"]

	for _, count := range agentByStatus {
		totalAgents += count
	}

	result := &PollResult{
		PoolID:                 poolId,
		PoolName:               poolName,
		OrganizationName:       p.client.organizationURL,
		QueuedJobs:             queuedCount,
		RunningJobs:            runningAgents,
		TotalAgents:            totalAgents,
		OnlineAgents:           onlineAgents,
		OfflineAgents:          offlineAgents,
		AgentsByStatus:         agentByStatus,
		QueuedJobsByCapability: queuedByCap,
		Timestamp:              time.Now(),
	}

	log.Info("Azure DevOps poll completed",
		"pool", poolName,
		"poolId", poolId,
		"queuedJobs", result.QueuedJobs,
		"runningJobs", result.RunningJobs,
		"totalAgents", result.TotalAgents,
		"onlineAgents", result.OnlineAgents,
		"offlineAgents", result.OfflineAgents,
		"demandsByCapability", result.QueuedJobsByCapability)

	return result, nil
}
