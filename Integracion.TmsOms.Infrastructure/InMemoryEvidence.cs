using Integracion.TmsOms.Application;

namespace Integracion.TmsOms.Infrastructure
{
    public sealed record StoredFile(byte[] Content, string ContentType);

    public sealed class InMemoryEvidenceStorage : IEvidenceStorage
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, StoredFile> _files = new(StringComparer.Ordinal);

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _files.Count;
                }
            }
        }

        public IReadOnlyList<string> Paths
        {
            get
            {
                lock (_lock)
                {
                    return [.. _files.Keys.OrderBy(k => k, StringComparer.Ordinal)];
                }
            }
        }

        public bool Contains(string path)
        {
            lock (_lock)
            {
                return _files.ContainsKey(path);
            }
        }

        public Task<long?> GetSizeAsync(string path, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                return Task.FromResult(_files.TryGetValue(path, out var file) ? file.Content.LongLength : (long?)null);
            }
        }

        public Task SaveAsync(string path, byte[] content, string contentType, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _files[path] = new StoredFile(content, contentType);
            }
            return Task.CompletedTask;
        }
    }

    public sealed class InMemoryEvidenceCatalog : IEvidenceCatalog
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, EvidenceRecord> _records = new(StringComparer.Ordinal);

        public Task UpsertAsync(EvidenceRecord record, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(record);
            lock (_lock)
            {
                _records[record.StoragePath] = record;
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<EvidenceRecord>> GetByOrderAsync(string orderNumber, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                IReadOnlyList<EvidenceRecord> result = [.. _records.Values
                    .Where(r => r.OrderNumber == orderNumber)
                    .OrderBy(r => r.EventDate)
                    .ThenBy(r => r.FileName, StringComparer.Ordinal)];
                return Task.FromResult(result);
            }
        }
    }
}
