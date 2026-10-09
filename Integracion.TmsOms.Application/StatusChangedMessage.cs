using Integracion.TmsOms.Domain;

namespace Integracion.TmsOms.Application
{
    public sealed record StatusChangedMessage(
        string OrderNumber,
        string TrackingNumber,
        string ClientCode,
        string ClientName,
        OrderStatus Status,
        string? SubStatus,
        DateTimeOffset EventDate,
        ServiceType ServiceType,
        DispatchType DispatchType,
        string? VehicleCode,
        string? CourierName,
        string? ReceivedBy,
        string? Comments,
        IReadOnlyList<EvidenceRef> Evidences,
        EventOrigin Origin,
        int VisitCount,
        string EventKey)
    {
        public static StatusChangedMessage From(Order order, TrackingEvent trackingEvent) =>
            new(
                order.OrderNumber,
                order.TrackingNumber,
                order.ClientCode,
                order.ClientName,
                trackingEvent.Status,
                trackingEvent.SubStatus,
                trackingEvent.EventDate,
                trackingEvent.ServiceType,
                trackingEvent.DispatchType,
                trackingEvent.VehicleCode,
                trackingEvent.CourierName,
                trackingEvent.ReceivedBy,
                trackingEvent.Comments,
                trackingEvent.Evidences,
                trackingEvent.Origin,
                order.VisitCount,
                trackingEvent.IdempotencyKey
            );
    }
}
