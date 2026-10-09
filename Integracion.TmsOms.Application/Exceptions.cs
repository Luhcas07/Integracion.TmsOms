namespace Integracion.TmsOms.Application
{
    /// <summary>Fallo temporal: vale la pena reintentar (red, servicio caído, conflicto de concurrencia).</summary>
    public class TransientException(string message, Exception? innerException = null) : Exception(message, innerException)
    {
    }

    /// <summary>Fallo permanente: reintentar no lo arregla. El mensaje debe ir a la dead-letter queue.</summary>
    public class PermanentFailureException(string message, Exception? innerException = null) : Exception(message, innerException)
    {
    }

    /// <summary>Otro proceso modificó el pedido entre la lectura y el guardado.</summary>
    public sealed class ConcurrencyException(string orderNumber) : TransientException($"El pedido '{orderNumber}' fue modificado por otro proceso.")
    {
    }

    /// <summary>
    /// El pedido no existe en el OMS. Se trata como temporal porque puede ser un desfase de sincronización;
    /// si persiste, el mensaje termina en la dead-letter queue al agotar las entregas.
    /// </summary>
    public sealed class OrderNotFoundException(string orderNumber) : TransientException($"El pedido '{orderNumber}' no existe en el OMS.")
    {
        public string OrderNumber { get; } = orderNumber;
    }

    /// <summary>
    /// El mensaje no puede procesarse todavía, pero no es un fallo: debe volver a la cola más tarde
    /// sin contar como un intento de entrega (por ejemplo, un circuito abierto).
    /// </summary>
    public class DeferMessageException(TimeSpan retryAfter, string message) : Exception(message)
    {
        public TimeSpan RetryAfter { get; } = retryAfter;
    }

    /// <summary>El circuit breaker del destino está abierto: no se intenta la llamada.</summary>
    public sealed class CircuitOpenException(TimeSpan retryAfter) : DeferMessageException(retryAfter, $"El circuito está abierto; se reintentará en {retryAfter.TotalSeconds:0.#} s.")
    {
    }
}
