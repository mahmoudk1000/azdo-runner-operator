namespace AzDORunner.Model.Domain
{
    // Generic API response wrapper
    public class ApiResponse<T>
    {
        public List<T> Value { get; set; } = new();
    }

    // Specific API response types
    public class AgentsResponse : ApiResponse<Agent> { }
    public class JobRequestsResponse : ApiResponse<JobRequest> { }
    public class PoolsResponse : ApiResponse<Pool> { }
}
