using Integracion.TmsOms.Application;
using Integracion.TmsOms.Domain;
using Integracion.TmsOms.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Integracion.TmsOms.Tests
{
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string ApiKey = "test-key";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Tms:ApiKey"] = ApiKey,
                    ["Workers:PollIntervalMilliseconds"] = "10",
                    ["Queue:MaxDeliveryCount"] = "5",
                    ["Queue:LockDurationSeconds"] = "30",
                    ["Queue:RedeliveryDelayMilliseconds"] = "50",
                    ["Retry:MaxAttempts"] = "3",
                    ["Retry:InitialDelayMilliseconds"] = "1",
                    ["CircuitBreaker:FailureThreshold"] = "3",
                    ["CircuitBreaker:OpenDurationSeconds"] = "1"
                });
            });
        }

        public InMemoryOmsStore Store => Services.GetRequiredService<InMemoryOmsStore>();

        public InMemoryMessageQueue<TrackingEvent> EventQueue => Services.GetRequiredService<InMemoryMessageQueue<TrackingEvent>>();

        public InMemoryTopic<StatusChangedMessage> Topic => Services.GetRequiredService<InMemoryTopic<StatusChangedMessage>>();

        public InMemoryClientEndpoint Clients => Services.GetRequiredService<InMemoryClientEndpoint>();

        public InMemoryEvidenceStorage Storage => Services.GetRequiredService<InMemoryEvidenceStorage>();

        public IEvidenceCatalog Evidence => Services.GetRequiredService<IEvidenceCatalog>();

        public CircuitBreakerRegistry Circuits => Services.GetRequiredService<CircuitBreakerRegistry>();

        public HttpClient CreateTmsClient(string? apiKey = ApiKey)
        {
            var client = CreateClient();
            if (apiKey is not null)
                client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
            return client;
        }

        public async Task WaitUntilAsync(Func<bool> condition, string description, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));

            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"Se agotó el tiempo esperando: {description}.");
                }
                await Task.Delay(20);
            }
        }

        public async Task WaitForIdleAsync(TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
            while (true)
            {
                var queue = EventQueue.PendingCount;
                var outbox = Store.PendingOutboxCount;
                var subscriptions = Topic.Subscriptions.Values.Sum(q => q.PendingCount);
                if (queue == 0 && outbox == 0 && subscriptions == 0)
                {
                    return;
                }
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException(
                        $"El sistema no quedó en reposo: cola={queue}, outbox={outbox}, suscripciones={subscriptions}.");
                }
                await Task.Delay(20);
            }
        }
    }
}
