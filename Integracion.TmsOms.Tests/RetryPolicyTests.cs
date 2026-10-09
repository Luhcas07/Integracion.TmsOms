using Integracion.TmsOms.Application;

namespace Integracion.TmsOms.Tests
{
    public class RetryPolicyTests
    {
        private static RetryPolicy Policy(int attempts) =>
            new(new RetryOptions { MaxAttempts = attempts, InitialDelay = TimeSpan.Zero });

        [Fact]
        public async Task Succeeds_on_first_attempt_without_retrying()
        {
            var calls = 0;
            var result = await Policy(3).ExecuteAsync(_ =>
            {
                calls++;
                return Task.FromResult(42);
            });
            Assert.Equal(42, result);
            Assert.Equal(1, calls);
        }

        [Fact]
        public async Task Retries_transient_failures_until_it_succeeds()
        {
            var calls = 0;
            var result = await Policy(3).ExecuteAsync(_ =>
            {
                calls++;
                if (calls < 3) throw new TransientException("fallo temporal");
                return Task.FromResult("ok");
            });
            Assert.Equal("ok", result);
            Assert.Equal(3, calls);
        }

        [Fact]
        public async Task Gives_up_after_the_maximum_number_of_attempts()
        {
            var calls = 0;
            await Assert.ThrowsAsync<TransientException>(() =>
                Policy(3).ExecuteAsync(_ =>
                {
                    calls++;
                    throw new TransientException("siempre falla");
                }));
            Assert.Equal(3, calls);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Does_not_retry_non_transient_failures(bool permanent)
        {
            var calls = 0;
            Exception failure = permanent
                ? new PermanentFailureException("sin arreglo")
                : new InvalidOperationException("error inesperado");
            await Assert.ThrowsAnyAsync<Exception>(() =>
                Policy(3).ExecuteAsync(_ =>
                {
                    calls++;
                    throw failure;
                }));
            Assert.Equal(1, calls);
        }
    }
}
