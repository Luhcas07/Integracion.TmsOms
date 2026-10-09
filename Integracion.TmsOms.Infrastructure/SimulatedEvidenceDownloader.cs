using Integracion.TmsOms.Application;
using Integracion.TmsOms.Domain;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Integracion.TmsOms.Infrastructure
{
    public sealed class SimulatedEvidenceDownloader : IEvidenceDownloader
    {
        private static readonly Regex TransientMarker =
            new(@"FAIL-TRANSIENT-(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly Lock _lock = new();

        private readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);

        public Task<DownloadedFile> DownloadAsync(EvidenceRef evidence, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(evidence);
            var name = evidence.FileName;
            if (name.Contains("FAIL-PERMANENT", StringComparison.OrdinalIgnoreCase))
            {
                throw new PermanentFailureException($"El archivo '{name}' no existe en el TMS (404 simulado).");
            }
            var match = TransientMarker.Match(name);
            if (match.Success)
            {
                var failures = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                int attempt;
                lock (_lock)
                {
                    _attempts.TryGetValue(evidence.Url, out attempt);
                    attempt++;
                    _attempts[evidence.Url] = attempt;
                }
                if (attempt <= failures)
                {
                    throw new TransientException($"Timeout simulado al descargar '{name}' (intento {attempt}).");
                }
            }
            var content = Encoding.UTF8.GetBytes($"Evidencia simulada: {evidence.Label} - {name}");
            return Task.FromResult(new DownloadedFile(content, ContentTypeFor(name)));
        }

        private static string ContentTypeFor(string fileName) =>
            Path.GetExtension(fileName).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".pdf" => "application/pdf",
                _ => "application/octet-stream"
            };
    }
}
