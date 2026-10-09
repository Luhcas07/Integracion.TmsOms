using Integracion.TmsOms.Application;
using Integracion.TmsOms.Infrastructure;

namespace Integracion.TmsOms.Api
{
    public abstract class ConsumerWorker<T>(string name, MessageConsumer<T> consumer, ILogger logger, TimeSpan idleDelay) : PollingBackgroundService(logger, idleDelay)
    {
        private readonly string _name = name;

        private readonly MessageConsumer<T> _consumer = consumer;

        private readonly ILogger _logger = logger;

        protected override async Task<bool> RunOnceAsync(CancellationToken stoppingToken)
        {
            var result = await _consumer.ProcessNextAsync(stoppingToken);
            switch (result.Outcome)
            {
                case ConsumeOutcome.NoMessage:
                    return false;
                case ConsumeOutcome.Completed:
                    _logger.LogInformation("[{Worker}] Mensaje {MessageId} procesado.", _name, result.MessageId);
                    break;
                case ConsumeOutcome.Abandoned:
                    _logger.LogWarning("[{Worker}] Mensaje {MessageId} devuelto a la cola: {Detail}", _name, result.MessageId, result.Detail);
                    break;
                case ConsumeOutcome.Deferred:
                    _logger.LogInformation("[{Worker}] Mensaje {MessageId} pospuesto: {Detail}", _name, result.MessageId, result.Detail);
                    break;
                case ConsumeOutcome.DeadLettered:
                    _logger.LogError("[{Worker}] Mensaje {MessageId} enviado a la dead-letter queue: {Detail}", _name, result.MessageId, result.Detail);
                    break;
            }
            return true;
        }
    }

    public sealed class EvidenceWorker(
        InMemoryTopic<StatusChangedMessage> topic,
        EvidenceHandler handler,
        IConfiguration config,
        ILogger<EvidenceWorker> logger) : ConsumerWorker<StatusChangedMessage>(
            "evidencias",
            new MessageConsumer<StatusChangedMessage>(topic.Subscriptions[Subscriptions.Evidences], handler.HandleAsync),
            logger,
            PollInterval(config))
    {
    }

    public sealed class NotificationWorker(
        InMemoryTopic<StatusChangedMessage> topic,
        NotificationHandler handler,
        IConfiguration config,
        ILogger<NotificationWorker> logger) : ConsumerWorker<StatusChangedMessage>(
            "notificaciones",
            new MessageConsumer<StatusChangedMessage>(topic.Subscriptions[Subscriptions.Notifications], handler.HandleAsync),
            logger,
            PollInterval(config)
        )
    {
    }
}
