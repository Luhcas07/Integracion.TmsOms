using Integracion.TmsOms.Application;

namespace Integracion.TmsOms.Infrastructure
{
    public sealed class InMemoryTopic<T>(QueueOptions? options = null, TimeProvider? timeProvider = null) : IMessageSender<T>
    {
        private sealed record Subscription(string Name, Func<T, bool> Filter, InMemoryMessageQueue<T> Queue);

        private readonly Lock _lock = new();

        private readonly Dictionary<string, Subscription> _subscriptions = [];

        private readonly QueueOptions _options = options ?? new QueueOptions();

        private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

        public IReadOnlyDictionary<string, InMemoryMessageQueue<T>> Subscriptions
        {
            get
            {
                lock (_lock)
                {
                    return _subscriptions.ToDictionary(s => s.Key, s => s.Value.Queue);
                }
            }
        }

        public InMemoryMessageQueue<T> Subscribe(string name, Func<T, bool>? filter = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            lock (_lock)
            {
                if (_subscriptions.ContainsKey(name))
                {
                    throw new InvalidOperationException($"Ya existe la suscripción '{name}'.");
                }
                var queue = new InMemoryMessageQueue<T>(_options, _time);
                _subscriptions[name] = new Subscription(name, filter ?? (_ => true), queue);
                return queue;
            }
        }

        public async Task SendAsync(T payload, string partitionKey, string correlationId, CancellationToken cancellationToken = default)
        {
            Subscription[] targets;
            lock (_lock)
            {
                targets = [.. _subscriptions.Values];
            }
            foreach (var subscription in targets)
            {
                if (subscription.Filter(payload))
                {
                    await subscription.Queue.SendAsync(payload, partitionKey, correlationId, cancellationToken);
                }
            }
        }
    }
}
