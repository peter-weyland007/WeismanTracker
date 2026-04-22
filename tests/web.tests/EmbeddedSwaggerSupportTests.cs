using Xunit;
using web.Swagger;

namespace web.tests;

public sealed class EmbeddedSwaggerSupportTests
{
    [Fact]
    public void BuildFrameHtml_points_to_proxy_openapi_document()
    {
        var html = EmbeddedSwaggerSupport.BuildFrameHtml("/admin/api/openapi.json", "WeismanTracker API Docs");

        Assert.Contains("SwaggerUIBundle", html);
        Assert.Contains("/admin/api/openapi.json", html);
        Assert.Contains("WeismanTracker API Docs", html);
    }
}
