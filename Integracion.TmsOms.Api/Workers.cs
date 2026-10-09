using Integracion.TmsOms.Application;
using Integracion.TmsOms.Domain;

namespace Integracion.TmsOms.Api
{
    public abstract class PollingBackgroundService(ILogger logger, TimeSpan idleDelay) : BackgroundService
    {
        private readonly ILogger _logger = logger;

        private readonly TimeSpan _idleDelay = idleDelay;

        protected static TimeSpan PollInterval(IConfiguration config) =>
            TimeSpan.FromMilliseconds(config.GetValue("Workers:PollIntervalMilliseconds", 100));

        protected abstract Task<bool> RunOnceAsync(CancellationToken stoppingToken);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var didWork = false;
                try
                {
                    didWork = await RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error inesperado en el worker; se reintentará.");
                }
                if (didWork)
                    continue;
                try
                {
                    await Task.Delay(_idleDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public sealed class EventProcessorWorker(
        MessageConsumer<TrackingEvent> consumer,
        IConfiguration config,
        ILogger<EventProcessorWorker> logger) : PollingBackgroundService(logger, PollInterval(config))
    {
        private readonly MessageConsumer<TrackingEvent> _consumer = consumer;

        private readonly ILogger<EventProcessorWorker> _logger = logger;

        protected override async Task<bool> RunOnceAsync(CancellationToken stoppingToken)
        {
            var result = await _consumer.ProcessNextAsync(stoppingToken);
            switch (result.Outcome)
            {
                case ConsumeOutcome.NoMessage:
                    return false;
                case ConsumeOutcome.Completed:
                    _logger.LogInformation("Mensaje {MessageId} procesado.", result.MessageId);
                    break;
                case ConsumeOutcome.Abandoned:
                    _logger.LogWarning("Mensaje {MessageId} devuelto a la cola: {Detail}", result.MessageId, result.Detail);
                    break;
                case ConsumeOutcome.DeadLettered:
                    _logger.LogError("Mensaje {MessageId} enviado a la dead-letter queue: {Detail}", result.MessageId, result.Detail);
                    break;
            }
            return true;
        }
    }

    public sealed class OutboxDispatcherWorker(
        OutboxDispatcher dispatcher,
        IConfiguration config,
        ILogger<OutboxDispatcherWorker> logger) : PollingBackgroundService(logger, PollInterval(config))
    {
        private readonly OutboxDispatcher _dispatcher = dispatcher;

        private readonly ILogger<OutboxDispatcherWorker> _logger = logger;

        protected override async Task<bool> RunOnceAsync(CancellationToken stoppingToken)
        {
            var dispatched = await _dispatcher.DispatchPendingAsync(20, stoppingToken);
            if (dispatched > 0)
            {
                _logger.LogInformation("{Count} mensaje(s) del Outbox publicados.", dispatched);
            }
            return dispatched > 0;
        }
    }
}
