using Integracion.TmsOms.Application;
using Integracion.TmsOms.Domain;

namespace Integracion.TmsOms.Tests
{
    internal sealed class FakeOmsStore : IOmsStore
    {
        private readonly Dictionary<string, Order> _orders = [];

        private readonly HashSet<string> _processed = [];

        public List<HistoryEntry> History { get; } = [];

        public List<OutboxEntry> Outbox { get; } = [];

        public int CommitAttempts { get; private set; }

        public int ConcurrencyFailuresToInject { get; set; }

        public void Seed(Order order) => _orders[order.OrderNumber] = order.Clone();

        public Order? Peek(string orderNumber) =>
            _orders.TryGetValue(orderNumber, out var order) ? order.Clone() : null;

        public Task<Order?> GetOrderAsync(string orderNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult<Order?>(Peek(orderNumber));

        public Task<bool> HasProcessedAsync(string consumer, string eventKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(_processed.Contains($"{consumer}|{eventKey}"));

        public Task CommitAsync(OmsCommit commit, CancellationToken cancellationToken = default)
        {
            CommitAttempts++;
            if (ConcurrencyFailuresToInject > 0)
            {
                ConcurrencyFailuresToInject--;
                throw new ConcurrencyException(commit.History.OrderNumber);
            }
            if (commit.ChangedOrder is not null)
            {
                var current = _orders[commit.ChangedOrder.OrderNumber];
                if (current.Version != commit.ExpectedVersion)
                    throw new ConcurrencyException(commit.ChangedOrder.OrderNumber);

                _orders[commit.ChangedOrder.OrderNumber] = commit.ChangedOrder.Clone();
            }
            _processed.Add($"{commit.Consumer}|{commit.EventKey}");
            History.Add(commit.History);
            Outbox.AddRange(commit.Outbox);
            return Task.CompletedTask;
        }
    }

    public class TrackingEventHandlerTests
    {
        private const string OrderNumber = "2500000006-01";

        private static readonly DateTimeOffset T0 = new(2025, 4, 15, 16, 53, 7, TimeSpan.FromHours(-5));

        private readonly FakeOmsStore _store = new();

        private readonly TrackingEventHandler _handler;

        public TrackingEventHandlerTests()
        {
            _store.Seed(new Order(OrderNumber, "OE2500000006-01", "01021755", "TIENDAS PERUANAS S.A."));
            var retry = new RetryPolicy(new RetryOptions { MaxAttempts = 3, InitialDelay = TimeSpan.Zero });
            _handler = new TrackingEventHandler(_store, retry);
        }

        private static TrackingEvent Ev(OrderStatus status, int minutes = 0) =>
            new(OrderNumber, status, T0.AddMinutes(minutes));

        [Fact]
        public async Task Applies_the_event_records_history_and_queues_a_status_change()
        {
            var result = await _handler.HandleAsync(Ev(OrderStatus.Delivered), "corr-1");
            Assert.Equal(ProcessingResult.Applied, result);
            var order = _store.Peek(OrderNumber)!;
            Assert.Equal(OrderStatus.Delivered, order.Status);
            Assert.Equal(1, order.VisitCount);
            Assert.Single(_store.History);
            var entry = Assert.Single(_store.Outbox);
            var message = Assert.IsType<StatusChangedMessage>(entry.Payload);
            Assert.Equal(OrderStatus.Delivered, message.Status);
            Assert.Equal("corr-1", entry.CorrelationId);
            Assert.Equal(OrderNumber, entry.PartitionKey);
        }

        [Fact]
        public async Task Duplicate_event_is_not_applied_twice()
        {
            var evt = Ev(OrderStatus.NotDelivered);
            var first = await _handler.HandleAsync(evt, "c1");
            var second = await _handler.HandleAsync(evt, "c2");
            Assert.Equal(ProcessingResult.Applied, first);
            Assert.Equal(ProcessingResult.Duplicate, second);
            Assert.Equal(1, _store.Peek(OrderNumber)!.VisitCount);
            Assert.Equal(1, _store.CommitAttempts);
            Assert.Single(_store.History);
        }

        [Fact]
        public async Task Event_on_a_closed_order_is_recorded_but_does_not_change_it()
        {
            await _handler.HandleAsync(Ev(OrderStatus.Delivered, 0), "c1");
            var result = await _handler.HandleAsync(Ev(OrderStatus.NotDelivered, 30), "c2");
            Assert.Equal(ProcessingResult.IgnoredFinalState, result);
            Assert.Equal(OrderStatus.Delivered, _store.Peek(OrderNumber)!.Status);
            Assert.Equal(1, _store.Peek(OrderNumber)!.VisitCount);
            Assert.Equal(2, _store.History.Count);
            Assert.Equal(ApplyOutcome.IgnoredFinalState, _store.History[1].Outcome);
            Assert.Single(_store.Outbox);
        }

        [Fact]
        public async Task Third_NotDelivered_queues_a_ToBeReturn_event_in_the_outbox()
        {
            await _handler.HandleAsync(Ev(OrderStatus.NotDelivered, 0), "c");
            await _handler.HandleAsync(Ev(OrderStatus.NotDelivered, 60), "c");
            await _handler.HandleAsync(Ev(OrderStatus.NotDelivered, 120), "c");
            var followUp = Assert.Single(_store.Outbox.Select(o => o.Payload).OfType<TrackingEvent>());
            Assert.Equal(OrderStatus.ToBeReturn, followUp.Status);
            Assert.Equal(EventOrigin.System, followUp.Origin);
            Assert.Equal(3, _store.Peek(OrderNumber)!.VisitCount);
        }

        [Fact]
        public async Task Auto_generated_ToBeReturn_is_processed_like_any_other_event()
        {
            await _handler.HandleAsync(Ev(OrderStatus.NotDelivered, 0), "c");
            await _handler.HandleAsync(Ev(OrderStatus.NotDelivered, 60), "c");
            await _handler.HandleAsync(Ev(OrderStatus.NotDelivered, 120), "c");
            var followUp = _store.Outbox.Select(o => o.Payload).OfType<TrackingEvent>().Single();
            var result = await _handler.HandleAsync(followUp, "c");
            Assert.Equal(ProcessingResult.Applied, result);
            Assert.Equal(OrderStatus.ToBeReturn, _store.Peek(OrderNumber)!.Status);
            Assert.Equal(3, _store.Peek(OrderNumber)!.VisitCount);
        }

        [Fact]
        public async Task Unknown_order_fails_with_a_transient_error_and_commits_nothing()
        {
            var unknown = new TrackingEvent("NO-EXISTE", OrderStatus.Started, T0);
            await Assert.ThrowsAsync<OrderNotFoundException>(() => _handler.HandleAsync(unknown, "c"));
            Assert.Equal(0, _store.CommitAttempts);
            Assert.Empty(_store.History);
        }

        [Fact]
        public async Task Concurrency_conflict_is_retried_and_the_event_is_applied_once()
        {
            _store.ConcurrencyFailuresToInject = 1;
            var result = await _handler.HandleAsync(Ev(OrderStatus.NotDelivered), "c");
            Assert.Equal(ProcessingResult.Applied, result);
            Assert.Equal(2, _store.CommitAttempts);
            Assert.Equal(1, _store.Peek(OrderNumber)!.VisitCount);
            Assert.Single(_store.History);
        }

        [Fact]
        public async Task Persistent_concurrency_conflict_gives_up_and_leaves_the_order_untouched()
        {
            _store.ConcurrencyFailuresToInject = 5;
            await Assert.ThrowsAsync<ConcurrencyException>(() => _handler.HandleAsync(Ev(OrderStatus.NotDelivered), "c"));
            Assert.Equal(3, _store.CommitAttempts);
            Assert.Null(_store.Peek(OrderNumber)!.Status);
            Assert.Empty(_store.History);
        }
    }
}
