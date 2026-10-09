namespace Integracion.TmsOms.Domain
{
    public enum OrderStatus
    {
        Planning,
        Started,
        AtPickupPoint,
        Collected,
        NotCollected,
        Delivered,
        NotDelivered,
        ToBeReturn,
        Returned,
        NotReturned
    }

    public static class OrderStatusExtensions
    {
        public static bool IsFinal(this OrderStatus status) =>
            status is OrderStatus.Delivered or OrderStatus.Returned;

        public static bool CountsAsVisit(this OrderStatus status) =>
            status is OrderStatus.Delivered or OrderStatus.NotDelivered;

        public static bool RequiresEvidence(this OrderStatus status) =>
            status is OrderStatus.Collected or OrderStatus.NotCollected
                   or OrderStatus.Delivered or OrderStatus.NotDelivered
                   or OrderStatus.Returned or OrderStatus.NotReturned;
    }
}
