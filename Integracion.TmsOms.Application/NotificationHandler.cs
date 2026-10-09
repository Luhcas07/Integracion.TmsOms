namespace Integracion.TmsOms.Application
{
    public sealed class NotificationHandler(
        IClientConfigProvider configs,
        NotificationAdapterRegistry adapters,
        IClientEndpoint endpoint,
        IIdempotencyStore idempotency,
        CircuitBreakerRegistry breakers,
        IRetryPolicy retry)
    {
        public const string ConsumerName = "notification-worker";

        private readonly IClientConfigProvider _configs = configs ?? throw new ArgumentNullException(nameof(configs));

        private readonly NotificationAdapterRegistry _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));

        private readonly IClientEndpoint _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

        private readonly IIdempotencyStore _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));

        private readonly CircuitBreakerRegistry _breakers = breakers ?? throw new ArgumentNullException(nameof(breakers));

        private readonly IRetryPolicy _retry = retry ?? throw new ArgumentNullException(nameof(retry));

        public async Task HandleAsync(
            StatusChangedMessage message,
            string correlationId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentException.ThrowIfNullOrEmpty(correlationId);
            if (await _idempotency.HasProcessedAsync(ConsumerName, message.EventKey, cancellationToken))
            {
                return;
            }
            var config = _configs.Get(message.ClientCode)
                ?? throw new PermanentFailureException(
                    $"El cliente '{message.ClientCode}' no tiene configuración de notificación.");
            if (config.NotifyStatuses.Count > 0 && !config.NotifyStatuses.Contains(message.Status))
            {
                return;
            }
            var notification = _adapters.Resolve(config.Format).Adapt(message, config);
            var breaker = _breakers.For(message.ClientCode);
            await _retry.ExecuteAsync(
                token => breaker.ExecuteAsync(t => _endpoint.SendAsync(message.ClientCode, notification, t), token),
                cancellationToken
            );
            await _idempotency.MarkProcessedAsync(ConsumerName, message.EventKey, cancellationToken);
        }
    }
}
