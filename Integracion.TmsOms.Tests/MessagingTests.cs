using Integracion.TmsOms.Infrastructure;

namespace Integracion.TmsOms.Tests
{
    internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    public class MessagingTests
    {
        private readonly ManualTimeProvider _time = new(new DateTimeOffset(2025, 4, 15, 22, 0, 0, TimeSpan.Zero));

        private InMemoryMessageQueue<string> NewQueue(int maxDeliveries = 3) =>
            new(new QueueOptions
            {
                MaxDeliveryCount = maxDeliveries,
                LockDurationSeconds = 30,
                RedeliveryDelayMilliseconds = 500
            }, _time);

        [Fact]
        public async Task Messages_of_the_same_partition_are_delivered_in_order_one_at_a_time()
        {
            var queue = NewQueue();
            await queue.SendAsync("A1", "A", "c");
            await queue.SendAsync("A2", "A", "c");
            var first = await queue.ReceiveAsync();
            Assert.Equal("A1", first!.Payload);
            Assert.Null(await queue.ReceiveAsync());
            await first.CompleteAsync();
            var second = await queue.ReceiveAsync();
            Assert.Equal("A2", second!.Payload);
        }

        [Fact]
        public async Task Messages_of_different_partitions_are_delivered_in_parallel()
        {
            var queue = NewQueue();
            await queue.SendAsync("A1", "A", "c");
            await queue.SendAsync("B1", "B", "c");
            var first = await queue.ReceiveAsync();
            var second = await queue.ReceiveAsync();
            Assert.Equal("A1", first!.Payload);
            Assert.Equal("B1", second!.Payload);
        }

        [Fact]
        public async Task Abandoned_message_is_redelivered_after_the_delay_with_a_higher_delivery_count()
        {
            var queue = NewQueue();
            await queue.SendAsync("m", "k", "c");
            var first = await queue.ReceiveAsync();
            Assert.Equal(1, first!.DeliveryCount);
            await first.AbandonAsync();
            Assert.Null(await queue.ReceiveAsync());
            _time.Advance(TimeSpan.FromMilliseconds(500));
            var second = await queue.ReceiveAsync();
            Assert.NotNull(second);
            Assert.Equal(2, second!.DeliveryCount);
            Assert.Equal(first.MessageId, second.MessageId);
        }

        [Fact]
        public async Task Message_goes_to_the_dead_letter_queue_after_the_maximum_deliveries()
        {
            var queue = NewQueue(maxDeliveries: 2);
            await queue.SendAsync("m", "k", "c");
            await (await queue.ReceiveAsync())!.AbandonAsync();
            _time.Advance(TimeSpan.FromSeconds(1));
            await (await queue.ReceiveAsync())!.AbandonAsync();
            Assert.Equal(0, queue.PendingCount);
            var dead = Assert.Single(queue.DeadLetters);
            Assert.Equal(2, dead.DeliveryCount);
            Assert.False(string.IsNullOrWhiteSpace(dead.Reason));
        }

        [Fact]
        public async Task Expired_lock_makes_the_message_available_again()
        {
            var queue = NewQueue();
            await queue.SendAsync("m", "k", "c");
            var first = await queue.ReceiveAsync();
            Assert.NotNull(first);
            _time.Advance(TimeSpan.FromSeconds(31));
            var second = await queue.ReceiveAsync();
            Assert.NotNull(second);
            Assert.Equal(first!.MessageId, second!.MessageId);
            Assert.Equal(2, second.DeliveryCount);
        }

        [Fact]
        public async Task Dead_letter_can_be_reprocessed_back_into_the_queue()
        {
            var queue = NewQueue();
            await queue.SendAsync("m", "k", "corr-9");
            var message = await queue.ReceiveAsync();
            await message!.DeadLetterAsync("mensaje inválido");
            Assert.Equal(0, queue.PendingCount);
            var dead = Assert.Single(queue.DeadLetters);
            Assert.Equal("mensaje inválido", dead.Reason);
            Assert.True(queue.Reprocess(dead.MessageId));
            Assert.Empty(queue.DeadLetters);
            var again = await queue.ReceiveAsync();
            Assert.NotNull(again);
            Assert.Equal(1, again!.DeliveryCount);
            Assert.Equal("corr-9", again.CorrelationId);
        }

        [Fact]
        public async Task Stale_handle_cannot_settle_a_message_that_was_redelivered()
        {
            var queue = NewQueue();
            await queue.SendAsync("m", "k", "c");
            var oldHandle = await queue.ReceiveAsync();
            _time.Advance(TimeSpan.FromSeconds(31));
            var newHandle = await queue.ReceiveAsync();
            await oldHandle!.CompleteAsync();
            Assert.Equal(1, queue.PendingCount);
            await newHandle!.CompleteAsync();
            Assert.Equal(0, queue.PendingCount);
        }

        [Fact]
        public async Task Topic_delivers_a_copy_to_each_subscription_applying_its_filter()
        {
            var topic = new InMemoryTopic<string>(new QueueOptions(), _time);
            var all = topic.Subscribe("all");
            var longOnly = topic.Subscribe("long", s => s.Length > 3);
            await topic.SendAsync("ab", "k", "c");
            await topic.SendAsync("abcd", "k", "c");
            Assert.Equal(2, all.PendingCount);
            Assert.Equal(1, longOnly.PendingCount);
        }
    }
}
