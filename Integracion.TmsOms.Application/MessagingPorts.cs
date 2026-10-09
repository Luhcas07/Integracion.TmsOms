namespace Integracion.TmsOms.Application
{
    public interface IMessageSender<in T>
    {
        Task SendAsync(T payload, string partitionKey, string correlationId, CancellationToken cancellationToken = default);
    }

    public interface IMessageReceiver<T>
    {
        Task<IReceivedMessage<T>?> ReceiveAsync(CancellationToken cancellationToken = default);
    }

    public interface IReceivedMessage<out T>
    {
        string MessageId { get; }

        T Payload { get; }

        string CorrelationId { get; }

        int DeliveryCount { get; }

        Task CompleteAsync(CancellationToken cancellationToken = default);

        Task AbandonAsync(CancellationToken cancellationToken = default);

        Task DeferAsync(TimeSpan delay, CancellationToken cancellationToken = default);

        Task DeadLetterAsync(string reason, CancellationToken cancellationToken = default);
    }
}
