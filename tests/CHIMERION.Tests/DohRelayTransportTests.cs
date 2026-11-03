using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

public class DohRelayTransportTests
{
    private static readonly byte[] Query = { 0x12, 0x34, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
    private static HttpResponseMessage Answer(byte[] bytes, string type = "application/dns-message")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(send(request));
    }
    private static DohRelayTransport Create(Func<HttpRequestMessage, HttpResponseMessage> send) =>
        new(new HttpClient(new Handler(send)), new Uri("https://dns.example.test/dns-query/example"));

    [Fact]
    public async Task Forwards_unmodified_packet_and_checks_response_id()
    {
        var response = Query.ToArray(); response[2] = 0x81;
        var relay = Create(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("application/dns-message", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal(Query, request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
            return Answer(response);
        });
        Assert.Equal(response, await relay.ForwardAsync(Query, CancellationToken.None));
        response[1]++;
        await Assert.ThrowsAsync<InvalidDataException>(() => relay.ForwardAsync(Query, CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_invalid_query_response_type_and_oversized_response()
    {
        var valid = Query.ToArray(); valid[2] = 0x81;
        var relay = Create(_ => Answer(valid));
        await Assert.ThrowsAsync<InvalidDataException>(() => relay.ForwardAsync(new byte[3], CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => Create(_ => Answer(valid, "text/html")).ForwardAsync(Query, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => Create(_ => Answer(new byte[65536])).ForwardAsync(Query, CancellationToken.None));
    }
}
