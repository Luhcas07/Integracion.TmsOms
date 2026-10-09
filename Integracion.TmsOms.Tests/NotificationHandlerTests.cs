using Integracion.TmsOms.Application;
using Integracion.TmsOms.Domain;
using Integracion.TmsOms.Infrastructure;

namespace Integracion.TmsOms.Tests
{
    public class NotificationHandlerTests
    {
        private static readonly DateTimeOffset T0 = new(2025, 4, 15, 16, 53, 7, TimeSpan.FromHours(-5));

        private readonly ManualTimeProvider _time = new(new DateTimeOffset(2025, 4, 15, 22, 0, 0, TimeSpan.Zero));

        private readonly InMemoryClientEndpoint _endpoint;

        private readonly NotificationHandler _handler;

        public NotificationHandlerTests()
        {
            var configs = new Dictionary<string, ClientConfig>
            {
                ["01021755"] = new() { Format = "standard-v1", Destination = "https://tp.example/tracking" },
                ["02034411"] = new()
                {
                    Format = "legacy-v1",
                    Destination = "https://sur.example/estado",
                    NotifyStatuses = [OrderStatus.Delivered, OrderStatus.NotDelivered]
                },
                ["77777777"] = new() { Format = "formato-inexistente", Destination = "https://x.example" }
            };
            _endpoint = new InMemoryClientEndpoint(_time);
            _handler = new NotificationHandler(
                new ConfiguredClientProvider(configs),
                new NotificationAdapterRegistry(
                [
                new StandardNotificationAdapter(),
                new LegacyNotificationAdapter()
                ]),
                _endpoint,
                new InMemoryIdempotencyStore(),
                new CircuitBreakerRegistry(
                    new CircuitBreakerOptions { FailureThreshold = 3, OpenDuration = TimeSpan.FromSeconds(10) }, _time),
                new RetryPolicy(new RetryOptions { MaxAttempts = 3, InitialDelay = TimeSpan.Zero })
            );
        }

        private static StatusChangedMessage Msg(OrderStatus status, string clientCode) =>
            new("2500000006-01", "OE2500000006-01", clientCode, "CLIENTE", status, "CLIENT RECEIVED",
                T0, ServiceType.LastMile, DispatchType.HomeDelivery, null, "Conductor 46", "Pepito Perez", null,
                [], EventOrigin.Tms, 1, $"key-{status}-{clientCode}");

        private async Task TripCircuitAsync(string clientCode)
        {
            _endpoint.Configure(clientCode, failFirstAttempts: 100, rejectAlways: false);
            await Assert.ThrowsAsync<TransientException>(
                () => _handler.HandleAsync(Msg(OrderStatus.Delivered, clientCode), "c"));
        }

        [Fact]
        public async Task Each_client_receives_the_notification_in_its_own_format()
        {
            await _handler.HandleAsync(Msg(OrderStatus.Delivered, "01021755"), "c");
            await _handler.HandleAsync(Msg(OrderStatus.Delivered, "02034411"), "c");
            var standard = Assert.Single(_endpoint.GetReceived("01021755"));
            var legacy = Assert.Single(_endpoint.GetReceived("02034411"));
            Assert.Equal("https://tp.example/tracking", standard.Notification.Destination);
            Assert.Contains("\"orderNumber\"", standard.Notification.Body);
            Assert.Contains("\"status\":\"DELIVERED\"", standard.Notification.Body);
            Assert.Equal("https://sur.example/estado", legacy.Notification.Destination);
            Assert.Contains("\"pedido\"", legacy.Notification.Body);
            Assert.DoesNotContain("\"orderNumber\"", legacy.Notification.Body);
        }

        [Fact]
        public async Task Duplicate_message_is_sent_only_once()
        {
            var message = Msg(OrderStatus.Delivered, "01021755");
            await _handler.HandleAsync(message, "c");
            await _handler.HandleAsync(message, "c");
            Assert.Single(_endpoint.GetReceived("01021755"));
        }

        [Fact]
        public async Task Client_status_filter_skips_statuses_the_client_did_not_ask_for()
        {
            await _handler.HandleAsync(Msg(OrderStatus.Started, "02034411"), "c");
            Assert.Empty(_endpoint.GetReceived("02034411"));
        }

        [Fact]
        public async Task Transient_client_failures_are_recovered_by_the_retry_policy()
        {
            _endpoint.Configure("01021755", failFirstAttempts: 2, rejectAlways: false);
            await _handler.HandleAsync(Msg(OrderStatus.Delivered, "01021755"), "c");
            Assert.Single(_endpoint.GetReceived("01021755"));
        }

        [Fact]
        public async Task Client_rejection_is_a_permanent_failure_and_does_not_trip_the_circuit()
        {
            var message = Msg(OrderStatus.Delivered, "01021755");
            _endpoint.Configure("01021755", failFirstAttempts: 0, rejectAlways: true);
            for (var i = 0; i < 4; i++)
            {
                await Assert.ThrowsAsync<PermanentFailureException>(() => _handler.HandleAsync(message, "c"));
            }
            Assert.Empty(_endpoint.GetReceived("01021755"));
            _endpoint.Configure("01021755", failFirstAttempts: 0, rejectAlways: false);
            await _handler.HandleAsync(message, "c");
            Assert.Single(_endpoint.GetReceived("01021755"));
        }

        [Fact]
        public async Task Client_without_configuration_is_a_permanent_failure()
        {
            await Assert.ThrowsAsync<PermanentFailureException>(
                () => _handler.HandleAsync(Msg(OrderStatus.Delivered, "00000000"), "c"));
        }

        [Fact]
        public async Task Unknown_notification_format_is_a_permanent_failure()
        {
            await Assert.ThrowsAsync<PermanentFailureException>(
                () => _handler.HandleAsync(Msg(OrderStatus.Delivered, "77777777"), "c"));
        }

        [Fact]
        public async Task Failing_client_trips_the_circuit_then_recovers()
        {
            var message = Msg(OrderStatus.Delivered, "01021755");
            await TripCircuitAsync("01021755");
            var deferred = await Assert.ThrowsAsync<CircuitOpenException>(() => _handler.HandleAsync(message, "c"));
            Assert.True(deferred.RetryAfter > TimeSpan.Zero);
            Assert.Empty(_endpoint.GetReceived("01021755"));
            _endpoint.Configure("01021755", failFirstAttempts: 0, rejectAlways: false);
            _time.Advance(TimeSpan.FromSeconds(11));
            await _handler.HandleAsync(message, "c");
            Assert.Single(_endpoint.GetReceived("01021755"));
        }

        [Fact]
        public async Task Open_circuit_of_one_client_does_not_affect_another()
        {
            await TripCircuitAsync("01021755");
            await _handler.HandleAsync(Msg(OrderStatus.Delivered, "02034411"), "c");
            Assert.Single(_endpoint.GetReceived("02034411"));
        }
    }
}
