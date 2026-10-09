using Integracion.TmsOms.Domain;

namespace Integracion.TmsOms.Tests
{
    public class OrderTests
    {
        private const string OrderNumber = "2500000006-01";

        private static readonly DateTimeOffset T0 = new(2025, 4, 15, 16, 53, 7, TimeSpan.FromHours(-5));

        private static readonly DateTimeOffset Now = new(2025, 4, 15, 22, 0, 0, TimeSpan.Zero);

        private static Order NewOrder() =>
            new(OrderNumber, "OE2500000006-01", "01021755", "TIENDAS PERUANAS S.A.");

        private static TrackingEvent Ev(OrderStatus status, int minutes = 0, string? subStatus = null) =>
            new(OrderNumber, status, T0.AddMinutes(minutes), subStatus);

        [Fact]
        public void New_order_has_no_status_and_no_visits()
        {
            var order = NewOrder();
            Assert.Null(order.Status);
            Assert.Equal(0, order.VisitCount);
            Assert.False(order.IsClosed);
        }

        [Fact]
        public void Delivered_closes_the_order_and_counts_one_visit()
        {
            var order = NewOrder();
            var result = order.Apply(Ev(OrderStatus.Delivered, subStatus: "CLIENT RECEIVED"), Now);
            Assert.Equal(ApplyOutcome.Applied, result.Outcome);
            Assert.Equal(OrderStatus.Delivered, order.Status);
            Assert.Equal("CLIENT RECEIVED", order.SubStatus);
            Assert.True(order.IsClosed);
            Assert.Equal(1, order.VisitCount);
            Assert.Null(result.FollowUpEvent);
        }

        [Fact]
        public void NotDelivered_counts_a_visit_but_keeps_the_order_open()
        {
            var order = NewOrder();
            order.Apply(Ev(OrderStatus.NotDelivered), Now);
            Assert.Equal(OrderStatus.NotDelivered, order.Status);
            Assert.False(order.IsClosed);
            Assert.Equal(1, order.VisitCount);
        }

        [Theory]
        [InlineData(OrderStatus.Planning)]
        [InlineData(OrderStatus.Started)]
        [InlineData(OrderStatus.AtPickupPoint)]
        [InlineData(OrderStatus.Collected)]
        [InlineData(OrderStatus.NotCollected)]
        [InlineData(OrderStatus.ToBeReturn)]
        [InlineData(OrderStatus.Returned)]
        [InlineData(OrderStatus.NotReturned)]
        public void Other_statuses_do_not_count_visits(OrderStatus status)
        {
            var order = NewOrder();
            order.Apply(Ev(status), Now);
            Assert.Equal(status, order.Status);
            Assert.Equal(0, order.VisitCount);
        }

        [Theory]
        [InlineData(OrderStatus.Delivered)]
        [InlineData(OrderStatus.Returned)]
        public void Closed_order_ignores_later_events(OrderStatus closingStatus)
        {
            var order = NewOrder();
            order.Apply(Ev(closingStatus, 0), Now);
            var versionBefore = order.Version;
            var visitsBefore = order.VisitCount;
            var result = order.Apply(Ev(OrderStatus.NotDelivered, 30), Now);
            Assert.Equal(ApplyOutcome.IgnoredFinalState, result.Outcome);
            Assert.False(result.StateChanged);
            Assert.Equal(closingStatus, order.Status);
            Assert.Equal(visitsBefore, order.VisitCount);
            Assert.Equal(versionBefore, order.Version);
            Assert.Null(result.FollowUpEvent);
        }

        [Fact]
        public void Ignored_event_is_still_recorded_in_history_as_ignored()
        {
            var order = NewOrder();
            order.Apply(Ev(OrderStatus.Delivered, 0), Now);
            var result = order.Apply(Ev(OrderStatus.Started, 30), Now);
            Assert.Equal(OrderStatus.Started, result.History.Status);
            Assert.Equal(ApplyOutcome.IgnoredFinalState, result.History.Outcome);
        }

        [Fact]
        public void Third_NotDelivered_emits_a_ToBeReturn_follow_up()
        {
            var order = NewOrder();
            var first = order.Apply(Ev(OrderStatus.NotDelivered, 0), Now);
            var second = order.Apply(Ev(OrderStatus.NotDelivered, 60), Now);
            var third = order.Apply(Ev(OrderStatus.NotDelivered, 120), Now);
            Assert.Null(first.FollowUpEvent);
            Assert.Null(second.FollowUpEvent);
            Assert.NotNull(third.FollowUpEvent);
            Assert.Equal(OrderStatus.ToBeReturn, third.FollowUpEvent!.Status);
            Assert.Equal(EventOrigin.System, third.FollowUpEvent.Origin);
            Assert.Equal(OrderNumber, third.FollowUpEvent.OrderNumber);
            Assert.Equal(3, order.VisitCount);
        }

        [Fact]
        public void Follow_up_is_emitted_only_once_when_the_counter_reaches_three()
        {
            var order = NewOrder();
            var followUps = 0;
            for (var i = 0; i < 5; i++)
            {
                var result = order.Apply(Ev(OrderStatus.NotDelivered, i * 10), Now);
                if (result.FollowUpEvent is not null) followUps++;
            }
            Assert.Equal(1, followUps);
        }

        [Fact]
        public void Third_visit_delivered_closes_the_order_without_requesting_return()
        {
            var order = NewOrder();
            order.Apply(Ev(OrderStatus.NotDelivered, 0), Now);
            order.Apply(Ev(OrderStatus.NotDelivered, 60), Now);
            var third = order.Apply(Ev(OrderStatus.Delivered, 120), Now);
            Assert.Equal(3, order.VisitCount);
            Assert.True(order.IsClosed);
            Assert.Null(third.FollowUpEvent);
        }

        [Fact]
        public void Follow_up_has_the_same_idempotency_key_when_regenerated()
        {
            var trigger = Ev(OrderStatus.NotDelivered, 120);
            var a = TrackingEvent.CreateToBeReturn(trigger);
            var b = TrackingEvent.CreateToBeReturn(trigger);
            Assert.Equal(a.IdempotencyKey, b.IdempotencyKey);
            Assert.NotEqual(trigger.IdempotencyKey, a.IdempotencyKey);
        }

        [Fact]
        public void System_ToBeReturn_changes_the_status_without_counting_a_visit()
        {
            var order = NewOrder();
            order.Apply(Ev(OrderStatus.NotDelivered, 0), Now);
            order.Apply(Ev(OrderStatus.NotDelivered, 60), Now);
            var third = order.Apply(Ev(OrderStatus.NotDelivered, 120), Now);
            var result = order.Apply(third.FollowUpEvent!, Now);
            Assert.Equal(ApplyOutcome.Applied, result.Outcome);
            Assert.Equal(OrderStatus.ToBeReturn, order.Status);
            Assert.Equal(3, order.VisitCount);
            Assert.Null(result.FollowUpEvent);
            Assert.Equal(EventOrigin.System, result.History.Origin);
        }

        [Fact]
        public void Version_increases_only_when_the_event_is_applied()
        {
            var order = NewOrder();
            Assert.Equal(0, order.Version);
            order.Apply(Ev(OrderStatus.Planning, 0), Now);
            order.Apply(Ev(OrderStatus.Delivered, 10), Now);
            Assert.Equal(2, order.Version);
            order.Apply(Ev(OrderStatus.Started, 20), Now);
            Assert.Equal(2, order.Version);
        }

        [Fact]
        public void Applied_event_produces_a_history_entry_with_the_event_data()
        {
            var order = NewOrder();
            var evt = new TrackingEvent(
                orderNumber: OrderNumber,
                status: OrderStatus.Delivered,
                eventDate: T0,
                subStatus: "CLIENT RECEIVED",
                serviceType: ServiceType.LastMile,
                dispatchType: DispatchType.HomeDelivery,
                vehicleCode: "LIMURB06VAN",
                courierName: "Conductor 46",
                receivedBy: "Pepito Perez",
                comments: "Entregado en domicilio"
            );
            var h = order.Apply(evt, Now).History;
            Assert.Equal(OrderNumber, h.OrderNumber);
            Assert.Equal(OrderStatus.Delivered, h.Status);
            Assert.Equal("CLIENT RECEIVED", h.SubStatus);
            Assert.Equal(T0, h.EventDate);
            Assert.Equal(Now, h.RecordedAt);
            Assert.Equal("LIMURB06VAN", h.VehicleCode);
            Assert.Equal("Conductor 46", h.CourierName);
            Assert.Equal("Pepito Perez", h.ReceivedBy);
            Assert.Equal(ApplyOutcome.Applied, h.Outcome);
            Assert.Equal(1, h.VisitCountAfter);
            Assert.Equal(evt.IdempotencyKey, h.IdempotencyKey);
        }

        [Fact]
        public void Event_for_another_order_is_rejected()
        {
            var order = NewOrder();
            var other = new TrackingEvent("9999999999-01", OrderStatus.Delivered, T0);
            Assert.Throws<InvalidOperationException>(() => order.Apply(other, Now));
        }

        [Theory]
        [InlineData(OrderStatus.Planning, false)]
        [InlineData(OrderStatus.Started, false)]
        [InlineData(OrderStatus.AtPickupPoint, false)]
        [InlineData(OrderStatus.Collected, true)]
        [InlineData(OrderStatus.NotCollected, true)]
        [InlineData(OrderStatus.Delivered, true)]
        [InlineData(OrderStatus.NotDelivered, true)]
        [InlineData(OrderStatus.ToBeReturn, false)]
        [InlineData(OrderStatus.Returned, true)]
        [InlineData(OrderStatus.NotReturned, true)]
        public void Evidence_is_required_only_in_the_six_milestones(OrderStatus status, bool expected)
        {
            Assert.Equal(expected, status.RequiresEvidence());
        }

        [Fact]
        public void Idempotency_key_is_stable_and_depends_on_status_substatus_and_date()
        {
            var a = Ev(OrderStatus.NotDelivered, 0, "ABSENT");
            var same = Ev(OrderStatus.NotDelivered, 0, "ABSENT");
            var otherDate = Ev(OrderStatus.NotDelivered, 5, "ABSENT");
            var otherSub = Ev(OrderStatus.NotDelivered, 0, "BAD ADDRESS");
            var otherStatus = Ev(OrderStatus.Delivered, 0, "ABSENT");
            Assert.Equal(a.IdempotencyKey, same.IdempotencyKey);
            Assert.NotEqual(a.IdempotencyKey, otherDate.IdempotencyKey);
            Assert.NotEqual(a.IdempotencyKey, otherSub.IdempotencyKey);
            Assert.NotEqual(a.IdempotencyKey, otherStatus.IdempotencyKey);
        }

        [Fact]
        public void Idempotency_key_ignores_the_offset_used_to_express_the_same_instant()
        {
            var peru = new TrackingEvent(OrderNumber, OrderStatus.Started, new DateTimeOffset(2025, 4, 15, 16, 0, 0, TimeSpan.FromHours(-5)));
            var utc = new TrackingEvent(OrderNumber, OrderStatus.Started, new DateTimeOffset(2025, 4, 15, 21, 0, 0, TimeSpan.Zero));
            Assert.Equal(peru.IdempotencyKey, utc.IdempotencyKey);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void TrackingEvent_requires_an_order_number(string orderNumber)
        {
            Assert.Throws<ArgumentException>(() => new TrackingEvent(orderNumber, OrderStatus.Started, T0));
        }

        [Fact]
        public void TrackingEvent_has_no_evidences_by_default()
        {
            Assert.Empty(Ev(OrderStatus.Delivered).Evidences);
        }

        [Fact]
        public void Order_requires_its_identifying_data()
        {
            Assert.Throws<ArgumentException>(() => new Order("", "OE1", "C1", "Cliente"));
            Assert.Throws<ArgumentException>(() => new Order("P1", " ", "C1", "Cliente"));
            Assert.Throws<ArgumentException>(() => new Order("P1", "OE1", "", "Cliente"));
        }

        [Fact]
        public void Clone_is_an_independent_copy_with_the_same_state()
        {
            var order = NewOrder();
            order.Apply(Ev(OrderStatus.NotDelivered), Now);
            var copy = order.Clone();
            copy.Apply(Ev(OrderStatus.NotDelivered, 10), Now);
            Assert.Equal(1, order.VisitCount);
            Assert.Equal(1, order.Version);
            Assert.Equal(2, copy.VisitCount);
            Assert.Equal(2, copy.Version);
            Assert.Equal(order.OrderNumber, copy.OrderNumber);
        }
    }

}
