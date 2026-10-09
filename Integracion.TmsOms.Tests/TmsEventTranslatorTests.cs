using Integracion.TmsOms.Application;
using Integracion.TmsOms.Domain;

namespace Integracion.TmsOms.Tests
{
    public class TmsEventTranslatorTests
    {
        private readonly TmsEventTranslator _translator = new();

        private static TmsWebhookPayload Payload(
            string? status = "DELIVERED",
            string? orderNumber = "2500000006-01",
            string? eventDate = "2025-04-15 16:53:07") =>
            new()
            {
                ServiceType = "LAST_MILE",
                DispatchType = "HOME_DELIVERY",
                Status = status,
                SubStatus = "CLIENT RECEIVED",
                VehicleCode = "LIMURB06VAN",
                CourierName = "Conductor 46",
                EventDate = eventDate,
                Details = new TmsOrderDetails
                {
                    OrderNumber = orderNumber,
                    TrackingNumber = "OE2500000006-01",
                    ClientCode = "01021755",
                    ClientName = "TIENDAS PERUANAS S.A.",
                    ReceivedBy = "Pepito Perez",
                    Comments = "Entregado en domicilio",
                    Evidences =
                    [
                    new() { Label = "Paquete", FileType = ".jpg", FileName = "delivered_201054.jpg", Url = "htps://beetrack.com/img/delivered_201054.jpg" },
                    new() { Label = "Fachada", FileType = ".jpg", FileName = "delivered_201055.jpg", Url = "htps://beetrack.com/img/delivered_201055.jpg" }
                    ]
                }
            };

        [Fact]
        public void Valid_payload_is_translated_to_the_internal_model()
        {
            var result = _translator.Translate(Payload());
            Assert.True(result.IsValid);
            var e = result.Event!;
            Assert.Equal("2500000006-01", e.OrderNumber);
            Assert.Equal(OrderStatus.Delivered, e.Status);
            Assert.Equal(ServiceType.LastMile, e.ServiceType);
            Assert.Equal(DispatchType.HomeDelivery, e.DispatchType);
            Assert.Equal("CLIENT RECEIVED", e.SubStatus);
            Assert.Equal("Pepito Perez", e.ReceivedBy);
            Assert.Equal(2, e.Evidences.Count);
            Assert.Equal(new DateTimeOffset(2025, 4, 15, 21, 53, 7, TimeSpan.Zero), e.EventDate);
            Assert.Equal(EventOrigin.Tms, e.Origin);
        }

        [Theory]
        [InlineData("AT PICKUP POINT")]
        [InlineData("at_pickup_point")]
        [InlineData("At Pickup Point")]
        public void Status_names_are_accepted_with_spaces_or_underscores_in_any_case(string raw)
        {
            var result = _translator.Translate(Payload(status: raw));
            Assert.True(result.IsValid);
            Assert.Equal(OrderStatus.AtPickupPoint, result.Event!.Status);
        }

        [Fact]
        public void Invalid_payload_reports_every_problem_at_once()
        {
            var result = _translator.Translate(Payload(status: "VOLANDO", orderNumber: null, eventDate: "15/04/2025"));
            Assert.False(result.IsValid);
            Assert.Null(result.Event);
            Assert.Equal(3, result.Errors.Count);
        }

        [Fact]
        public void Null_payload_is_rejected()
        {
            var result = _translator.Translate(null);
            Assert.False(result.IsValid);
            Assert.Single(result.Errors);
        }
    }
}
