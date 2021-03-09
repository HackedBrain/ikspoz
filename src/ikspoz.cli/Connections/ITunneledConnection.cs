using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Ikspoz.Cli
{
    internal interface ITunneledConnection
    {
        TunneledConnectionEvents Events { get; }

        Task OpenAsync(CancellationToken cancellationToken);

        Task CloseAsync(CancellationToken cancellationToken);
    }

    internal class TunneledConnectionEvents
    {
        public Action? OnConnectionOpening { get; set; }

        public Action<Uri>? OnConnectionOpened { get; set; }

        public Action? OnConnectionClosing { get; set; }

        public Action? OnConnectionClosed { get; set; }

        public Action<HttpRequestMessage>? OnTunnelingHttpRequest { get; set; }

        public Action<HttpResponseMessage>? OnTunnelingHttpResponse { get; set; }

        public Action<Exception>? OnRequestTunnelingException { get; set; }

        public Action<Exception>? OnResponseTunnelingException { get; set; }
    }
}
