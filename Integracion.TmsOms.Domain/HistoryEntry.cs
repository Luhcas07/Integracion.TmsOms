namespace Integracion.TmsOms.Domain
{
    public sealed record HistoryEntry(
        string OrderNumber,
        OrderStatus Status,
        string? SubStatus,
        DateTimeOffset EventDate,
        DateTimeOffset RecordedAt,
        ServiceType ServiceType,
        DispatchType DispatchType,
        string? VehicleCode,
        string? CourierName,
        string? ReceivedBy,
        string? Comments,
        EventOrigin Origin,
        ApplyOutcome Outcome,
        int VisitCountAfter,
        string IdempotencyKey
    );
}
