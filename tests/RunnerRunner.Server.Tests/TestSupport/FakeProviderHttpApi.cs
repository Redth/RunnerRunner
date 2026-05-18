using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace RunnerRunner.Server.Tests.TestSupport;

internal sealed class FakeProviderHttpApi : HttpMessageHandler, IHttpClientFactory
{
    private readonly object _gate = new();
    private readonly List<Route> _routes = [];
    private readonly List<RecordedHttpRequest> _requests = [];

    public FakeProviderHttpApi()
    {
    }

    public FakeProviderHttpApi(HttpResponseMessage response)
        : this(_ => response)
    {
    }

    public FakeProviderHttpApi(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        Respond(handler);
    }

    public IReadOnlyList<RecordedHttpRequest> Requests
    {
        get
        {
            lock (_gate)
                return _requests.ToArray();
        }
    }

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    public FakeProviderHttpApi Respond(Func<HttpRequestMessage, HttpResponseMessage> handler)
        => Respond(_ => true, handler);

    public FakeProviderHttpApi Respond(
        Func<HttpRequestMessage, bool> predicate,
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _routes.Add(new Route(predicate, handler));
        return this;
    }

    public FakeProviderHttpApi RespondJson(
        Func<HttpRequestMessage, bool> predicate,
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK)
        => Respond(predicate, _ => JsonResponse(json, statusCode));

    public static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Record(request);

        foreach (var route in _routes)
        {
            if (route.Predicate(request))
                return Task.FromResult(route.Handler(request));
        }

        throw new InvalidOperationException($"Unexpected HTTP request: {request.Method} {request.RequestUri}");
    }

    private void Record(HttpRequestMessage request)
    {
        lock (_gate)
        {
            _requests.Add(new RecordedHttpRequest(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization,
                request.Headers.UserAgent.ToString()));
        }
    }

    private sealed record Route(
        Func<HttpRequestMessage, bool> Predicate,
        Func<HttpRequestMessage, HttpResponseMessage> Handler);
}

internal sealed record RecordedHttpRequest(
    HttpMethod Method,
    Uri? Uri,
    AuthenticationHeaderValue? Authorization,
    string UserAgent)
{
    public string PathAndQuery => Uri?.PathAndQuery ?? "";
}
