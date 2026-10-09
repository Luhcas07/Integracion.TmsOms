using Integracion.TmsOms.Application;
using Integracion.TmsOms.Infrastructure;

namespace Integracion.TmsOms.Tests
{
    public class DeferTests
    {
        private readonly ManualTimeProvider _time = new(new DateTimeOffset(2025, 4, 15, 22, 0, 0, TimeSpan.Zero));

        private InMemoryMessageQueue<string> NewQueue() =>
            new(new QueueOptions { MaxDeliveryCount = 3, LockDurationSeconds = 30, RedeliveryDelayMilliseconds = 500 }, _time);

        [Fact]
        public async Task Deferred_message_does_not_count_as_a_delivery_and_waits_for_the_delay()
        {
            var queue = NewQueue();
            await queue.SendAsync("m", "k", "c");
            var first = await queue.ReceiveAsync();
            Assert.Equal(1, first!.DeliveryCount);
            await first.DeferAsync(TimeSpan.FromSeconds(5));
            Assert.Null(await queue.ReceiveAsync());
            _time.Advance(TimeSpan.FromSeconds(5));
            var second = await queue.ReceiveAsync();
            Assert.NotNull(second);
            Assert.Equal(1, second!.DeliveryCount);
            Assert.Empty(queue.DeadLetters);
        }

        [Fact]
        public async Task Handle_that_was_deferred_cannot_settle_the_next_delivery()
        {
            var queue = NewQueue();
            await queue.SendAsync("m", "k", "c");
            var first = await queue.ReceiveAsync();
            await first!.DeferAsync(TimeSpan.Zero);
            var second = await queue.ReceiveAsync();
            await first.CompleteAsync();
            Assert.Equal(1, queue.PendingCount);
            await second!.CompleteAsync();
            Assert.Equal(0, queue.PendingCount);
        }

        [Fact]
        public async Task Consumer_defers_the_message_when_the_handler_asks_for_it()
        {
            var queue = NewQueue();
            await queue.SendAsync("m", "k", "c");
            var consumer = new MessageConsumer<string>(
                queue,
                (_, _, _) => throw new DeferMessageException(TimeSpan.FromSeconds(5), "pospuesto"));
            Assert.Equal(ConsumeOutcome.Deferred, (await consumer.ProcessNextAsync()).Outcome);
            Assert.Equal(ConsumeOutcome.NoMessage, (await consumer.ProcessNextAsync()).Outcome);
            _time.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal(ConsumeOutcome.Deferred, (await consumer.ProcessNextAsync()).Outcome);
            Assert.Empty(queue.DeadLetters);
        }
    }
}
