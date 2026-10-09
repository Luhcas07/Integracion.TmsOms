using Integracion.TmsOms.Application;

namespace Integracion.TmsOms.Tests
{
    public class CircuitBreakerTests
    {
        private readonly ManualTimeProvider _time = new(new DateTimeOffset(2025, 4, 15, 22, 0, 0, TimeSpan.Zero));

        private CircuitBreaker NewBreaker() =>
            new(new CircuitBreakerOptions { FailureThreshold = 3, OpenDuration = TimeSpan.FromSeconds(10) }, _time);

        private static async Task TripAsync(CircuitBreaker breaker)
        {
            for (var i = 0; i < 3; i++)
            {
                await Assert.ThrowsAsync<TransientException>(
                    () => breaker.ExecuteAsync(_ => throw new TransientException("caída")));
            }
        }

        [Fact]
        public async Task Opens_after_the_failure_threshold_and_rejects_without_calling_the_action()
        {
            var breaker = NewBreaker();
            await TripAsync(breaker);
            Assert.Equal(CircuitBreakerState.Open, breaker.State);
            var called = false;
            await Assert.ThrowsAsync<CircuitOpenException>(() => breaker.ExecuteAsync(_ =>
            {
                called = true;
                return Task.CompletedTask;
            }));
            Assert.False(called);
        }

        [Fact]
        public async Task A_success_resets_the_failure_count()
        {
            var breaker = NewBreaker();
            for (var i = 0; i < 2; i++)
            {
                await Assert.ThrowsAsync<TransientException>(() => breaker.ExecuteAsync(_ => throw new TransientException("x")));
            }
            await breaker.ExecuteAsync(_ => Task.CompletedTask);
            for (var i = 0; i < 2; i++)
            {
                await Assert.ThrowsAsync<TransientException>(() => breaker.ExecuteAsync(_ => throw new TransientException("x")));
            }
            Assert.Equal(CircuitBreakerState.Closed, breaker.State);
        }

        [Fact]
        public async Task After_the_open_duration_one_probe_is_allowed_and_a_success_closes_the_circuit()
        {
            var breaker = NewBreaker();
            await TripAsync(breaker);
            _time.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(CircuitBreakerState.HalfOpen, breaker.State);
            await breaker.ExecuteAsync(_ => Task.CompletedTask);
            Assert.Equal(CircuitBreakerState.Closed, breaker.State);
        }

        [Fact]
        public async Task A_failed_probe_reopens_the_circuit()
        {
            var breaker = NewBreaker();
            await TripAsync(breaker);
            _time.Advance(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<TransientException>(() => breaker.ExecuteAsync(_ => throw new TransientException("sigue caído")));
            Assert.Equal(CircuitBreakerState.Open, breaker.State);
            await Assert.ThrowsAsync<CircuitOpenException>(() => breaker.ExecuteAsync(_ => Task.CompletedTask));
        }

        [Fact]
        public async Task Permanent_failures_do_not_trip_the_circuit()
        {
            var breaker = NewBreaker();
            for (var i = 0; i < 5; i++)
            {
                await Assert.ThrowsAsync<PermanentFailureException>(
                    () => breaker.ExecuteAsync(_ => throw new PermanentFailureException("rechazado")));
            }
            Assert.Equal(CircuitBreakerState.Closed, breaker.State);
        }

        [Fact]
        public async Task Rejection_asks_the_caller_to_defer_the_message()
        {
            var breaker = NewBreaker();
            await TripAsync(breaker);
            var ex = await Assert.ThrowsAsync<CircuitOpenException>(() => breaker.ExecuteAsync(_ => Task.CompletedTask));
            Assert.IsType<DeferMessageException>(ex, exactMatch: false);
            Assert.True(ex.RetryAfter > TimeSpan.Zero);
        }
    }
}
