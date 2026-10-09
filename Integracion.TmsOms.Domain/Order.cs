namespace Integracion.TmsOms.Domain
{
    public sealed class Order(string orderNumber, string trackingNumber, string clientCode, string clientName)
    {
        public const int MaxVisitsBeforeReturn = 3;

        public string OrderNumber { get; } = Required(orderNumber, nameof(orderNumber));

        public string TrackingNumber { get; } = Required(trackingNumber, nameof(trackingNumber));

        public string ClientCode { get; } = Required(clientCode, nameof(clientCode));

        public string ClientName { get; } = Required(clientName, nameof(clientName));

        public OrderStatus? Status { get; private set; }

        public string? SubStatus { get; private set; }

        public int VisitCount { get; private set; }

        public DateTimeOffset? LastEventDate { get; private set; }

        public int Version { get; private set; }

        public bool IsClosed => Status?.IsFinal() == true;

        public Order Clone() =>
            Restore(OrderNumber, TrackingNumber, ClientCode, ClientName,
                    Status, SubStatus, VisitCount, LastEventDate, Version);

        public ApplyResult Apply(TrackingEvent trackingEvent, DateTimeOffset recordedAt)
        {
            ArgumentNullException.ThrowIfNull(trackingEvent);
            if (!string.Equals(trackingEvent.OrderNumber, OrderNumber, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"El evento es del pedido '{trackingEvent.OrderNumber}' y no corresponde al pedido '{OrderNumber}'.");
            }
            if (IsClosed)
            {
                return new ApplyResult(
                    ApplyOutcome.IgnoredFinalState,
                    ToHistory(trackingEvent, recordedAt, ApplyOutcome.IgnoredFinalState),
                    FollowUpEvent: null);
            }
            Status = trackingEvent.Status;
            SubStatus = trackingEvent.SubStatus;
            LastEventDate = trackingEvent.EventDate;
            if (trackingEvent.Status.CountsAsVisit())
            {
                VisitCount++;
            }
            Version++;
            var followUp = ShouldRequestReturn(trackingEvent)
                ? TrackingEvent.CreateToBeReturn(trackingEvent)
                : null;
            return new ApplyResult(
                ApplyOutcome.Applied,
                ToHistory(trackingEvent, recordedAt, ApplyOutcome.Applied),
                followUp
            );
        }

        private bool ShouldRequestReturn(TrackingEvent applied) =>
            applied.Status == OrderStatus.NotDelivered && VisitCount == MaxVisitsBeforeReturn;

        private HistoryEntry ToHistory(TrackingEvent e, DateTimeOffset recordedAt, ApplyOutcome outcome) =>
            new(
                OrderNumber,
                e.Status,
                e.SubStatus,
                e.EventDate,
                recordedAt,
                e.ServiceType,
                e.DispatchType,
                e.VehicleCode,
                e.CourierName,
                e.ReceivedBy,
                e.Comments,
                e.Origin,
                outcome,
                VisitCount,
                e.IdempotencyKey
            );

        private static string Required(string? value, string paramName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("El valor es obligatorio.", paramName);
            }
            return value.Trim();
        }

        public static Order Restore(
            string orderNumber,
            string trackingNumber,
            string clientCode,
            string clientName,
            OrderStatus? status,
            string? subStatus,
            int visitCount,
            DateTimeOffset? lastEventDate,
            int version)
        {
            var order = new Order(orderNumber, trackingNumber, clientCode, clientName)
            {
                Status = status,
                SubStatus = subStatus,
                VisitCount = visitCount,
                LastEventDate = lastEventDate,
                Version = version
            };
            return order;
        }
    }
}
