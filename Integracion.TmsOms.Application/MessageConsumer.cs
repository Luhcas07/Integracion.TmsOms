namespace Integracion.TmsOms.Application
{
    public enum ConsumeOutcome
    {
        NoMessage,
        Completed,
        Abandoned,
        Deferred,
        DeadLettered
    }

    public sealed record ConsumeResult(ConsumeOutcome Outcome, string? MessageId = null, string? Detail = null);

    public sealed class MessageConsumer<T>(IMessageReceiver<T> receiver, Func<T, string, CancellationToken, Task> handler)
    {
        private readonly IMessageReceiver<T> _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));

        private readonly Func<T, string, CancellationToken, Task> _handler = handler ?? throw new ArgumentNullException(nameof(handler));

        public async Task<ConsumeResult> ProcessNextAsync(CancellationToken cancellationToken = default)
        {
            var message = await _receiver.ReceiveAsync(cancellationToken);
            if (message is null)
            {
                return new ConsumeResult(ConsumeOutcome.NoMessage);
            }
            try
            {
                await _handler(message.Payload, message.CorrelationId, cancellationToken);
                await message.CompleteAsync(CancellationToken.None);
                return new ConsumeResult(ConsumeOutcome.Completed, message.MessageId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (PermanentFailureException ex)
            {
                await message.DeadLetterAsync(ex.Message, CancellationToken.None);
                return new ConsumeResult(ConsumeOutcome.DeadLettered, message.MessageId, ex.Message);
            }
            catch (DeferMessageException ex)
            {
                await message.DeferAsync(ex.RetryAfter, CancellationToken.None);
                return new ConsumeResult(ConsumeOutcome.Deferred, message.MessageId, ex.Message);
            }
            catch (Exception ex)
            {
                await message.AbandonAsync(CancellationToken.None);
                return new ConsumeResult(ConsumeOutcome.Abandoned, message.MessageId, ex.Message);
            }
        }
    }
}
