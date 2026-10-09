using Integracion.TmsOms.Domain;
using System.Globalization;

namespace Integracion.TmsOms.Application
{
    public static class StoragePaths
    {
        public static string ForEvidence(string orderNumber, OrderStatus status, DateTimeOffset eventDate, string fileName)
        {
            var stamp = eventDate.UtcDateTime.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
            return $"{Segment(orderNumber)}/{stamp}-{status}/{Segment(Path.GetFileName(fileName))}";
        }

        public static string Segment(string value)
        {
            var chars = value
                .Trim()
                .Select(c => char.IsLetterOrDigit(c) || (c is '.' or '-' or '_') ? c : '_')
                .ToArray();
            var clean = new string(chars).Trim('.');
            return clean.Length == 0 ? "_" : clean;
        }
    }
}
