namespace Integracion.TmsOms.Application
{
    public interface IIdempotencyStore
    {
        Task<bool> HasProcessedAsync(string consumer, string key, CancellationToken cancellationToken = default);

        Task MarkProcessedAsync(string consumer, string key, CancellationToken cancellationToken = default);
    }
}
