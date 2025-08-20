namespace AzDORunner.Model.Domain
{
    public class ApiResponse<T>
    {
        public List<T> Value { get; set; } = new();
    }

    public class AgentsResponse : ApiResponse<Agent> { }

    public class JobRequestsResponse : ApiResponse<JobRequest> { }

    public class PoolsResponse : ApiResponse<Pool> { }
}