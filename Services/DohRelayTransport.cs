using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Dnex.Services;

/// <summary>Передает один DNS-пакет через DoH, не разбирая и не записывая его содержимое.</summary>
internal sealed class DohRelayTransport
{
    private readonly HttpClient _client;
    private readonly Uri _endpoint;

    internal DohRelayTransport(HttpClient client, Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new ArgumentException("Нужен адрес DoH через HTTPS", nameof(endpoint));
        _client = client;
        _endpoint = endpoint;
    }
    internal const int MaxPacketSize = 65535;

    internal async Task<byte[]> ForwardAsync(byte[] query, CancellationToken cancellationToken)
    {
        if (query.Length is < 12 or > MaxPacketSize || (query[2] & 0x80) != 0)
            throw new InvalidDataException("Некорректный DNS-запрос");
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Content = new ByteArrayContent(query);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"DoH вернул HTTP {(int)response.StatusCode}");
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/dns-message", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("DoH вернул ответ неподходящего типа");
        if (response.Content.Headers.ContentLength is > MaxPacketSize) throw new InvalidDataException("Ответ DNS слишком большой");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            int count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > MaxPacketSize) throw new InvalidDataException("Ответ DNS слишком большой");
            output.Write(buffer, 0, count);
        }
        var answer = output.ToArray();
        if (answer.Length < 12 || (answer[2] & 0x80) == 0 || answer[0] != query[0] || answer[1] != query[1])
            throw new InvalidDataException("Ответ DNS не соответствует запросу");
        return answer;
    }
}
