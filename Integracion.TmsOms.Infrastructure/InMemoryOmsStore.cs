using Integracion.TmsOms.Application;
using Integracion.TmsOms.Domain;

namespace Integracion.TmsOms.Infrastructure
{
    public sealed class InMemoryOmsStore : IOmsStore, IOutboxStore
    {
        private readonly Lock _lock = new();

        private readonly Dictionary<string, Order> _orders = new(StringComparer.Ordinal);

        private readonly HashSet<string> _processed = new(StringComparer.Ordinal);

        private readonly List<HistoryEntry> _history = [];

        private readonly List<OutboxEntry> _outbox = [];

        public int PendingOutboxCount
        {
            get
            {
                lock (_lock)
                {
                    return _outbox.Count;
                }
            }
        }

        public void Seed(Order order)
        {
            ArgumentNullException.ThrowIfNull(order);

            lock (_lock)
            {
                _orders[order.OrderNumber] = order.Clone();
            }
        }

        public Order? Peek(string orderNumber)
        {
            lock (_lock)
            {
                return _orders.TryGetValue(orderNumber, out var order) ? order.Clone() : null;
            }
        }

        public IReadOnlyList<HistoryEntry> GetHistory(string orderNumber)
        {
            lock (_lock)
            {
                return [.. _history.Where(h => h.OrderNumber == orderNumber)];
            }
        }

        public Task<Order?> GetOrderAsync(string orderNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult(Peek(orderNumber));

        public Task<bool> HasProcessedAsync(string consumer, string eventKey, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                return Task.FromResult(_processed.Contains(Key(consumer, eventKey)));
            }
        }

        public Task CommitAsync(OmsCommit commit, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(commit);
            lock (_lock)
            {
                if (commit.ChangedOrder is { } changed)
                {
                    if (!_orders.TryGetValue(changed.OrderNumber, out var current))
                    {
                        throw new OrderNotFoundException(changed.OrderNumber);
                    }
                    if (current.Version != commit.ExpectedVersion)
                    {
                        throw new ConcurrencyException(changed.OrderNumber);
                    }
                    _orders[changed.OrderNumber] = changed.Clone();
                }
                _processed.Add(Key(commit.Consumer, commit.EventKey));
                _history.Add(commit.History);
                _outbox.AddRange(commit.Outbox);
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OutboxEntry>> GetPendingAsync(int maxCount, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                return Task.FromResult<IReadOnlyList<OutboxEntry>>([.. _outbox.Take(maxCount)]);
            }
        }

        public Task MarkDispatchedAsync(Guid id, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _outbox.RemoveAll(o => o.Id == id);
            }
            return Task.CompletedTask;
        }

        private static string Key(string consumer, string eventKey) => $"{consumer}|{eventKey}";
    }
}
