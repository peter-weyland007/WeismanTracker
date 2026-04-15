using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.JSInterop;
using web.Services;
using Xunit;

namespace web.tests;

public class CatEtApiClientComputerSearchTests
{
    [Fact]
    public async Task SearchLicenseAssignableComputersAsync_usesPagedComputerEndpointWithSearchTermAndLimit()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"items\":[],\"totalCount\":0,\"page\":1,\"pageSize\":25}", Encoding.UTF8, "application/json")
        });

        var client = CreateClient(handler);

        _ = await client.SearchLicenseAssignableComputersAsync("lv426", 25);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("/api/catet/computers?page=1&pageSize=25&search=lv426&visibility=all", handler.LastRequest!.RequestUri!.PathAndQuery);
    }

    private static CatEtApiClient CreateClient(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.test")
        };

        var factory = new FakeHttpClientFactory(httpClient);
        var tokenStore = new AuthTokenStore(new FakeJsRuntime(), new HttpContextAccessor { HttpContext = new DefaultHttpContext() });
        return new CatEtApiClient(factory, tokenStore);
    }

    private sealed class FakeHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(responder(request));
        }
    }

    private sealed class FakeJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => ValueTask.FromResult(default(TValue)!);
    }
}
