using Integracion.TmsOms.Domain;
using Integracion.TmsOms.Infrastructure;

namespace Integracion.TmsOms.Api
{
    public static class DemoData
    {
        public static void Seed(InMemoryOmsStore store)
        {
            store.Seed(new Order("2500000006-01", "OE2500000006-01", "01021755", "TIENDAS PERUANAS S.A."));
            store.Seed(new Order("2500000007-01", "OE2500000007-01", "01021755", "TIENDAS PERUANAS S.A."));
            store.Seed(new Order("2500000008-01", "OE2500000008-01", "02034411", "SUPERMERCADOS DEL SUR S.A.C."));
        }
    }
}
