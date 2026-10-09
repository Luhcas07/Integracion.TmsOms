using Integracion.TmsOms.Application;
using Integracion.TmsOms.Domain;
using Integracion.TmsOms.Infrastructure;

namespace Integracion.TmsOms.Tests
{
    /// <summary>Envuelve un descargador y cuenta cuántas veces se pidió cada archivo.</summary>
    internal sealed class CountingDownloader(IEvidenceDownloader inner) : IEvidenceDownloader
    {
        private readonly IEvidenceDownloader _inner = inner;

        private readonly Dictionary<string, int> _calls = [];

        public int CallsFor(string fileName) => _calls.TryGetValue(fileName, out var n) ? n : 0;

        public int TotalCalls => _calls.Values.Sum();

        public Task<DownloadedFile> DownloadAsync(EvidenceRef evidence, CancellationToken cancellationToken = default)
        {
            _calls[evidence.FileName] = CallsFor(evidence.FileName) + 1;
            return _inner.DownloadAsync(evidence, cancellationToken);
        }
    }

    public class EvidenceHandlerTests
    {
        private const string OrderNumber = "2500000006-01";
        private static readonly DateTimeOffset T0 = new(2025, 4, 15, 16, 53, 7, TimeSpan.FromHours(-5));

        private readonly CountingDownloader _downloader = new(new SimulatedEvidenceDownloader());
        private readonly InMemoryEvidenceStorage _storage = new();
        private readonly InMemoryEvidenceCatalog _catalog = new();
        private readonly InMemoryIdempotencyStore _idempotency = new();

        private EvidenceHandler NewHandler(EvidenceOptions? options = null) =>
            new(_downloader, _storage, _catalog, _idempotency,
                new RetryPolicy(new RetryOptions { MaxAttempts = 3, InitialDelay = TimeSpan.Zero }),
                options ?? new EvidenceOptions());

        private static EvidenceRef Ev(string fileName) =>
            new("Paquete", ".jpg", fileName, $"htps://beetrack.example/img/{fileName}");

        private static StatusChangedMessage Msg(OrderStatus status, params EvidenceRef[] evidences) =>
            new(OrderNumber, "OE2500000006-01", "01021755", "TIENDAS PERUANAS S.A.", status, null,
                T0, ServiceType.LastMile, DispatchType.HomeDelivery, null, null, null, null,
                evidences, EventOrigin.Tms, 1, $"key-{status}");

        [Fact]
        public async Task Saves_each_evidence_under_a_path_tied_to_the_order_and_event()
        {
            await NewHandler().HandleAsync(Msg(OrderStatus.Delivered, Ev("front.jpg"), Ev("door.jpg")), "c");
            Assert.Equal(2, _storage.Count);
            Assert.True(_storage.Contains("2500000006-01/20250415T215307Z-Delivered/front.jpg"));
            var records = await _catalog.GetByOrderAsync(OrderNumber);
            Assert.Equal(2, records.Count);
            Assert.All(records, r => Assert.True(r.SizeBytes > 0));
        }

        [Fact]
        public async Task Statuses_without_evidence_are_ignored()
        {
            await NewHandler().HandleAsync(Msg(OrderStatus.Planning, Ev("x.jpg")), "c");
            Assert.Equal(0, _storage.Count);
            Assert.Equal(0, _downloader.TotalCalls);
        }

        [Fact]
        public async Task Duplicate_message_is_processed_once()
        {
            var handler = NewHandler();
            var message = Msg(OrderStatus.Delivered, Ev("front.jpg"));
            await handler.HandleAsync(message, "c");
            await handler.HandleAsync(message, "c");
            Assert.Equal(1, _downloader.CallsFor("front.jpg"));
            Assert.Equal(1, _storage.Count);
        }

        [Fact]
        public async Task Partial_failure_is_completed_on_the_next_delivery_without_repeating_finished_downloads()
        {
            var handler = NewHandler();
            var message = Msg(OrderStatus.NotDelivered, Ev("ok.jpg"), Ev("late-FAIL-TRANSIENT-4.jpg"));
            await Assert.ThrowsAsync<TransientException>(() => handler.HandleAsync(message, "c"));
            Assert.Equal(1, _storage.Count);
            await handler.HandleAsync(message, "c");
            Assert.Equal(2, _storage.Count);
            Assert.Equal(1, _downloader.CallsFor("ok.jpg"));
            Assert.Equal(5, _downloader.CallsFor("late-FAIL-TRANSIENT-4.jpg"));
        }

        [Fact]
        public async Task Permanent_failure_still_saves_the_other_evidences()
        {
            var message = Msg(OrderStatus.Delivered, Ev("good.jpg"), Ev("FAIL-PERMANENT.jpg"));
            await Assert.ThrowsAsync<PermanentFailureException>(() => NewHandler().HandleAsync(message, "c"));
            Assert.Equal(1, _storage.Count);
            Assert.Equal(1, _downloader.CallsFor("FAIL-PERMANENT.jpg"));
        }

        [Fact]
        public async Task File_types_that_are_not_allowed_are_rejected_before_downloading()
        {
            var virus = new EvidenceRef("x", ".exe", "virus.exe", "htps://beetrack.example/virus.exe");
            await Assert.ThrowsAsync<PermanentFailureException>(
                () => NewHandler().HandleAsync(Msg(OrderStatus.Delivered, virus), "c"));
            Assert.Equal(0, _downloader.TotalCalls);
            Assert.Equal(0, _storage.Count);
        }

        [Fact]
        public async Task Files_over_the_size_limit_are_rejected()
        {
            var handler = NewHandler(new EvidenceOptions { MaxSizeBytes = 10 });
            await Assert.ThrowsAsync<PermanentFailureException>(
                () => handler.HandleAsync(Msg(OrderStatus.Delivered, Ev("big.jpg")), "c"));
            Assert.Equal(0, _storage.Count);
        }

        [Fact]
        public void Evidence_paths_are_sanitized()
        {
            var path = StoragePaths.ForEvidence("2500/..", OrderStatus.Delivered, T0, "../../evil.jpg");
            var segments = path.Split('/');
            Assert.Equal(3, segments.Length);
            Assert.DoesNotContain("..", segments);
            Assert.Equal("evil.jpg", segments[2]);
            Assert.Equal("_", StoragePaths.Segment(".."));
        }
    }
}
