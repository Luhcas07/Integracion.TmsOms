using Integracion.TmsOms.Application;
using Integracion.TmsOms.Domain;
using Integracion.TmsOms.Infrastructure;

namespace Integracion.TmsOms.Tests
{
    public class PipelineTests
    {
        private const string OrderNumber = "2500000006-01";

        private static readonly DateTimeOffset T0 = new(2025, 4, 15, 16, 53, 7, TimeSpan.FromHours(-5));

        private readonly ManualTimeProvider _time = new(new DateTimeOffset(2025, 4, 15, 22, 0, 0, TimeSpan.Zero));

        private readonly InMemoryOmsStore _store = new();

        private readonly InMemoryMessageQueue<TrackingEvent> _queue;

        private readonly InMemoryMessageQueue<StatusChangedMessage> _evidences;

        private readonly InMemoryMessageQueue<StatusChangedMessage> _notifications;

        private readonly MessageConsumer<TrackingEvent> _consumer;

        private readonly OutboxDispatcher _dispatcher;

        public PipelineTests()
        {
            var options = new QueueOptions
            {
                MaxDeliveryCount = 3,
                LockDurationSeconds = 30,
                RedeliveryDelayMilliseconds = 500
            };
            _queue = new InMemoryMessageQueue<TrackingEvent>(options, _time);
            var topic = new InMemoryTopic<StatusChangedMessage>(options, _time);
            _evidences = topic.Subscribe("evidences", m => m.Status.RequiresEvidence());
            _notifications = topic.Subscribe("notifications");
            _store.Seed(new Order(OrderNumber, "OE2500000006-01", "01021755", "TIENDAS PERUANAS S.A."));
            var retry = new RetryPolicy(new RetryOptions { MaxAttempts = 2, InitialDelay = TimeSpan.Zero });
            var handler = new TrackingEventHandler(_store, retry);
            _consumer = new MessageConsumer<TrackingEvent>(
                _queue, handler.HandleAsync);
            _dispatcher = new OutboxDispatcher(_store, topic, _queue);
        }

        private static TrackingEvent Ev(OrderStatus status, int minutes = 0) =>
            new(OrderNumber, status, T0.AddMinutes(minutes));

        private Task Send(TrackingEvent trackingEvent) =>
            _queue.SendAsync(trackingEvent, trackingEvent.OrderNumber, "corr");

        private async Task PumpAsync()
        {
            for (var i = 0; i < 20; i++)
            {
                var consumed = await _consumer.ProcessNextAsync();
                var dispatched = await _dispatcher.DispatchPendingAsync();
                if (consumed.Outcome == ConsumeOutcome.NoMessage && dispatched == 0)
                {
                    return;
                }
            }
        }

        [Fact]
        public async Task Delivered_event_reaches_both_notifications_and_evidences()
        {
            await Send(Ev(OrderStatus.Delivered));
            await PumpAsync();
            Assert.Equal(OrderStatus.Delivered, _store.Peek(OrderNumber)!.Status);
            Assert.Equal(1, _notifications.PendingCount);
            Assert.Equal(1, _evidences.PendingCount);
            Assert.Equal(0, _queue.PendingCount);
            Assert.Equal(0, _store.PendingOutboxCount);
        }

        [Fact]
        public async Task Planning_event_reaches_notifications_but_not_evidences()
        {
            await Send(Ev(OrderStatus.Planning));
            await PumpAsync();
            Assert.Equal(1, _notifications.PendingCount);
            Assert.Equal(0, _evidences.PendingCount);
        }

        [Fact]
        public async Task Three_failed_visits_leave_the_order_pending_return()
        {
            await Send(Ev(OrderStatus.NotDelivered, 0));
            await Send(Ev(OrderStatus.NotDelivered, 60));
            await Send(Ev(OrderStatus.NotDelivered, 120));
            await PumpAsync();
            var order = _store.Peek(OrderNumber)!;
            Assert.Equal(OrderStatus.ToBeReturn, order.Status);
            Assert.Equal(3, order.VisitCount);
            Assert.Equal(4, _notifications.PendingCount);
            Assert.Equal(3, _evidences.PendingCount);
            Assert.Empty(_queue.DeadLetters);
        }

        [Fact]
        public async Task Duplicate_webhook_does_not_double_count_the_visit()
        {
            var evt = Ev(OrderStatus.NotDelivered);
            await Send(evt);
            await Send(evt);
            await PumpAsync();
            Assert.Equal(1, _store.Peek(OrderNumber)!.VisitCount);
            Assert.Equal(1, _notifications.PendingCount);
            Assert.Single(_store.GetHistory(OrderNumber));
        }

        [Fact]
        public async Task Unknown_order_ends_up_in_the_dead_letter_queue_after_the_maximum_deliveries()
        {
            var unknown = new TrackingEvent("NO-EXISTE", OrderStatus.Started, T0);
            await _queue.SendAsync(unknown, unknown.OrderNumber, "corr");
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var result = await _consumer.ProcessNextAsync();
                Assert.Equal(ConsumeOutcome.Abandoned, result.Outcome);
                _time.Advance(TimeSpan.FromSeconds(1));
            }
            Assert.Equal(0, _queue.PendingCount);
            var dead = Assert.Single(_queue.DeadLetters);
            Assert.Equal(3, dead.DeliveryCount);
        }

        [Fact]
        public async Task Permanent_failure_goes_straight_to_the_dead_letter_queue()
        {
            var queue = new InMemoryMessageQueue<string>(new QueueOptions(), _time);
            await queue.SendAsync("x", "k", "corr");
            var consumer = new MessageConsumer<string>(
                queue, (_, _, _) => throw new PermanentFailureException("mensaje inválido"));
            var result = await consumer.ProcessNextAsync();
            Assert.Equal(ConsumeOutcome.DeadLettered, result.Outcome);
            Assert.Equal(0, queue.PendingCount);
            Assert.Single(queue.DeadLetters);
        }

        [Fact]
        public async Task Transient_failure_is_retried_after_the_redelivery_delay()
        {
            var queue = new InMemoryMessageQueue<string>(new QueueOptions(), _time);
            await queue.SendAsync("x", "k", "corr");
            var calls = 0;
            var consumer = new MessageConsumer<string>(queue, (_, _, _) =>
            {
                calls++;
                if (calls == 1)
                {
                    throw new TransientException("caída temporal");
                }
                return Task.CompletedTask;
            });
            Assert.Equal(ConsumeOutcome.Abandoned, (await consumer.ProcessNextAsync()).Outcome);
            Assert.Equal(ConsumeOutcome.NoMessage, (await consumer.ProcessNextAsync()).Outcome);
            _time.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(ConsumeOutcome.Completed, (await consumer.ProcessNextAsync()).Outcome);
            Assert.Equal(0, queue.PendingCount);
            Assert.Equal(2, calls);
        }
    }
}
