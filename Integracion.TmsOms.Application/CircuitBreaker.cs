using System.Collections.Concurrent;

namespace Integracion.TmsOms.Application
{
    public sealed class CircuitBreakerOptions
    {
        public int FailureThreshold { get; init; } = 3;

        public TimeSpan OpenDuration { get; init; } = TimeSpan.FromSeconds(10);
    }

    public enum CircuitBreakerState
    {
        Closed,
        Open,
        HalfOpen
    }

    public sealed class CircuitBreaker
    {
        private readonly CircuitBreakerOptions _options;
        private readonly TimeProvider _time;
        private readonly Lock _lock = new();
        private int _failures;
        private DateTimeOffset? _openUntil;
        private bool _probeInFlight;

        public CircuitBreaker(CircuitBreakerOptions options, TimeProvider? timeProvider = null)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (options.FailureThreshold < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "FailureThreshold debe ser al menos 1.");
            }
            _options = options;
            _time = timeProvider ?? TimeProvider.System;
        }

        public CircuitBreakerState State
        {
            get
            {
                lock (_lock)
                {
                    if (_openUntil is null)
                    {
                        return CircuitBreakerState.Closed;
                    }
                    return _time.GetUtcNow() < _openUntil.Value
                        ? CircuitBreakerState.Open
                        : CircuitBreakerState.HalfOpen;
                }
            }
        }

        public async Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(action);
            var isProbe = Acquire();
            try
            {
                await action(cancellationToken);
            }
            catch (TransientException)
            {
                RecordFailure(isProbe);
                throw;
            }
            catch
            {
                ReleaseProbe(isProbe);
                throw;
            }
            RecordSuccess();
        }

        private bool Acquire()
        {
            lock (_lock)
            {
                if (_openUntil is null)
                {
                    return false;
                }
                var now = _time.GetUtcNow();
                if (now < _openUntil.Value)
                {
                    throw new CircuitOpenException(_openUntil.Value - now);
                }
                if (_probeInFlight)
                {
                    throw new CircuitOpenException(TimeSpan.FromSeconds(1));
                }
                _probeInFlight = true;
                return true;
            }
        }

        private void RecordFailure(bool wasProbe)
        {
            lock (_lock)
            {
                _failures++;
                if (wasProbe)
                {
                    _probeInFlight = false;
                }
                if (wasProbe || _failures >= _options.FailureThreshold)
                {
                    _openUntil = _time.GetUtcNow() + _options.OpenDuration;
                }
            }
        }

        private void RecordSuccess()
        {
            lock (_lock)
            {
                _failures = 0;
                _openUntil = null;
                _probeInFlight = false;
            }
        }

        private void ReleaseProbe(bool wasProbe)
        {
            if (!wasProbe)
            {
                return;
            }
            lock (_lock)
            {
                _probeInFlight = false;
            }
        }
    }

    public sealed class CircuitBreakerRegistry(CircuitBreakerOptions options, TimeProvider? timeProvider = null)
    {
        private readonly ConcurrentDictionary<string, CircuitBreaker> _breakers = new(StringComparer.Ordinal);

        private readonly CircuitBreakerOptions _options = options ?? throw new ArgumentNullException(nameof(options));

        private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

        public CircuitBreaker For(string key) =>
            _breakers.GetOrAdd(key, _ => new CircuitBreaker(_options, _time));

        public IReadOnlyDictionary<string, CircuitBreakerState> Snapshot() =>
            _breakers.ToDictionary(b => b.Key, b => b.Value.State);
    }
}
