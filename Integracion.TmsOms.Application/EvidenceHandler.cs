using Integracion.TmsOms.Domain;

namespace Integracion.TmsOms.Application
{
    public sealed class EvidenceHandler(
        IEvidenceDownloader downloader,
        IEvidenceStorage storage,
        IEvidenceCatalog catalog,
        IIdempotencyStore idempotency,
        IRetryPolicy retry,
        EvidenceOptions options,
        TimeProvider? clock = null)
    {
        public const string ConsumerName = "evidence-worker";

        private readonly IEvidenceDownloader _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));

        private readonly IEvidenceStorage _storage = storage ?? throw new ArgumentNullException(nameof(storage));

        private readonly IEvidenceCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

        private readonly IIdempotencyStore _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));

        private readonly IRetryPolicy _retry = retry ?? throw new ArgumentNullException(nameof(retry));

        private readonly EvidenceOptions _options = options ?? throw new ArgumentNullException(nameof(options));

        private readonly TimeProvider _clock = clock ?? TimeProvider.System;

        public async Task HandleAsync(
            StatusChangedMessage message,
            string correlationId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentNullException.ThrowIfNull(correlationId);
            if (!message.Status.RequiresEvidence() || message.Evidences.Count == 0)
            {
                return;
            }
            if (await _idempotency.HasProcessedAsync(ConsumerName, message.EventKey, cancellationToken))
            {
                return;
            }
            var transientFailures = new List<string>();
            var permanentFailures = new List<string>();
            foreach (var evidence in message.Evidences)
            {
                try
                {
                    await SaveEvidenceAsync(message, evidence, cancellationToken);
                }
                catch (TransientException ex)
                {
                    transientFailures.Add($"{evidence.FileName}: {ex.Message}");
                }
                catch (PermanentFailureException ex)
                {
                    permanentFailures.Add($"{evidence.FileName}: {ex.Message}");
                }
            }
            if (transientFailures.Count > 0)
            {
                throw new TransientException("No se pudieron guardar algunas evidencias: " + string.Join("; ", transientFailures));
            }
            if (permanentFailures.Count > 0)
            {
                throw new PermanentFailureException("Evidencias inválidas o inaccesibles: " + string.Join("; ", permanentFailures));
            }
            await _idempotency.MarkProcessedAsync(ConsumerName, message.EventKey, cancellationToken);
        }

        private async Task SaveEvidenceAsync(StatusChangedMessage message, EvidenceRef evidence, CancellationToken cancellationToken)
        {
            var extension = Path.GetExtension(evidence.FileName);
            if (!_options.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                throw new PermanentFailureException($"Tipo de archivo no permitido: '{extension}'.");
            }
            var path = StoragePaths.ForEvidence(message.OrderNumber, message.Status, message.EventDate, evidence.FileName);
            var size = await _storage.GetSizeAsync(path, cancellationToken);
            if (size is null)
            {
                var file = await _retry.ExecuteAsync(
                    token => _downloader.DownloadAsync(evidence, token),
                    cancellationToken);
                if (file.Content.Length == 0)
                {
                    throw new PermanentFailureException("El archivo descargado está vacío.");
                }
                if (file.Content.Length > _options.MaxSizeBytes)
                {
                    throw new PermanentFailureException(
                        $"El archivo supera el tamaño máximo de {_options.MaxSizeBytes} bytes.");
                }
                await _retry.ExecuteAsync(
                    token => _storage.SaveAsync(path, file.Content, file.ContentType, token),
                    cancellationToken);
                size = file.Content.Length;
            }
            await _catalog.UpsertAsync(
                new EvidenceRecord(
                    message.OrderNumber,
                    message.Status,
                    message.EventDate,
                    evidence.Label,
                    evidence.FileName,
                    path,
                    size.Value,
                    evidence.Url,
                    _clock.GetUtcNow()
                ),
                cancellationToken
            );
        }
    }
}
