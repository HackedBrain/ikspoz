using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Relay;

namespace Ikspoz.Cli
{
    internal class AzureRelayTunneledConnection : ITunneledConnection
    {
        private static readonly HashSet<string> DoNotTunnelRequestHeaders = new(new[]
        {
            "Host",
        }, StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> ContentSpecificTunnelRequestHeaders = new(new[]
        {
            "Allow",
            "Content-Disposition",
            "Content-Encoding",
            "Content-Language",
            "Content-Length",
            "Content-Location",
            "Content-Range",
            "Content-Type",
            "Expires",
            "Last-Modified",
        }, StringComparer.OrdinalIgnoreCase);

        private readonly string _hybridConnectionString;
        private readonly Uri _tunnelTargetBaseUrl;
        private HybridConnectionListener? _hybridConnectionListener;
        private CancellationTokenSource? _tunnelingCancellationTokenSource;

        public AzureRelayTunneledConnection(string hybridConnectionString, Uri tunnelTargetBaseUrl)
        {
            _hybridConnectionString = hybridConnectionString;
            _tunnelTargetBaseUrl = tunnelTargetBaseUrl;
        }

        public TunneledConnectionEvents Events { get; } = new();

        public async Task OpenAsync(CancellationToken cancellationToken)
        {
            var tunnelingHttpClient = new HttpClient()
            {
                BaseAddress = _tunnelTargetBaseUrl
            };

            var hybridConnectionListener = new HybridConnectionListener(_hybridConnectionString);
            var hybridConnectionEndpointPublicUrl = new UriBuilder(hybridConnectionListener.Address) { Scheme = "https" }.Uri;
            var tunnelingCancellationTokenSource = new CancellationTokenSource();

            hybridConnectionListener.RequestHandler = TunnelIndividualHttpRequest;

            try
            {
                await hybridConnectionListener.OpenAsync(cancellationToken);
            }
            catch (EndpointNotFoundException)
            {
                Console.WriteLine($"💥 The specified Azure Relay Hybrid Connection was not found. Please check the specified connection string to make sure it contains the expected Hybrid Connection name for the Entity value.");

                return;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"💥 An unexpected error occurred while connecting to the specified Azure Relay Hybrid Connection:{Environment.NewLine}{Environment.NewLine}{exception.Message}");

                // TODO: log full exception details

                return;
            }

            _hybridConnectionListener = hybridConnectionListener;
            _tunnelingCancellationTokenSource = tunnelingCancellationTokenSource;

            Events.OnConnectionOpened?.Invoke(hybridConnectionEndpointPublicUrl);

            async void TunnelIndividualHttpRequest(RelayedHttpListenerContext context)
            {
                var request = context.Request;
                var response = context.Response;

                try
                {
                    tunnelingCancellationTokenSource.Token.ThrowIfCancellationRequested();

                    var tunnelRelativeRequestUrl = hybridConnectionListener.Address.MakeRelativeUri(request.Url);
                    var tunneledRequestContent = new StreamContent(request.InputStream);

                    using var tunneledRequest = new HttpRequestMessage
                    {
                        Method = new HttpMethod(request.HttpMethod),
                        RequestUri = tunnelRelativeRequestUrl,
                        Content = tunneledRequestContent,
                    };

                    foreach (var requestHeaderKey in request.Headers.AllKeys)
                    {
                        // If the header is on the "do not tunnel" list then just skip it
                        if (DoNotTunnelRequestHeaders.Contains(requestHeaderKey))
                        {
                            continue;
                        }

                        // Determine which headers collection to tunnel the value through
                        HttpHeaders targetHeadersCollection = ContentSpecificTunnelRequestHeaders.Contains(requestHeaderKey) ? tunneledRequestContent.Headers : tunneledRequest.Headers;

                        targetHeadersCollection.Add(requestHeaderKey, request.Headers.GetValues(requestHeaderKey)!);
                    }

                    tunnelingCancellationTokenSource.Token.ThrowIfCancellationRequested();

                    Events.OnTunnelingHttpRequest?.Invoke(tunneledRequest);

                    HttpResponseMessage tunneledResponse;

                    try
                    {
                        tunneledResponse = await tunnelingHttpClient.SendAsync(tunneledRequest, HttpCompletionOption.ResponseHeadersRead, tunnelingCancellationTokenSource.Token);
                    }
                    catch (Exception exception)
                    {
                        Events.OnRequestTunnelingException?.Invoke(exception);

                        response.StatusCode = HttpStatusCode.InternalServerError;
                        response.StatusDescription = "ikspoz: Error tunneling request";

                        return;
                    }

                    using (tunneledResponse)
                    {
                        Events.OnTunnelingHttpResponse?.Invoke(tunneledResponse);

                        response.StatusCode = tunneledResponse.StatusCode;
                        response.StatusDescription = tunneledResponse.ReasonPhrase;

                        var allTunneledResponseHeaders = tunneledResponse.Headers.Concat(tunneledResponse.Content.Headers);

                        foreach (var tunneledResponseHeader in allTunneledResponseHeaders)
                        {
                            foreach (var tunneledResponseHeaderValue in tunneledResponseHeader.Value)
                            {
                                response.Headers.Add(tunneledResponseHeader.Key, tunneledResponseHeaderValue);
                            }
                        }

                        try
                        {
                            await tunneledResponse.Content.CopyToAsync(response.OutputStream, tunnelingCancellationTokenSource.Token);
                        }
                        catch (Exception exception) when (exception is not TaskCanceledException)
                        {
                            Events.OnResponseTunnelingException?.Invoke(exception);
                        }
                    }
                }
                catch (TaskCanceledException)
                {
                    response.StatusCode = HttpStatusCode.InternalServerError;
                    response.StatusDescription = "ikspoz: Tunnel closing";
                }
                finally
                {
                    await response.CloseAsync();
                }
            }
        }

        public async Task CloseAsync(CancellationToken cancellationToken)
        {
            if (_tunnelingCancellationTokenSource is null)
            {
                throw new InvalidOperationException("Connection is not currently open.");
            }

            Events.OnConnectionClosing?.Invoke();

            _tunnelingCancellationTokenSource.Cancel();
            await _hybridConnectionListener!.CloseAsync(cancellationToken);

            _tunnelingCancellationTokenSource = null;
            _hybridConnectionListener = null;

            Events.OnConnectionClosed?.Invoke();
        }
    }
}
