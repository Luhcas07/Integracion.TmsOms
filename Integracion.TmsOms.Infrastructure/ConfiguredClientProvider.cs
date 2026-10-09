using Integracion.TmsOms.Application;

namespace Integracion.TmsOms.Infrastructure
{
    public sealed class ConfiguredClientProvider(IReadOnlyDictionary<string, ClientConfig> clients) : IClientConfigProvider
    {
        private readonly IReadOnlyDictionary<string, ClientConfig> _clients = clients ?? throw new ArgumentNullException(nameof(clients));

        public ClientConfig? Get(string clientCode) =>
            _clients.TryGetValue(clientCode, out var config) ? config : null;
    }
}
