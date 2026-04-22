namespace web.Swagger;

public static class EmbeddedSwaggerSupport
{
    public const string OpenApiProxyPath = "/admin/api/openapi.json";
    public const string FramePath = "/admin/api/frame";

    public static string BuildFrameHtml(string openApiPath, string title)
    {
        var safePath = string.IsNullOrWhiteSpace(openApiPath) ? OpenApiProxyPath : openApiPath.Trim();
        var safeTitle = string.IsNullOrWhiteSpace(title) ? "API Docs" : title.Trim();

        return $$"""
<!doctype html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>{{safeTitle}}</title>
    <link rel="stylesheet" href="/lib/swagger-ui/swagger-ui.css" />
    <style>
        html, body, #swagger-ui { height: 100%; margin: 0; }
        body { background: #fff; }
    </style>
</head>
<body>
    <div id="swagger-ui"></div>
    <script src="/lib/swagger-ui/swagger-ui-bundle.js"></script>
    <script>
        window.ui = SwaggerUIBundle({
            url: '{{safePath}}',
            dom_id: '#swagger-ui',
            deepLinking: true,
            displayRequestDuration: true,
            docExpansion: 'list',
            defaultModelsExpandDepth: -1,
            presets: [SwaggerUIBundle.presets.apis],
            layout: 'BaseLayout'
        });
    </script>
</body>
</html>
""";
    }
}
