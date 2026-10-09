using Integracion.TmsOms.Domain;

namespace Integracion.TmsOms.Application
{
    public interface IOutboxStore
    {
        Task<IReadOnlyList<OutboxEntry>> GetPendingAsync(int maxCount, CancellationToken cancellationToken = default);

        Task MarkDispatchedAsync(Guid id, CancellationToken cancellationToken = default);
    }

    public sealed class OutboxDispatcher(
        IOutboxStore outbox,
        IMessageSender<StatusChangedMessage> statusTopic,
        IMessageSender<TrackingEvent> eventQueue)
    {
        private readonly IOutboxStore _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));

        private readonly IMessageSender<StatusChangedMessage> _statusTopic = statusTopic ?? throw new ArgumentNullException(nameof(statusTopic));

        private readonly IMessageSender<TrackingEvent> _eventQueue = eventQueue ?? throw new ArgumentNullException(nameof(eventQueue));

        public async Task<int> DispatchPendingAsync(int batchSize = 20, CancellationToken cancellationToken = default)
        {
            var pending = await _outbox.GetPendingAsync(batchSize, cancellationToken);
            var dispatched = 0;
            foreach (var entry in pending)
            {
                switch (entry.Payload)
                {
                    case StatusChangedMessage status:
                        await _statusTopic.SendAsync(status, entry.PartitionKey, entry.CorrelationId, cancellationToken);
                        break;
                    case TrackingEvent trackingEvent:
                        await _eventQueue.SendAsync(trackingEvent, entry.PartitionKey, entry.CorrelationId, cancellationToken);
                        break;
                    default:
                        throw new PermanentFailureException(
                            $"Tipo de mensaje de Outbox no soportado: {entry.Payload.GetType().Name}.");
                }
                await _outbox.MarkDispatchedAsync(entry.Id, cancellationToken);
                dispatched++;
            }
            return dispatched;
        }
    }
}
