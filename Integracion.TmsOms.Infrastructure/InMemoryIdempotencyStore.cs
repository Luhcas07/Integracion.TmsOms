using Integracion.TmsOms.Application;

namespace Integracion.TmsOms.Infrastructure
{
    public sealed class InMemoryIdempotencyStore : IIdempotencyStore
    {
        private readonly Lock _lock = new();

        private readonly HashSet<string> _processed = new(StringComparer.Ordinal);

        public Task<bool> HasProcessedAsync(string consumer, string key, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                return Task.FromResult(_processed.Contains($"{consumer}|{key}"));
            }
        }

        public Task MarkProcessedAsync(string consumer, string key, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _processed.Add($"{consumer}|{key}");
            }
            return Task.CompletedTask;
        }
    }
}
