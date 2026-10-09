using Integracion.TmsOms.Domain;

namespace Integracion.TmsOms.Application
{
    public sealed record DownloadedFile(byte[] Content, string ContentType);

    public interface IEvidenceDownloader
    {
        Task<DownloadedFile> DownloadAsync(EvidenceRef evidence, CancellationToken cancellationToken = default);
    }

    public interface IEvidenceStorage
    {
        Task<long?> GetSizeAsync(string path, CancellationToken cancellationToken = default);

        Task SaveAsync(string path, byte[] content, string contentType, CancellationToken cancellationToken = default);
    }

    public sealed record EvidenceRecord(
        string OrderNumber,
        OrderStatus Status,
        DateTimeOffset EventDate,
        string Label,
        string FileName,
        string StoragePath,
        long SizeBytes,
        string SourceUrl,
        DateTimeOffset SavedAt);

    public interface IEvidenceCatalog
    {
        Task UpsertAsync(EvidenceRecord record, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<EvidenceRecord>> GetByOrderAsync(string orderNumber, CancellationToken cancellationToken = default);
    }

    public sealed class EvidenceOptions
    {
        public long MaxSizeBytes { get; set; } = 5 * 1024 * 1024;

        public List<string> AllowedExtensions { get; set; } = [".jpg", ".jpeg", ".png", ".pdf"];
    }
}
