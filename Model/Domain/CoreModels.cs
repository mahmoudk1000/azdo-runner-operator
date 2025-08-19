namespace AzDORunner.Model.Domain
{
    public class Agent
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime? LastActive { get; set; }

        // API compatibility properties
        public DateTime? CreatedOn { get; set; }
        public LastCompletedRequest? LastCompletedRequest { get; set; }
    }

    // Simple class for API deserialization only
    public class LastCompletedRequest
    {
        public DateTime? FinishTime { get; set; }
    }

    public class Pool
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public class JobRequest
    {
        public int RequestId { get; set; }
        public string? Result { get; set; }
        public DateTime QueueTime { get; set; }
    }
}
