using Integracion.TmsOms.Api;
using Integracion.TmsOms.Application;
using Integracion.TmsOms.Domain;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Integracion.TmsOms.Tests
{
    public sealed class WebhookIntegrationTests : IDisposable
    {
        private const string Order06 = "2500000006-01";

        private const string Order07 = "2500000007-01";

        private const string Order08 = "2500000008-01";

        private const string ClientStandard = "01021755";

        private const string ClientLegacy = "02034411";

        private readonly ApiFactory _factory = new();

        private readonly HttpClient _client;

        public WebhookIntegrationTests()
        {
            _client = _factory.CreateTmsClient();
        }

        public void Dispose()
        {
            _client.Dispose();
            _factory.Dispose();
        }

        private static object Payload(
            string orderNumber,
            string clientCode,
            string status,
            string eventDate,
            string subStatus = "CLIENT RECEIVED",
            object[]? evidences = null) =>
            new
            {
                serviceType = "LAST_MILE",
                dispatchType = "HOME_DELIVERY",
                status,
                subStatus,
                courierName = "Conductor 46",
                details = new
                {
                    orderNumber,
                    trackingNumber = "OE" + orderNumber,
                    clientCode,
                    clientName = "CLIENTE",
                    receivedBy = "Pepito Perez",
                    evidences = evidences ?? []
                },
                eventDate
            };

        private static object EvidencePayload(string fileName) =>
            new
            {
                label = "Paquete",
                fileType = ".jpg",
                fileName,
                url = $"htps://beetrack.example/img/{fileName}"
            };

        private static Task<HttpResponseMessage> PostEventAsync(HttpClient client, object payload, string? correlationId = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/tms")
            {
                Content = JsonContent.Create(payload)
            };
            if (correlationId is not null)
            {
                request.Headers.Add("X-Correlation-Id", correlationId);
            }
            return client.SendAsync(request);
        }

        private async Task SimulateClientAsync(string clientCode, int failFirstAttempts, bool rejectAlways)
        {
            var response = await _client.PostAsJsonAsync(
                $"/diagnostics/clients/{clientCode}/simulation",
                new { failFirstAttempts, rejectAlways });
            response.EnsureSuccessStatusCode();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("clave-incorrecta")]
        public async Task Webhook_rejects_requests_without_a_valid_api_key(string? apiKey)
        {
            using var client = _factory.CreateTmsClient(apiKey);
            var response = await PostEventAsync(client, Payload(Order06, ClientStandard, "DELIVERED", "2025-04-15 16:53:07"));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(0, _factory.EventQueue.PendingCount);
        }

        [Fact]
        public async Task Invalid_payload_returns_400_with_every_problem_and_nothing_is_queued()
        {
            var payload = new
            {
                serviceType = "LAST_MILE",
                dispatchType = "HOME_DELIVERY",
                status = "VOLANDO",
                details = new { },
                eventDate = "15/04/2025"
            };
            var response = await PostEventAsync(_client, payload);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(3, body.GetProperty("errors").GetProperty("payload").GetArrayLength());
            Assert.Equal(0, _factory.EventQueue.PendingCount);
        }

        [Fact]
        public async Task Delivered_event_flows_end_to_end()
        {
            var payload = Payload(
                Order06, ClientStandard, "DELIVERED", "2025-04-15 16:53:07",
                evidences: [EvidencePayload("front.jpg"), EvidencePayload("door.jpg")]);
            var response = await PostEventAsync(_client, payload, correlationId: "corr-e2e-1");
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var accepted = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("corr-e2e-1", accepted.GetProperty("correlationId").GetString());
            await _factory.WaitForIdleAsync();
            var order = _factory.Store.Peek(Order06)!;
            Assert.Equal(OrderStatus.Delivered, order.Status);
            Assert.Equal(1, order.VisitCount);
            Assert.True(order.IsClosed);
            Assert.Equal(2, (await _factory.Evidence.GetByOrderAsync(Order06)).Count);
            Assert.Equal(2, _factory.Storage.Count);
            var notification = Assert.Single(_factory.Clients.GetReceived(ClientStandard));
            Assert.Contains("\"status\":\"DELIVERED\"", notification.Notification.Body);
            var diagnostics = await _client.GetFromJsonAsync<JsonElement>($"/diagnostics/orders/{Order06}");
            Assert.Equal("Delivered", diagnostics.GetProperty("status").GetString());
            Assert.Equal(1, diagnostics.GetProperty("history").GetArrayLength());
        }

        [Fact]
        public async Task Duplicate_webhook_is_accepted_but_counted_only_once()
        {
            var payload = Payload(Order07, ClientStandard, "NOT DELIVERED", "2025-04-15 16:00:00", "CLIENT ABSENT");
            var first = await PostEventAsync(_client, payload);
            var second = await PostEventAsync(_client, payload);
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
            await _factory.WaitForIdleAsync();
            Assert.Equal(1, _factory.Store.Peek(Order07)!.VisitCount);
            Assert.Single(_factory.Store.GetHistory(Order07));
            Assert.Single(_factory.Clients.GetReceived(ClientStandard));
        }

        [Fact]
        public async Task Three_failed_visits_send_the_order_to_return_through_the_whole_pipeline()
        {
            foreach (var eventDate in new[] { "2025-04-15 16:00:00", "2025-04-15 17:00:00", "2025-04-15 18:00:00" })
            {
                var response = await PostEventAsync(
                    _client, Payload(Order07, ClientStandard, "NOT DELIVERED", eventDate, "CLIENT ABSENT"));
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            }
            await _factory.WaitForIdleAsync();
            var order = _factory.Store.Peek(Order07)!;
            Assert.Equal(OrderStatus.ToBeReturn, order.Status);
            Assert.Equal(3, order.VisitCount);
            var history = _factory.Store.GetHistory(Order07);
            Assert.Equal(4, history.Count);
            Assert.Equal(OrderStatus.ToBeReturn, history[3].Status);
            Assert.Equal(EventOrigin.System, history[3].Origin);
            Assert.Equal(4, _factory.Clients.GetReceived(ClientStandard).Count);
            Assert.Empty(_factory.EventQueue.DeadLetters);
        }

        [Fact]
        public async Task Event_for_a_closed_order_is_recorded_but_does_not_change_it_or_notify()
        {
            await PostEventAsync(_client, Payload(Order06, ClientStandard, "DELIVERED", "2025-04-15 16:00:00"));
            await _factory.WaitForIdleAsync();
            await PostEventAsync(_client, Payload(Order06, ClientStandard, "NOT DELIVERED", "2025-04-15 17:00:00"));
            await _factory.WaitForIdleAsync();
            var order = _factory.Store.Peek(Order06)!;
            Assert.Equal(OrderStatus.Delivered, order.Status);
            Assert.Equal(1, order.VisitCount);
            var history = _factory.Store.GetHistory(Order06);
            Assert.Equal(2, history.Count);
            Assert.Equal(ApplyOutcome.IgnoredFinalState, history[1].Outcome);
            Assert.Single(_factory.Clients.GetReceived(ClientStandard));
        }

        [Fact]
        public async Task Unknown_order_goes_to_the_dead_letter_queue_and_is_recovered_by_reprocessing()
        {
            const string orderNumber = "2500000099-01";
            await PostEventAsync(_client, Payload(orderNumber, ClientStandard, "STARTED", "2025-04-15 09:00:00", "ON ROUTE"));
            await _factory.WaitForIdleAsync();
            var dead = Assert.Single(_factory.EventQueue.DeadLetters);
            Assert.Equal(5, dead.DeliveryCount);
            var listed = await _client.GetFromJsonAsync<JsonElement>("/diagnostics/dead-letters");
            Assert.Equal(1, listed.GetArrayLength());
            _factory.Store.Seed(new Order(orderNumber, "OE" + orderNumber, ClientStandard, "TIENDAS PERUANAS S.A."));
            var reprocess = await _client.PostAsync($"/diagnostics/dead-letters/{dead.MessageId}/reprocess", null);
            Assert.Equal(HttpStatusCode.Accepted, reprocess.StatusCode);
            await _factory.WaitForIdleAsync();
            Assert.Empty(_factory.EventQueue.DeadLetters);
            Assert.Equal(OrderStatus.Started, _factory.Store.Peek(orderNumber)!.Status);
            Assert.Single(_factory.Clients.GetReceived(ClientStandard));
        }

        [Fact]
        public async Task Evidence_that_fails_temporarily_is_retried_and_saved_without_blocking_the_notification()
        {
            var payload = Payload(
                Order08, ClientLegacy, "NOT DELIVERED", "2025-04-15 10:00:00", "CLIENT ABSENT",
                evidences: [EvidencePayload("fachada-FAIL-TRANSIENT-4.jpg")]);
            await PostEventAsync(_client, payload);
            await _factory.WaitForIdleAsync();
            var saved = Assert.Single(await _factory.Evidence.GetByOrderAsync(Order08));
            Assert.Equal("fachada-FAIL-TRANSIENT-4.jpg", saved.FileName);
            Assert.Equal(1, _factory.Storage.Count);
            Assert.Empty(_factory.Topic.Subscriptions[Subscriptions.Evidences].DeadLetters);
            Assert.Single(_factory.Clients.GetReceived(ClientLegacy));
        }

        [Fact]
        public async Task Client_that_rejects_notifications_fills_the_dead_letter_queue_and_recovers_after_reprocessing()
        {
            await SimulateClientAsync(ClientStandard, failFirstAttempts: 0, rejectAlways: true);
            await PostEventAsync(_client, Payload(Order07, ClientStandard, "STARTED", "2025-04-15 09:00:00", "ON ROUTE"));
            await _factory.WaitForIdleAsync();
            var deadLetters = _factory.Topic.Subscriptions[Subscriptions.Notifications].DeadLetters;
            var dead = Assert.Single(deadLetters);
            Assert.Equal(1, dead.DeliveryCount);
            Assert.Empty(_factory.Clients.GetReceived(ClientStandard));
            Assert.Equal(CircuitBreakerState.Closed, _factory.Circuits.Snapshot()[ClientStandard]);
            await SimulateClientAsync(ClientStandard, failFirstAttempts: 0, rejectAlways: false);
            var reprocess = await _client.PostAsync(
                $"/diagnostics/subscriptions/{Subscriptions.Notifications}/dead-letters/{dead.MessageId}/reprocess", null);
            Assert.Equal(HttpStatusCode.Accepted, reprocess.StatusCode);
            await _factory.WaitForIdleAsync();
            Assert.Empty(_factory.Topic.Subscriptions[Subscriptions.Notifications].DeadLetters);
            Assert.Single(_factory.Clients.GetReceived(ClientStandard));
        }

        [Fact]
        public async Task Failing_client_opens_the_circuit_and_the_notification_is_delivered_after_it_recovers()
        {
            await SimulateClientAsync(ClientStandard, failFirstAttempts: 100, rejectAlways: false);
            await PostEventAsync(_client, Payload(Order07, ClientStandard, "STARTED", "2025-04-15 09:00:00", "ON ROUTE"));
            await _factory.WaitUntilAsync(
                () => _factory.Circuits.Snapshot().TryGetValue(ClientStandard, out var state)
                      && state == CircuitBreakerState.Open,
                "que el circuito del cliente se abra");
            Assert.Empty(_factory.Clients.GetReceived(ClientStandard));
            Assert.Empty(_factory.Topic.Subscriptions[Subscriptions.Notifications].DeadLetters);
            await SimulateClientAsync(ClientStandard, failFirstAttempts: 0, rejectAlways: false);
            await _factory.WaitForIdleAsync(TimeSpan.FromSeconds(15));
            Assert.Single(_factory.Clients.GetReceived(ClientStandard));
            Assert.Equal(CircuitBreakerState.Closed, _factory.Circuits.Snapshot()[ClientStandard]);
            Assert.Empty(_factory.Topic.Subscriptions[Subscriptions.Notifications].DeadLetters);
        }

        [Fact]
        public async Task Diagnostics_returns_404_for_an_unknown_order()
        {
            var response = await _client.GetAsync("/diagnostics/orders/NO-EXISTE");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }
}
