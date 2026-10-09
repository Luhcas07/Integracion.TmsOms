using Integracion.TmsOms.Domain;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Integracion.TmsOms.Application
{
    public static class StatusNames
    {
        public static string ToTmsName(OrderStatus status) => status switch
        {
            OrderStatus.Planning => "PLANNING",
            OrderStatus.Started => "STARTED",
            OrderStatus.AtPickupPoint => "AT PICKUP POINT",
            OrderStatus.Collected => "COLLECTED",
            OrderStatus.NotCollected => "NOT COLLECTED",
            OrderStatus.Delivered => "DELIVERED",
            OrderStatus.NotDelivered => "NOT DELIVERED",
            OrderStatus.ToBeReturn => "TO BE RETURN",
            OrderStatus.Returned => "RETURNED",
            OrderStatus.NotReturned => "NOT RETURNED",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
        };
    }

    public interface INotificationAdapter
    {
        string Format { get; }

        OutboundNotification Adapt(StatusChangedMessage message, ClientConfig config);
    }

    internal static class NotificationJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
    }

    public sealed class StandardNotificationAdapter : INotificationAdapter
    {
        public string Format => "standard-v1";

        public OutboundNotification Adapt(StatusChangedMessage message, ClientConfig config)
        {
            var body = new
            {
                notificationId = message.EventKey,
                orderNumber = message.OrderNumber,
                trackingNumber = message.TrackingNumber,
                status = StatusNames.ToTmsName(message.Status),
                subStatus = message.SubStatus,
                eventDate = message.EventDate.ToString("o", CultureInfo.InvariantCulture),
                visitCount = message.VisitCount,
                receivedBy = message.ReceivedBy,
                comments = message.Comments
            };
            return new OutboundNotification(
                config.Destination,
                "application/json",
                JsonSerializer.Serialize(body, NotificationJson.Options),
                message.EventKey
            );
        }
    }

    public sealed class LegacyNotificationAdapter : INotificationAdapter
    {
        public string Format => "legacy-v1";

        public OutboundNotification Adapt(StatusChangedMessage message, ClientConfig config)
        {
            var body = new
            {
                id = message.EventKey,
                pedido = message.OrderNumber,
                guia = message.TrackingNumber,
                estado = StatusNames.ToTmsName(message.Status),
                detalle = message.SubStatus,
                fecha = message.EventDate.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture),
                intentos = message.VisitCount,
                recibidoPor = message.ReceivedBy
            };
            return new OutboundNotification(
                config.Destination,
                "application/json",
                JsonSerializer.Serialize(body, NotificationJson.Options),
                message.EventKey
            );
        }
    }

    public sealed class NotificationAdapterRegistry
    {
        private readonly Dictionary<string, INotificationAdapter> _adapters;

        public NotificationAdapterRegistry(IEnumerable<INotificationAdapter> adapters)
        {
            ArgumentNullException.ThrowIfNull(adapters);
            _adapters = adapters.ToDictionary(a => a.Format, StringComparer.OrdinalIgnoreCase);
        }

        public INotificationAdapter Resolve(string format) =>
            _adapters.TryGetValue(format, out var adapter)
                ? adapter
                : throw new PermanentFailureException($"No hay un adaptador para el formato '{format}'.");
    }
}
