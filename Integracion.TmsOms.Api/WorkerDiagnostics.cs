using Integracion.TmsOms.Application;
using Integracion.TmsOms.Infrastructure;

namespace Integracion.TmsOms.Api
{
    public sealed record SimulationRequest(int FailFirstAttempts, bool RejectAlways);

    public static class WorkerDiagnostics
    {
        public static void MapWorkerDiagnosticsEndpoints(this WebApplication app)
        {
            var group = app.MapGroup("/diagnostics");
            group.MapGet("/orders/{orderNumber}/evidences", async (
                string orderNumber,
                IEvidenceCatalog catalog,
                CancellationToken cancellationToken) =>
                Results.Ok(await catalog.GetByOrderAsync(orderNumber, cancellationToken)));
            group.MapGet("/clients/{clientCode}/notifications", (string clientCode, InMemoryClientEndpoint endpoint) =>
                Results.Ok(endpoint.GetReceived(clientCode)));
            group.MapPost("/clients/{clientCode}/simulation", (
                string clientCode,
                SimulationRequest request,
                InMemoryClientEndpoint endpoint) =>
            {
                endpoint.Configure(clientCode, request.FailFirstAttempts, request.RejectAlways);
                return Results.Ok(request);
            });
            group.MapGet("/circuits", (CircuitBreakerRegistry breakers) =>
                Results.Ok(breakers.Snapshot().ToDictionary(b => b.Key, b => b.Value.ToString())));
            group.MapGet("/subscriptions/{name}/dead-letters", (string name, InMemoryTopic<StatusChangedMessage> topic) =>
            {
                if (!topic.Subscriptions.TryGetValue(name, out var queue))
                {
                    return Results.NotFound();
                }
                return Results.Ok(queue.DeadLetters.Select(d => new
                        {
                            d.MessageId,
                            d.PartitionKey,
                            d.CorrelationId,
                            d.Reason,
                            d.DeliveryCount,
                            d.DeadLetteredAt,
                            d.Payload.OrderNumber,
                            d.Payload.EventKey,
                            Status = d.Payload.Status.ToString()
                        }
                    )
                );
            });

            group.MapPost("/subscriptions/{name}/dead-letters/{messageId}/reprocess", (
                string name,
                string messageId,
                InMemoryTopic<StatusChangedMessage> topic) =>
            {
                if (!topic.Subscriptions.TryGetValue(name, out var queue))
                {
                    return Results.NotFound();
                }
                return queue.Reprocess(messageId) ? Results.Accepted() : Results.NotFound();
            });
        }
    }
}
