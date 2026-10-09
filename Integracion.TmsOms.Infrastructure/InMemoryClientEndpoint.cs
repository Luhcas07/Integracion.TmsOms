using Integracion.TmsOms.Application;

namespace Integracion.TmsOms.Infrastructure
{
    public sealed record ReceivedNotification(string ClientCode, OutboundNotification Notification, DateTimeOffset ReceivedAt);

    public sealed class InMemoryClientEndpoint(TimeProvider? timeProvider = null) : IClientEndpoint
    {
        private sealed class Behavior
        {
            public int FailRemaining { get; set; }
            public bool RejectAlways { get; set; }
        }

        private readonly Lock _lock = new();
        private readonly Dictionary<string, Behavior> _behaviors = new(StringComparer.Ordinal);
        private readonly List<ReceivedNotification> _received = [];
        private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

        public void Configure(string clientCode, int failFirstAttempts, bool rejectAlways)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(clientCode);
            lock (_lock)
            {
                _behaviors[clientCode] = new Behavior
                {
                    FailRemaining = Math.Max(0, failFirstAttempts),
                    RejectAlways = rejectAlways
                };
            }
        }

        public Task SendAsync(string clientCode, OutboundNotification notification, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                if (_behaviors.TryGetValue(clientCode, out var behavior))
                {
                    if (behavior.RejectAlways)
                    {
                        throw new PermanentFailureException("El sistema del cliente rechazó la notificación.");
                    }
                    if (behavior.FailRemaining > 0)
                    {
                        behavior.FailRemaining--;
                        throw new TransientException("El sistema del cliente no responde.");
                    }
                }
                _received.Add(new ReceivedNotification(clientCode, notification, _time.GetUtcNow()));
            }
            return Task.CompletedTask;
        }

        public IReadOnlyList<ReceivedNotification> GetReceived(string clientCode)
        {
            lock (_lock)
            {
                return [.. _received.Where(r => r.ClientCode == clientCode)];
            }
        }
    }
}
