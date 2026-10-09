using Integracion.TmsOms.Application;
using Integracion.TmsOms.Domain;
using Integracion.TmsOms.Infrastructure;
using System.Security.Cryptography;
using System.Text;

namespace Integracion.TmsOms.Api
{
    public static class Endpoints
    {
        public static void MapWebhookEndpoints(this WebApplication app)
        {
            app.MapPost("/webhooks/tms", async (
                HttpContext http,
                TmsWebhookPayload? payload,
                TmsEventTranslator translator,
                IMessageSender<TrackingEvent> queue,
                IConfiguration config,
                CancellationToken cancellationToken) =>
            {
                if (!IsAuthorized(http.Request, config["Tms:ApiKey"]))
                {
                    return Results.Unauthorized();
                }
                var translation = translator.Translate(payload);
                if (translation.Event is null)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["payload"] = [.. translation.Errors]
                    });
                }
                var trackingEvent = translation.Event;
                var correlationId = http.Request.Headers["X-Correlation-Id"].FirstOrDefault()
                                    ?? Guid.NewGuid().ToString("N");
                await queue.SendAsync(trackingEvent, trackingEvent.OrderNumber, correlationId, cancellationToken);
                return Results.Accepted(value: new { correlationId, eventKey = trackingEvent.IdempotencyKey });
            });
        }

        public static void MapDiagnosticsEndpoints(this WebApplication app)
        {
            var group = app.MapGroup("/diagnostics");
            group.MapGet("/orders/{orderNumber}", (string orderNumber, InMemoryOmsStore store) =>
            {
                var order = store.Peek(orderNumber);
                if (order is null)
                {
                    return Results.NotFound();
                }
                return Results.Ok(new
                    {
                        order.OrderNumber,
                        order.TrackingNumber,
                        order.ClientCode,
                        order.ClientName,
                        Status = order.Status?.ToString(),
                        order.SubStatus,
                        order.VisitCount,
                        order.Version,
                        order.IsClosed,
                        order.LastEventDate,
                        History = store.GetHistory(orderNumber)
                    }
                );
            });

            group.MapGet("/queue", (
                InMemoryMessageQueue<TrackingEvent> queue,
                InMemoryTopic<StatusChangedMessage> topic,
                InMemoryOmsStore store) =>
                Results.Ok(new
                {
                    QueuePending = queue.PendingCount,
                    QueueDeadLetters = queue.DeadLetters.Count,
                    OutboxPending = store.PendingOutboxCount,
                    Topic = topic.Subscriptions.ToDictionary(
                        s => s.Key,
                        s => new { Pending = s.Value.PendingCount, DeadLetters = s.Value.DeadLetters.Count }
                    )
                }));

            group.MapGet("/dead-letters", (InMemoryMessageQueue<TrackingEvent> queue) =>
                Results.Ok(queue.DeadLetters.Select(d => new
                {
                    d.MessageId,
                    d.PartitionKey,
                    d.CorrelationId,
                    d.Reason,
                    d.DeliveryCount,
                    d.DeadLetteredAt,
                    EventKey = d.Payload.IdempotencyKey,
                    Status = d.Payload.Status.ToString()
                })));

            group.MapPost("/dead-letters/{messageId}/reprocess", (string messageId, InMemoryMessageQueue<TrackingEvent> queue) =>
                queue.Reprocess(messageId) ? Results.Accepted() : Results.NotFound());
        }

        private static bool IsAuthorized(HttpRequest request, string? expectedKey)
        {
            if (string.IsNullOrEmpty(expectedKey))
            {
                return false;
            }
            var provided = request.Headers["X-Api-Key"].FirstOrDefault();
            if (string.IsNullOrEmpty(provided))
            {
                return false;
            }
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(provided),
                Encoding.UTF8.GetBytes(expectedKey)
            );
        }
    }
}
