using Integracion.TmsOms.Application;

namespace Integracion.TmsOms.Infrastructure
{
    public sealed record DeadLetterEntry<T>(
        string MessageId,
        T Payload,
        string PartitionKey,
        string CorrelationId,
        string Reason,
        int DeliveryCount,
        DateTimeOffset DeadLetteredAt
    );

    public sealed class InMemoryMessageQueue<T>(QueueOptions? options = null, TimeProvider? timeProvider = null) : IMessageSender<T>, IMessageReceiver<T>
    {
        private enum EntryState
        {
            Available,
            InFlight
        }

        private sealed class Entry
        {
            public required string MessageId { get; init; }
            public required T Payload { get; init; }
            public required string PartitionKey { get; init; }
            public required string CorrelationId { get; init; }
            public int DeliveryCount { get; set; }
            public long LockId { get; set; }
            public EntryState State { get; set; } = EntryState.Available;
            public DateTimeOffset AvailableAt { get; set; }
            public DateTimeOffset LockedUntil { get; set; }
        }

        private readonly Lock _lock = new();

        private readonly List<Entry> _entries = [];

        private readonly List<DeadLetterEntry<T>> _deadLetters = [];

        private readonly QueueOptions _options = options ?? new QueueOptions();

        private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

        private long _sequence;

        public int PendingCount
        {
            get
            {
                lock (_lock)
                {
                    return _entries.Count;
                }
            }
        }

        public IReadOnlyList<DeadLetterEntry<T>> DeadLetters
        {
            get
            {
                lock (_lock)
                {
                    return [.. _deadLetters];
                }
            }
        }

        public Task SendAsync(T payload, string partitionKey, string correlationId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
            lock (_lock)
            {
                _sequence++;
                _entries.Add(new Entry
                {
                    MessageId = $"msg-{_sequence:D6}",
                    Payload = payload,
                    PartitionKey = partitionKey,
                    CorrelationId = correlationId,
                    AvailableAt = _time.GetUtcNow()
                });
            }
            return Task.CompletedTask;
        }

        public Task<IReceivedMessage<T>?> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                var now = _time.GetUtcNow();
                ReleaseExpiredLocks(now);
                var blockedKeys = new HashSet<string>();
                foreach (var entry in _entries)
                {
                    if (entry.State == EntryState.InFlight || blockedKeys.Contains(entry.PartitionKey))
                    {
                        blockedKeys.Add(entry.PartitionKey);
                        continue;
                    }
                    if (entry.AvailableAt > now)
                    {
                        blockedKeys.Add(entry.PartitionKey);
                        continue;
                    }
                    entry.State = EntryState.InFlight;
                    entry.DeliveryCount++;
                    entry.LockId++;
                    entry.LockedUntil = now + TimeSpan.FromSeconds(_options.LockDurationSeconds);
                    return Task.FromResult<IReceivedMessage<T>?>(new ReceivedMessage(this, entry));
                }
            }
            return Task.FromResult<IReceivedMessage<T>?>(null);
        }

        public bool Reprocess(string messageId)
        {
            lock (_lock)
            {
                var index = _deadLetters.FindIndex(d => d.MessageId == messageId);
                if (index < 0)
                {
                    return false;
                }
                var dead = _deadLetters[index];
                _deadLetters.RemoveAt(index);
                _entries.Add(new Entry
                {
                    MessageId = dead.MessageId,
                    Payload = dead.Payload,
                    PartitionKey = dead.PartitionKey,
                    CorrelationId = dead.CorrelationId,
                    AvailableAt = _time.GetUtcNow()
                });
                return true;
            }
        }

        private void ReleaseExpiredLocks(DateTimeOffset now)
        {
            foreach (var entry in _entries.ToList())
            {
                if (entry.State == EntryState.InFlight && entry.LockedUntil <= now)
                {
                    ReturnToQueue(entry, now, TimeSpan.Zero, "El bloqueo expiró y se superó el máximo de entregas.");
                }
            }
        }

        private void ReturnToQueue(Entry entry, DateTimeOffset now, TimeSpan delay, string deadLetterReason)
        {
            if (entry.DeliveryCount >= _options.MaxDeliveryCount)
            {
                MoveToDeadLetter(entry, now, deadLetterReason);
                return;
            }
            entry.State = EntryState.Available;
            entry.AvailableAt = now + delay;
        }

        private void MoveToDeadLetter(Entry entry, DateTimeOffset now, string reason)
        {
            _entries.Remove(entry);
            _deadLetters.Add(new DeadLetterEntry<T>(
                entry.MessageId, entry.Payload, entry.PartitionKey, entry.CorrelationId,
                reason, entry.DeliveryCount, now));
        }

        private void Settle(Entry entry, long lockId, Action<Entry, DateTimeOffset> action)
        {
            lock (_lock)
            {
                if (entry.State != EntryState.InFlight || entry.LockId != lockId || !_entries.Contains(entry))
                {
                    return;
                }
                action(entry, _time.GetUtcNow());
            }
        }

        private sealed class ReceivedMessage(InMemoryMessageQueue<T> queue, InMemoryMessageQueue<T>.Entry entry) : IReceivedMessage<T>
        {
            private readonly InMemoryMessageQueue<T> _queue = queue;

            private readonly Entry _entry = entry;

            private readonly int _delivery = entry.DeliveryCount;

            private readonly long _lockId = entry.LockId;

            public string MessageId => _entry.MessageId;

            public T Payload => _entry.Payload;

            public string CorrelationId => _entry.CorrelationId;

            public int DeliveryCount => _delivery;

            public Task CompleteAsync(CancellationToken cancellationToken = default)
            {
                _queue.Settle(_entry, _lockId, (e, _) => _queue._entries.Remove(e));
                return Task.CompletedTask;
            }

            public Task AbandonAsync(CancellationToken cancellationToken = default)
            {
                _queue.Settle(_entry, _lockId, (e, now) =>
                    _queue.ReturnToQueue(
                        e, now,
                        TimeSpan.FromMilliseconds(_queue._options.RedeliveryDelayMilliseconds),
                        "Se superó el máximo de entregas."));
                return Task.CompletedTask;
            }

            public Task DeferAsync(TimeSpan delay, CancellationToken cancellationToken = default)
            {
                _queue.Settle(_entry, _lockId, (e, now) =>
                {
                    e.DeliveryCount = Math.Max(0, e.DeliveryCount - 1);
                    e.State = EntryState.Available;
                    e.AvailableAt = now + delay;
                });
                return Task.CompletedTask;
            }

            public Task DeadLetterAsync(string reason, CancellationToken cancellationToken = default)
            {
                _queue.Settle(_entry, _lockId, (e, now) => _queue.MoveToDeadLetter(e, now, reason));
                return Task.CompletedTask;
            }
        }
    }
}
