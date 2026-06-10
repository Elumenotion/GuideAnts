using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace GuideAntsApi.Tests.Services.Providers;

/// <summary>
/// Self-contained, deterministic HTTP test plumbing shared by the provider "deep" coverage tests.
/// Mirrors the proven approach in <c>ProviderNativeChatClientTests</c> but lives in its own namespace
/// with uniquely named public types so it never collides with that file's private helpers.
/// </summary>
internal sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;
    private readonly List<CapturedRequest> _requests = [];

    public Uri? LastRequestUri { get; private set; }
    public RequestHeadersSnapshot LastRequestHeaders { get; private set; } = new();
    public string LastRequestBody { get; private set; } = string.Empty;
    public int RequestCount => _requests.Count;
    public IReadOnlyList<CapturedRequest> Requests => _requests;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri;
        LastRequestHeaders = new RequestHeadersSnapshot(request.Headers);
        LastRequestBody = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        _requests.Add(new CapturedRequest(LastRequestUri, LastRequestBody, LastRequestHeaders));
        return _responder(request);
    }
}

internal sealed record CapturedRequest(Uri? Uri, string Body, RequestHeadersSnapshot Headers);

internal sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => httpClient;
}

internal sealed class RequestHeadersSnapshot
{
    private readonly Dictionary<string, IReadOnlyList<string>> _headers;

    public RequestHeadersSnapshot()
    {
        _headers = new(StringComparer.OrdinalIgnoreCase);
    }

    public RequestHeadersSnapshot(HttpRequestHeaders headers)
    {
        Authorization = headers.Authorization;
        _headers = headers.ToDictionary(
            header => header.Key,
            header => (IReadOnlyList<string>)header.Value.ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    public AuthenticationHeaderValue? Authorization { get; }

    public bool TryGetValues(string name, out IReadOnlyList<string>? values) =>
        _headers.TryGetValue(name, out values);
}

internal static class ChatHttpResponses
{
    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    public static HttpResponseMessage Sse(string sse, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        };
}
