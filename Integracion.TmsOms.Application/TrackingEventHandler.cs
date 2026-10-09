using Integracion.TmsOms.Domain;

namespace Integracion.TmsOms.Application
{
    public sealed class TrackingEventHandler(IOmsStore store, IRetryPolicy retry, TimeProvider? clock = null)
    {
        public const string ConsumerName = "event-processor";

        private readonly IOmsStore _store = store ?? throw new ArgumentNullException(nameof(store));

        private readonly IRetryPolicy _retry = retry ?? throw new ArgumentNullException(nameof(retry));

        private readonly TimeProvider _clock = clock ?? TimeProvider.System;

        public Task<ProcessingResult> HandleAsync(
            TrackingEvent trackingEvent,
            string correlationId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(trackingEvent);
            ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
            return _retry.ExecuteAsync(
                token => ProcessOnceAsync(trackingEvent, correlationId, token),
                cancellationToken);
        }

        private async Task<ProcessingResult> ProcessOnceAsync(
            TrackingEvent trackingEvent,
            string correlationId,
            CancellationToken cancellationToken)
        {
            if (await _store.HasProcessedAsync(ConsumerName, trackingEvent.IdempotencyKey, cancellationToken))
            {
                return ProcessingResult.Duplicate;
            }
            var order = await _store.GetOrderAsync(trackingEvent.OrderNumber, cancellationToken)
                ?? throw new OrderNotFoundException(trackingEvent.OrderNumber);
            var expectedVersion = order.Version;
            var result = order.Apply(trackingEvent, _clock.GetUtcNow());
            var outbox = new List<OutboxEntry>();
            if (result.StateChanged)
            {
                outbox.Add(new OutboxEntry(
                    Guid.NewGuid(), order.OrderNumber, correlationId,
                    StatusChangedMessage.From(order, trackingEvent)));
                if (result.FollowUpEvent is not null)
                {
                    outbox.Add(new OutboxEntry(
                        Guid.NewGuid(), order.OrderNumber, correlationId, result.FollowUpEvent));
                }
            }
            await _store.CommitAsync(
                new OmsCommit(
                    ConsumerName,
                    trackingEvent.IdempotencyKey,
                    result.StateChanged ? order : null,
                    expectedVersion,
                    result.History,
                    outbox
                ),
                cancellationToken
            );
            return result.StateChanged ? ProcessingResult.Applied : ProcessingResult.IgnoredFinalState;
        }
    }
}
