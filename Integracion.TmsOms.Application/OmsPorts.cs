using Integracion.TmsOms.Domain;

namespace Integracion.TmsOms.Application
{
    public interface IOmsStore
    {
        Task<Order?> GetOrderAsync(string orderNumber, CancellationToken cancellationToken = default);

        Task<bool> HasProcessedAsync(string consumer, string eventKey, CancellationToken cancellationToken = default);

        Task CommitAsync(OmsCommit commit, CancellationToken cancellationToken = default);
    }

    public sealed record OmsCommit(
        string Consumer,
        string EventKey,
        Order? ChangedOrder,
        int ExpectedVersion,
        HistoryEntry History,
        IReadOnlyList<OutboxEntry> Outbox
    );

    public sealed record OutboxEntry(Guid Id, string PartitionKey, string CorrelationId, object Payload);
}
