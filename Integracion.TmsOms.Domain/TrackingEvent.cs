namespace Integracion.TmsOms.Domain
{
    public sealed class TrackingEvent
    {
        public const string MaxVisitsSubStatus = "MAX VISITS REACHED";

        public TrackingEvent(
            string orderNumber,
            OrderStatus status,
            DateTimeOffset eventDate,
            string? subStatus = null,
            ServiceType serviceType = ServiceType.LastMile,
            DispatchType dispatchType = DispatchType.HomeDelivery,
            string? vehicleCode = null,
            string? courierName = null,
            string? receivedBy = null,
            string? comments = null,
            IReadOnlyList<EvidenceRef>? evidences = null,
            EventOrigin origin = EventOrigin.Tms)
        {
            if (string.IsNullOrWhiteSpace(orderNumber))
            {
                throw new ArgumentException("El número de pedido es obligatorio.", nameof(orderNumber));
            }
            if (!Enum.IsDefined(status))
            {
                throw new ArgumentOutOfRangeException(nameof(status), status, "Estado desconocido.");
            }
            if (eventDate == default)
            {
                throw new ArgumentException("La fecha del evento es obligatoria.", nameof(eventDate));
            }
            OrderNumber = orderNumber.Trim();
            Status = status;
            EventDate = eventDate;
            SubStatus = NormalizeOptional(subStatus);
            ServiceType = serviceType;
            DispatchType = dispatchType;
            VehicleCode = NormalizeOptional(vehicleCode);
            CourierName = NormalizeOptional(courierName);
            ReceivedBy = NormalizeOptional(receivedBy);
            Comments = NormalizeOptional(comments);
            Evidences = evidences ?? [];
            Origin = origin;
            IdempotencyKey = $"{OrderNumber}|{Status}|{SubStatus}|{EventDate.UtcDateTime:O}";
        }

        public string OrderNumber { get; }
        public OrderStatus Status { get; }
        public string? SubStatus { get; }
        public DateTimeOffset EventDate { get; }
        public ServiceType ServiceType { get; }
        public DispatchType DispatchType { get; }
        public string? VehicleCode { get; }
        public string? CourierName { get; }
        public string? ReceivedBy { get; }
        public string? Comments { get; }
        public IReadOnlyList<EvidenceRef> Evidences { get; }
        public EventOrigin Origin { get; }

        public string IdempotencyKey { get; }

        public static TrackingEvent CreateToBeReturn(TrackingEvent trigger)
        {
            ArgumentNullException.ThrowIfNull(trigger);
            return new TrackingEvent(
                orderNumber: trigger.OrderNumber,
                status: OrderStatus.ToBeReturn,
                eventDate: trigger.EventDate,
                subStatus: MaxVisitsSubStatus,
                serviceType: ServiceType.Return,
                dispatchType: DispatchType.Reverse,
                comments: "Generado automáticamente al alcanzar el máximo de visitas.",
                origin: EventOrigin.System
            );
        }

        private static string? NormalizeOptional(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

}
