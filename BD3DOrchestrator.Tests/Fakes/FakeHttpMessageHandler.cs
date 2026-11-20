using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BD3DOrchestrator.Tests.Fakes;

public sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage>? handler = null) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Serializes <paramref name="data"/>. The options are required because the naming must match the faked API.</summary>
    public static HttpResponseMessage JsonResponse<T>(T data, JsonSerializerOptions options, HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
    {
        Content = JsonContent.Create(data, options: options),
    };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        this.Requests.Add(request);
        HttpResponseMessage response = handler is not null
            ? handler(request)
            : new(HttpStatusCode.NotFound);
        return Task.FromResult(response);
    }
}
