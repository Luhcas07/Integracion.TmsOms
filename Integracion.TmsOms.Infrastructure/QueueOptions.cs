namespace Integracion.TmsOms.Infrastructure
{
    public sealed class QueueOptions
    {
        public int MaxDeliveryCount { get; set; } = 5;
        public int LockDurationSeconds { get; set; } = 30;
        public int RedeliveryDelayMilliseconds { get; set; } = 500;
    }
}
