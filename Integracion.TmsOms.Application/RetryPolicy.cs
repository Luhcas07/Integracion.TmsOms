namespace Integracion.TmsOms.Application
{
    public sealed class RetryOptions
    {
        public int MaxAttempts { get; init; } = 3;

        public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(200);

        public double BackoffFactor { get; init; } = 2.0;

        public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(5);
    }

    public interface IRetryPolicy
    {
        Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default);

        Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default);
    }

    public sealed class RetryPolicy : IRetryPolicy
    {
        private readonly RetryOptions _options;

        private readonly TimeProvider _time;

        public RetryPolicy(RetryOptions options, TimeProvider? timeProvider = null)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (options.MaxAttempts < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "MaxAttempts debe ser al menos 1.");
            }
            _options = options;
            _time = timeProvider ?? TimeProvider.System;
        }

        public async Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(action);
            await ExecuteAsync<object?>(
                async token =>
                {
                    await action(token);
                    return null;
                },
                cancellationToken
            );
        }

        public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(action);
            var delay = _options.InitialDelay;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await action(cancellationToken);
                }
                catch (TransientException) when (attempt < _options.MaxAttempts)
                {
                    await Task.Delay(delay, _time, cancellationToken);
                    delay = NextDelay(delay);
                }
            }
        }

        private TimeSpan NextDelay(TimeSpan current)
        {
            var next = (long)(current.Ticks * _options.BackoffFactor);
            return TimeSpan.FromTicks(Math.Min(next, _options.MaxDelay.Ticks));
        }
    }
}
