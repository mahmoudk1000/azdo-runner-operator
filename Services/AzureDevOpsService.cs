using System.Text.Json;
using System.Text;
using AzDORunner.Model.Domain;

namespace AzDORunner.Services;

public interface IAzureDevOpsService
{
    Task<List<JobRequest>> GetJobRequestsAsync(string azDoUrl, string poolName, string pat);
    Task<List<JobRequest>> GetQueuedJobsWithCapabilitiesAsync(string azDoUrl, string poolName, string pat);
    Task<bool> TestConnectionAsync(string azDoUrl, string pat);
    Task<int> GetQueuedJobsCountAsync(string azDoUrl, string poolName, string pat);
    Task<List<string>> GetAvailablePoolNamesAsync(string azDoUrl, string pat);
    Task<List<Agent>> GetPoolAgentsAsync(string azDoUrl, string poolName, string pat);
    Task<bool> UnregisterAgentAsync(string azDoUrl, string poolName, string agentName, string pat);
    string ExtractOrganizationName(string azDoUrl);
}

public class AzureDevOpsService : IAzureDevOpsService
{
    public async Task<List<JobRequest>> GetJobRequestsAsync(string azDoUrl, string poolName, string pat)
    {
        try
        {
            _logger.LogDebug("Getting job requests for pool '{PoolName}' from {AzDoUrl}", poolName, azDoUrl);

            var poolId = await GetPoolIdAsync(azDoUrl, poolName, pat);
            if (poolId == null)
            {
                _logger.LogWarning("Pool '{PoolName}' not found for job requests", poolName);
                return new List<JobRequest>();
            }

            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{azDoUrl.TrimEnd('/')}/_apis/distributedtask/pools/{poolId}/jobrequests?api-version=7.0");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to get job requests for pool '{PoolName}': {StatusCode}", poolName, response.StatusCode);
                return new List<JobRequest>();
            }

            var content = await response.Content.ReadAsStringAsync();
            var jobRequests = JsonSerializer.Deserialize<JobRequestsResponse>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            var allJobs = jobRequests?.Value ?? new List<JobRequest>();
            _logger.LogInformation("Pool '{PoolName}': {JobCount} total job requests", poolName, allJobs.Count);
            return allJobs;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get job requests for pool '{PoolName}' from {AzDoUrl}", poolName, azDoUrl);
            return new List<JobRequest>();
        }
    }

    public async Task<List<JobRequest>> GetQueuedJobsWithCapabilitiesAsync(string azDoUrl, string poolName, string pat)
    {
        try
        {
            _logger.LogDebug("Getting queued jobs with capabilities for pool '{PoolName}' from {AzDoUrl}", poolName, azDoUrl);

            var poolId = await GetPoolIdAsync(azDoUrl, poolName, pat);
            if (poolId == null)
            {
                _logger.LogWarning("Pool '{PoolName}' not found for job requests with capabilities", poolName);
                return new List<JobRequest>();
            }

            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{azDoUrl.TrimEnd('/')}/_apis/distributedtask/pools/{poolId}/jobrequests?api-version=7.0&$expand=jobs");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to get job requests with capabilities for pool '{PoolName}': {StatusCode}", poolName, response.StatusCode);
                return new List<JobRequest>();
            }

            var content = await response.Content.ReadAsStringAsync();
            var jobRequests = JsonSerializer.Deserialize<JobRequestsResponse>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            var queuedJobs = jobRequests?.Value?.Where(j => j.Result == null).ToList() ?? new List<JobRequest>();

            // Parse demands/capabilities from each job
            foreach (var job in queuedJobs)
            {
                job.RequiredCapability = ExtractRequiredCapabilityFromDemands(job.Demands);
            }

            _logger.LogInformation("Pool '{PoolName}': {JobCount} queued jobs with capabilities parsed", poolName, queuedJobs.Count);
            return queuedJobs;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get queued jobs with capabilities for pool '{PoolName}' from {AzDoUrl}", poolName, azDoUrl);
            return new List<JobRequest>();
        }
    }

    private string? ExtractRequiredCapabilityFromDemands(List<string> demands)
    {
        if (demands == null || !demands.Any())
            return null;

        // Return the first demand found - this allows exact keyword matching
        // If user configures "mykeyword" in capabilityImages and sets demands: [mykeyword]
        // it will return "mykeyword" directly for exact matching
        foreach (var demand in demands)
        {
            var cleanDemand = demand.Trim().ToLowerInvariant();

            // Return the demand as-is for direct matching with capabilityImages keys
            // This enables custom keywords like "mykeyword", "gpu", "docker", etc.
            if (!string.IsNullOrEmpty(cleanDemand))
            {
                _logger.LogDebug("Found capability demand: '{Demand}'", cleanDemand);
                return cleanDemand;
            }
        }

        return null; // No demands found
    }
    private readonly HttpClient _httpClient;
    private readonly ILogger<AzureDevOpsService> _logger;

    public AzureDevOpsService(HttpClient httpClient, ILogger<AzureDevOpsService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public string ExtractOrganizationName(string azDoUrl)
    {
        try
        {
            // Handle formats like:
            // Cloud: https://dev.azure.com/organization
            // Cloud: https://organization.visualstudio.com
            // On-premises: https://tfs.company.com/tfs/CollectionName
            // On-premises: https://azuredevops.company.com/CollectionName
            // On-premises: https://server/tfs/CollectionName
            var uri = new Uri(azDoUrl);

            if (uri.Host == "dev.azure.com")
            {
                // Extract from path: /organization
                var segments = uri.Segments;
                if (segments.Length > 1)
                {
                    return segments[1].TrimEnd('/');
                }
            }
            else if (uri.Host.EndsWith(".visualstudio.com"))
            {
                // Extract from subdomain: organization.visualstudio.com
                var parts = uri.Host.Split('.');
                if (parts.Length > 0)
                {
                    return parts[0];
                }
            }
            else
            {
                // Self-hosted Azure DevOps Server/TFS
                // Extract collection name from path segments
                var segments = uri.Segments;

                // Look for collection name in various patterns:
                // /tfs/CollectionName -> return CollectionName
                // /CollectionName -> return CollectionName
                if (segments.Length > 1)
                {
                    // Skip empty segments and common prefixes
                    var meaningfulSegments = segments
                        .Where(s => !string.IsNullOrWhiteSpace(s) && s != "/")
                        .Select(s => s.TrimEnd('/'))
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToList();

                    if (meaningfulSegments.Any())
                    {
                        if (meaningfulSegments.Count > 1 &&
                            meaningfulSegments[0].Equals("tfs", StringComparison.OrdinalIgnoreCase))
                        {
                            return meaningfulSegments[1];
                        }

                        return meaningfulSegments[0];
                    }
                }

                return uri.Host;
            }

            return "Unknown";
        }
        catch
        {
            return "Invalid";
        }
    }

    public async Task<bool> TestConnectionAsync(string azDoUrl, string pat)
    {
        try
        {
            _logger.LogDebug("Testing Azure DevOps connection to {AzDoUrl}", azDoUrl);

            var request = new HttpRequestMessage(HttpMethod.Get, $"{azDoUrl.TrimEnd('/')}/_apis/projects?api-version=7.0");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));

            var response = await _httpClient.SendAsync(request);
            var isSuccess = response.IsSuccessStatusCode;

            if (isSuccess)
            {
                _logger.LogDebug("Azure DevOps connection test successful for {AzDoUrl}", azDoUrl);
            }
            else
            {
                _logger.LogWarning("Azure DevOps connection test failed for {AzDoUrl}: {StatusCode}", azDoUrl, response.StatusCode);
            }

            return isSuccess;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to test Azure DevOps connection to {AzDoUrl}", azDoUrl);
            return false;
        }
    }

    public async Task<List<string>> GetAvailablePoolNamesAsync(string azDoUrl, string pat)
    {
        try
        {
            _logger.LogDebug("Getting available pool names from {AzDoUrl}", azDoUrl);

            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{azDoUrl.TrimEnd('/')}/_apis/distributedtask/pools?api-version=7.0");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to get pools: {StatusCode}", response.StatusCode);
                return new List<string>();
            }

            var content = await response.Content.ReadAsStringAsync();
            var pools = JsonSerializer.Deserialize<PoolsResponse>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            var poolNames = pools?.Value?.Select(p => p.Name).ToList() ?? new List<string>();
            _logger.LogDebug("Found {PoolCount} available pools: [{PoolNames}]", poolNames.Count, string.Join(", ", poolNames));
            return poolNames;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get available pool names from {AzDoUrl}", azDoUrl);
            return new List<string>();
        }
    }

    public async Task<int> GetQueuedJobsCountAsync(string azDoUrl, string poolName, string pat)
    {
        try
        {
            _logger.LogDebug("Getting queued jobs count for pool '{PoolName}' from {AzDoUrl}", poolName, azDoUrl);

            // First get the pool ID with better logging
            var poolId = await GetPoolIdAsync(azDoUrl, poolName, pat);
            if (poolId == null)
            {
                // Log available pools for debugging
                var availablePools = await GetAvailablePoolNamesAsync(azDoUrl, pat);
                _logger.LogWarning("Pool '{PoolName}' not found. Available pools: [{AvailablePools}]",
                    poolName, string.Join(", ", availablePools));
                return 0;
            }

            // Get queued jobs for the pool
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{azDoUrl.TrimEnd('/')}/_apis/distributedtask/pools/{poolId}/jobrequests?api-version=7.0");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to get job requests for pool '{PoolName}': {StatusCode}", poolName, response.StatusCode);
                return 0;
            }

            var content = await response.Content.ReadAsStringAsync();
            var jobRequests = JsonSerializer.Deserialize<JobRequestsResponse>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            // Enhanced queued job detection with detailed logging
            var allJobs = jobRequests?.Value ?? new List<JobRequest>();
            var queuedJobs = allJobs.Where(j => j.Result == null).ToList();

            _logger.LogInformation("Pool '{PoolName}': {QueuedJobs} queued jobs out of {TotalJobs} total jobs",
                poolName, queuedJobs.Count, allJobs.Count);

            // Additional debugging for queued jobs
            if (queuedJobs.Any())
            {
                _logger.LogDebug("Queued jobs details: [{QueuedJobIds}]",
                    string.Join(", ", queuedJobs.Select(j => j.RequestId)));
            }

            return queuedJobs.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get queued jobs count for pool '{PoolName}' from {AzDoUrl}", poolName, azDoUrl);
            return 0;
        }
    }

    public async Task<List<Agent>> GetPoolAgentsAsync(string azDoUrl, string poolName, string pat)
    {
        try
        {
            _logger.LogDebug("👥 Getting agents for pool '{PoolName}' from {AzDoUrl}", poolName, azDoUrl);

            var poolId = await GetPoolIdAsync(azDoUrl, poolName, pat);
            if (poolId == null)
            {
                _logger.LogWarning("❌ Pool '{PoolName}' not found for agent listing", poolName);
                return new List<Agent>();
            }

            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{azDoUrl.TrimEnd('/')}/_apis/distributedtask/pools/{poolId}/agents?api-version=7.0&includeCapabilities=false&includeLastCompletedRequest=true");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to get agents for pool '{PoolName}': {StatusCode}", poolName, response.StatusCode);
                return new List<Agent>();
            }

            var content = await response.Content.ReadAsStringAsync();
            var agentsResponse = JsonSerializer.Deserialize<AgentsResponse>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (agentsResponse?.Value != null)
            {
                // Process the agents to set the application properties from API properties
                foreach (var agent in agentsResponse.Value)
                {
                    agent.CreatedAt = agent.CreatedOn ?? DateTime.UtcNow;
                    agent.LastActive = agent.LastCompletedRequest?.FinishTime;
                    agent.Status = agent.Status == "online" ? "Online" : "Offline";
                }
            }

            var agents = agentsResponse?.Value ?? new List<Agent>();

            _logger.LogInformation("Found {AgentCount} agents in pool '{PoolName}': [{AgentNames}]",
                agents.Count, poolName, string.Join(", ", agents.Select(a => $"{a.Name}({a.Status})")));

            return agents;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get agents for pool '{PoolName}' from {AzDoUrl}", poolName, azDoUrl);
            return new List<Agent>();
        }
    }

    public async Task<bool> UnregisterAgentAsync(string azDoUrl, string poolName, string agentName, string pat)
    {
        try
        {
            _logger.LogDebug("Unregistering agent '{AgentName}' from pool '{PoolName}'", agentName, poolName);

            var poolId = await GetPoolIdAsync(azDoUrl, poolName, pat);
            if (poolId == null)
            {
                _logger.LogWarning("Pool '{PoolName}' not found for agent unregistration", poolName);
                return false;
            }

            // First find the agent ID
            var agents = await GetPoolAgentsAsync(azDoUrl, poolName, pat);
            var agent = agents.FirstOrDefault(a => a.Name == agentName);
            if (agent == null)
            {
                _logger.LogWarning("Agent '{AgentName}' not found in pool '{PoolName}'", agentName, poolName);
                return false;
            }

            // Unregister the agent
            var request = new HttpRequestMessage(HttpMethod.Delete,
                $"{azDoUrl.TrimEnd('/')}/_apis/distributedtask/pools/{poolId}/agents/{agent.Id}?api-version=7.0");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));

            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully unregistered agent '{AgentName}' from pool '{PoolName}'", agentName, poolName);
                return true;
            }
            else
            {
                _logger.LogError("Failed to unregister agent '{AgentName}' from pool '{PoolName}': {StatusCode}",
                    agentName, poolName, response.StatusCode);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unregister agent '{AgentName}' from pool '{PoolName}'", agentName, poolName);
            return false;
        }
    }

    private async Task<int?> GetPoolIdAsync(string azDoUrl, string poolName, string pat)
    {
        try
        {
            _logger.LogDebug("Looking up pool ID for '{PoolName}'", poolName);

            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{azDoUrl.TrimEnd('/')}/_apis/distributedtask/pools?api-version=7.0");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to get pools list: {StatusCode}", response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            var pools = JsonSerializer.Deserialize<PoolsResponse>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            var matchedPool = pools?.Value?.FirstOrDefault(p =>
                string.Equals(p.Name, poolName, StringComparison.OrdinalIgnoreCase));

            if (matchedPool != null)
            {
                _logger.LogDebug("Found pool: ID={PoolId}, Name='{PoolName}'", matchedPool.Id, matchedPool.Name);
                return matchedPool.Id;
            }

            _logger.LogWarning("No pool found with name '{PoolName}'", poolName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get pool ID for '{PoolName}'", poolName);
            return null;
        }
    }
}