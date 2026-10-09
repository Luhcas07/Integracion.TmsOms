using Integracion.TmsOms.Domain;

namespace Integracion.TmsOms.Application
{
    public sealed class ClientConfig
    {
        public string Format { get; set; } = "standard-v1";

        public string Destination { get; set; } = string.Empty;

        public List<OrderStatus> NotifyStatuses { get; set; } = [];
    }

    public interface IClientConfigProvider
    {
        ClientConfig? Get(string clientCode);
    }

    public sealed record OutboundNotification(string Destination, string ContentType, string Body, string NotificationId);

    public interface IClientEndpoint
    {
        Task SendAsync(string clientCode, OutboundNotification notification, CancellationToken cancellationToken = default);
    }
}
