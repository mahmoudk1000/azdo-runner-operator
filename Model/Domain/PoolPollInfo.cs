using AzDORunner.Entities;

namespace AzDORunner.Model.Domain
{
    public class PoolPollInfo
    {
        public V1AzDORunnerEntity Entity { get; set; } = null!;

        public string Pat { get; set; } = string.Empty;

        public DateTime LastPolled { get; set; } = DateTime.MinValue;

        public int PollIntervalSeconds { get; set; } = 10;
    }
}