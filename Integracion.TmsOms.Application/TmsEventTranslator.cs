using Integracion.TmsOms.Domain;
using System.Globalization;

namespace Integracion.TmsOms.Application
{
    public sealed record TranslationResult(TrackingEvent? Event, IReadOnlyList<string> Errors)
    {
        public bool IsValid => Event is not null;
    }

    public sealed class TmsEventTranslator(TimeSpan? sourceUtcOffset = null)
    {
        private const string DateFormat = "yyyy-MM-dd HH:mm:ss";

        private static readonly Dictionary<string, OrderStatus> Statuses = new()
        {
            ["PLANNING"] = OrderStatus.Planning,
            ["STARTED"] = OrderStatus.Started,
            ["AT PICKUP POINT"] = OrderStatus.AtPickupPoint,
            ["COLLECTED"] = OrderStatus.Collected,
            ["NOT COLLECTED"] = OrderStatus.NotCollected,
            ["DELIVERED"] = OrderStatus.Delivered,
            ["NOT DELIVERED"] = OrderStatus.NotDelivered,
            ["TO BE RETURN"] = OrderStatus.ToBeReturn,
            ["RETURNED"] = OrderStatus.Returned,
            ["NOT RETURNED"] = OrderStatus.NotReturned
        };

        private static readonly Dictionary<string, ServiceType> ServiceTypes = new()
        {
            ["LAST MILE"] = ServiceType.LastMile,
            ["PICKUP"] = ServiceType.Pickup,
            ["RETURN"] = ServiceType.Return
        };

        private static readonly Dictionary<string, DispatchType> DispatchTypes = new()
        {
            ["HOME DELIVERY"] = DispatchType.HomeDelivery,
            ["STORE WITHDRAWAL"] = DispatchType.StoreWithdrawal,
            ["REVERSE"] = DispatchType.Reverse
        };

        private readonly TimeSpan _sourceUtcOffset = sourceUtcOffset ?? TimeSpan.FromHours(-5);

        public TranslationResult Translate(TmsWebhookPayload? payload)
        {
            if (payload is null)
            {
                return new TranslationResult(null, ["El cuerpo de la petición es obligatorio."]);
            }
            var errors = new List<string>();
            var status = Parse(Statuses, payload.Status, "status", errors);
            var serviceType = Parse(ServiceTypes, payload.ServiceType, "serviceType", errors);
            var dispatchType = Parse(DispatchTypes, payload.DispatchType, "dispatchType", errors);
            var orderNumber = payload.Details?.OrderNumber;
            if (string.IsNullOrWhiteSpace(orderNumber))
            {
                errors.Add("details.orderNumber es obligatorio.");
            }
            var eventDate = default(DateTimeOffset);
            if (string.IsNullOrWhiteSpace(payload.EventDate))
            {
                errors.Add("eventDate es obligatorio.");
            }
            else if (!DateTime.TryParseExact(payload.EventDate, DateFormat, CultureInfo.InvariantCulture,
                         DateTimeStyles.None, out var local))
            {
                errors.Add($"eventDate debe tener el formato '{DateFormat}'.");
            }
            else
            {
                eventDate = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), _sourceUtcOffset);
            }
            var evidences = new List<EvidenceRef>();
            var rawEvidences = payload.Details?.Evidences;
            if (rawEvidences is not null)
            {
                for (var i = 0; i < rawEvidences.Count; i++)
                {
                    var evidence = rawEvidences[i];
                    if (evidence is null
                        || string.IsNullOrWhiteSpace(evidence.FileName)
                        || string.IsNullOrWhiteSpace(evidence.Url))
                    {
                        errors.Add($"details.evidences[{i}] requiere fileName y url.");
                        continue;
                    }
                    evidences.Add(new EvidenceRef(
                        evidence.Label?.Trim() ?? string.Empty,
                        evidence.FileType?.Trim() ?? string.Empty,
                        evidence.FileName.Trim(),
                        evidence.Url.Trim()));
                }
            }
            if (errors.Count > 0)
            {
                return new TranslationResult(null, errors);
            }
            var trackingEvent = new TrackingEvent(
                orderNumber: orderNumber!,
                status: status.GetValueOrDefault(),
                eventDate: eventDate,
                subStatus: payload.SubStatus,
                serviceType: serviceType.GetValueOrDefault(),
                dispatchType: dispatchType.GetValueOrDefault(),
                vehicleCode: payload.VehicleCode,
                courierName: payload.CourierName,
                receivedBy: payload.Details?.ReceivedBy,
                comments: payload.Details?.Comments,
                evidences: evidences);
            return new TranslationResult(trackingEvent, []);
        }

        private static T? Parse<T>(Dictionary<string, T> map, string? raw, string field, List<string> errors)
            where T : struct
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                errors.Add($"{field} es obligatorio.");
                return null;
            }
            var key = raw.Trim().ToUpperInvariant().Replace('_', ' ');
            if (map.TryGetValue(key, out var value))
            {
                return value;
            }
            errors.Add($"{field} '{raw}' no es un valor conocido.");
            return null;
        }
    }
}
