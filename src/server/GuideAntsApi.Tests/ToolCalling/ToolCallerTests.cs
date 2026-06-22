using System.Net;
using System.Text;
using System.Text.Json;
using AntRunner.ToolCalling.Functions;
using FluentAssertions;

namespace GuideAntsApi.Tests.ToolCalling;

[TestClass]
[DoNotParallelize]
public sealed class ToolCallerTests
{
    [TestInitialize]
    public void Initialize()
    {
        ToolCaller.ConfigurationVariableResolver = null;
    }

    [TestCleanup]
    public void Cleanup()
    {
        ToolCaller.ConfigurationVariableResolver = null;
    }

    [TestMethod]
    public void GetToolCallers_WithServiceHeaderLiteral_UsesConfiguredHeader()
    {
        using var spec = JsonDocument.Parse("""
            {
              "openapi": "3.0.0",
              "servers": [{ "url": "https://api.example.com" }],
              "paths": {
                "/items": {
                  "get": {
                    "operationId": "listItems",
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """);

        var domainAuth = new DomainAuth
        {
            HostAuthorizationConfigurations = new Dictionary<string, ActionAuthConfig>
            {
                ["api.example.com"] = new()
                {
                    AuthType = AuthType.service_http,
                    HeaderKey = "x-api-key",
                    HeaderValueLiteral = "abc123"
                }
            }
        };

        var callers = ToolCaller.GetToolCallers(spec, domainAuth);

        callers.Should().ContainKey("listItems");
        callers["listItems"].AuthHeaders.Should().Contain(new KeyValuePair<string, string>("x-api-key", "abc123"));
        callers["listItems"].AuthRequiredButMissing.Should().BeFalse();
    }

    [TestMethod]
    public void GetToolCallers_WithServiceQueryFromEnvironment_UsesConfiguredValue()
    {
        using var spec = JsonDocument.Parse("""
            {
              "openapi": "3.0.0",
              "servers": [{ "url": "https://api.example.com" }],
              "paths": {
                "/items": {
                  "get": {
                    "operationId": "listItems",
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """);
        const string envVarName = "GUIDEANTS_TEST_TOOLCALLER_QUERY_API_KEY";
        Environment.SetEnvironmentVariable(envVarName, "resolved-secret");
        var domainAuth = new DomainAuth
        {
            HostAuthorizationConfigurations = new Dictionary<string, ActionAuthConfig>
            {
                ["api.example.com"] = new()
                {
                    AuthType = AuthType.service_query,
                    HeaderKey = "api_key",
                    HeaderValueEnvironmentVariable = envVarName
                }
            }
        };

        try
        {
            var callers = ToolCaller.GetToolCallers(spec, domainAuth);
            callers["listItems"].AuthQueryParams.Should().Contain(new KeyValuePair<string, string>("api_key", "resolved-secret"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVarName, null);
        }
    }

    [TestMethod]
    public void GetToolCallers_WithMissingConfiguredSecret_FlagsMissingAuth()
    {
        using var spec = JsonDocument.Parse("""
            {
              "openapi": "3.0.0",
              "servers": [{ "url": "https://api.example.com" }],
              "paths": {
                "/items": {
                  "get": {
                    "operationId": "listItems",
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """);

        var domainAuth = new DomainAuth
        {
            HostAuthorizationConfigurations = new Dictionary<string, ActionAuthConfig>
            {
                ["api.example.com"] = new()
                {
                    AuthType = AuthType.service_http,
                    HeaderKey = "x-api-key"
                }
            }
        };

        var callers = ToolCaller.GetToolCallers(spec, domainAuth);

        callers["listItems"].AuthRequiredButMissing.Should().BeTrue();
    }

    [TestMethod]
    public void ActionType_MapsUriSchemesToExpectedKinds()
    {
        CreateCaller("client://bridge", "/x", "GET").ActionType.Should().Be(ActionType.ClientHandled);
        CreateCaller("client://mcp-bridge-my-mcp", "/tools/search", "POST").ActionType.Should().Be(ActionType.ClientHandled);
        CreateCaller("tool://localhost", "A.B.C.Do", "POST").ActionType.Should().Be(ActionType.LocalFunction);
        CreateCaller("sandbox://init.py", "/x", "POST").ActionType.Should().Be(ActionType.SandboxHandled);
        CreateCaller("https://api.example.com", "/x", "GET").ActionType.Should().Be(ActionType.WebApi);
    }

    [TestMethod]
    public async Task ExecuteWebApiAsync_BuildsUrlHeadersAndQueryAsExpected()
    {
        var methodSchema = ParseElement("""
            {
              "parameters": [
                { "name": "q", "in": "query", "schema": { "type": "string" } }
              ]
            }
            """);

        var caller = new ToolCaller(
            baseUrl: "https://api.example.com/v1",
            path: "/search/{id}",
            method: "GET",
            operation: "search",
            methodSchema: methodSchema,
            contentType: "application/json",
            authHeaders: new Dictionary<string, string> { ["x-api-key"] = "header-secret" },
            authQueryParams: new Dictionary<string, string> { ["api_key"] = "query-secret" });
        caller.Params = new Dictionary<string, object> { ["id"] = "42", ["q"] = "term with spaces" };

        var handler = new CapturingHandler();
        using var client = new HttpClient(handler);

        var response = await caller.ExecuteWebApiAsync(httpClient: client);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString().Should().Be("https://api.example.com/v1/search/42?q=term with spaces&api_key=query-secret");
        handler.LastRequest.Headers.GetValues("x-api-key").Should().ContainSingle().Which.Should().Be("header-secret");
    }

    [TestMethod]
    public async Task ExecuteWebApiAsync_WithOAuthAndNoToken_ThrowsArgumentNullException()
    {
        var caller = new ToolCaller(
            baseUrl: "https://api.example.com",
            path: "/items",
            method: "GET",
            operation: "listItems",
            methodSchema: ParseElement("""{ "responses": { "200": { "description": "ok" } } }"""),
            contentType: "application/json",
            authHeaders: [],
            authQueryParams: [],
            oAuth: true);

        var act = async () => await caller.ExecuteWebApiAsync(httpClient: new HttpClient(new CapturingHandler()));

        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithMessage("*OAuth user access token is required*");
    }

    [TestMethod]
    public async Task ExecuteWebApiAsync_WhenAuthIsRequiredButMissing_ThrowsMissingAssistantAuthException()
    {
        var caller = new ToolCaller(
            baseUrl: "https://api.example.com",
            path: "/items",
            method: "GET",
            operation: "listItems",
            methodSchema: ParseElement("""{ "responses": { "200": { "description": "ok" } } }"""),
            contentType: "application/json",
            authHeaders: [],
            authQueryParams: [],
            authRequiredButMissing: true);

        var act = async () => await caller.ExecuteWebApiAsync(httpClient: new HttpClient(new CapturingHandler()));

        await act.Should().ThrowAsync<ToolCaller.MissingAssistantAuthException>()
            .WithMessage("Assistant tool requires an API key that is not set.");
    }

    [TestMethod]
    public async Task ExecuteWebApiAsync_JsonObjectBody_IncludesOnlyDeclaredProperties()
    {
        var methodSchema = ParseElement("""
            {
              "requestBody": {
                "content": {
                  "application/json": {
                    "schema": {
                      "type": "object",
                      "properties": {
                        "name": { "type": "string" },
                        "count": { "type": "integer" }
                      }
                    }
                  }
                }
              }
            }
            """);

        var caller = new ToolCaller(
            baseUrl: "https://api.example.com",
            path: "/items",
            method: "POST",
            operation: "createItem",
            methodSchema: methodSchema,
            contentType: "application/json",
            authHeaders: [],
            authQueryParams: []);
        caller.Params = new Dictionary<string, object>
        {
            ["name"] = "widget",
            ["count"] = 2,
            ["ignored"] = "should-not-be-in-body"
        };

        var handler = new CapturingHandler();
        using var client = new HttpClient(handler);
        await caller.ExecuteWebApiAsync(httpClient: client);

        handler.LastBody.Should().NotBeNull();
        using var bodyDoc = JsonDocument.Parse(handler.LastBody!);
        bodyDoc.RootElement.GetProperty("name").GetString().Should().Be("widget");
        bodyDoc.RootElement.GetProperty("count").GetInt32().Should().Be(2);
        bodyDoc.RootElement.TryGetProperty("ignored", out _).Should().BeFalse();
    }

    [TestMethod]
    public async Task ExecuteWebApiAsync_TextPlainBody_UsesRequestBodyString()
    {
        var methodSchema = ParseElement("""
            {
              "requestBody": {
                "content": {
                  "text/plain": {
                    "schema": { "type": "string" }
                  }
                }
              }
            }
            """);

        var caller = new ToolCaller(
            baseUrl: "https://api.example.com",
            path: "/notes",
            method: "POST",
            operation: "saveNote",
            methodSchema: methodSchema,
            contentType: "text/plain",
            authHeaders: [],
            authQueryParams: []);
        caller.Params = new Dictionary<string, object> { ["requestBody"] = "plain-text-payload" };

        var handler = new CapturingHandler();
        using var client = new HttpClient(handler);
        await caller.ExecuteWebApiAsync(httpClient: client);

        handler.LastBody.Should().Be("plain-text-payload");
        handler.LastRequest!.Content!.Headers.ContentType!.MediaType.Should().Be("text/plain");
    }

    [TestMethod]
    public async Task ExecuteWebApiAsync_UnsupportedMethod_ThrowsNotSupportedException()
    {
        var caller = new ToolCaller(
            baseUrl: "https://api.example.com",
            path: "/items",
            method: "TRACE",
            operation: "traceItems",
            methodSchema: ParseElement("""{}"""),
            contentType: "application/json",
            authHeaders: [],
            authQueryParams: []);

        var act = async () => await caller.ExecuteWebApiAsync();

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*Unsupported HTTP method: TRACE*");
    }

    [TestMethod]
    public void AddMissingRequiredParamsFromSchema_AddsDefaultsAndSingleEnumValues()
    {
        var caller = new ToolCaller(
            baseUrl: "https://api.example.com",
            path: "/items",
            method: "POST",
            operation: "createItem",
            methodSchema: ParseElement("""
                {
                  "requestBody": {
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "object",
                          "required": ["mode", "status", "name"],
                          "properties": {
                            "mode": { "type": "string", "default": "fast" },
                            "status": { "type": "string", "enum": ["active"] },
                            "name": { "type": "string" }
                          }
                        }
                      }
                    }
                  }
                }
                """),
            contentType: "application/json",
            authHeaders: [],
            authQueryParams: [])
        {
            Params = new Dictionary<string, object> { ["name"] = "widget" }
        };

        caller.AddMissingRequiredParamsFromSchema();

        caller.Params.Should().Contain(new KeyValuePair<string, object>("mode", "fast"));
        caller.Params.Should().Contain(new KeyValuePair<string, object>("status", "active"));
        caller.Params.Should().ContainKey("name");
    }

    [TestMethod]
    public void ValidateParamsAgainstSchema_ReportsUnknownAndMissingParameters()
    {
        var caller = new ToolCaller(
            baseUrl: "tool://localhost",
            path: $"{typeof(SampleLocalTools).FullName}.Echo",
            method: "POST",
            operation: "echo",
            methodSchema: ParseElement("""
                {
                  "parameters": [
                    { "name": "text", "in": "query", "required": true, "schema": { "type": "string" } }
                  ]
                }
                """),
            contentType: "application/json",
            authHeaders: [],
            authQueryParams: [])
        {
            Params = new Dictionary<string, object> { ["unexpected"] = "value" }
        };

        var result = caller.ValidateParamsAgainstSchema();

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not a valid parameter");
        result.ErrorMessage.Should().Contain("missing required parameter");
        result.ErrorMessage.Should().Contain("`unexpected`");
        result.ErrorMessage.Should().Contain("`text`");
    }

    [TestMethod]
    public async Task ExecuteLocalFunctionAsync_InvokesPropertyAndMethod()
    {
        var propertyCaller = new ToolCaller(
            baseUrl: "tool://localhost",
            path: $"{typeof(SampleLocalTools).FullName}.BuildVersion",
            method: "POST",
            operation: "buildVersion",
            methodSchema: ParseElement("""{}"""),
            contentType: "application/json",
            authHeaders: [],
            authQueryParams: []);

        var methodCaller = new ToolCaller(
            baseUrl: "tool://localhost",
            path: $"{typeof(SampleLocalTools).FullName}.Add",
            method: "POST",
            operation: "add",
            methodSchema: ParseElement("""
                {
                  "parameters": [
                    { "name": "a", "in": "query", "required": true, "schema": { "type": "integer" } },
                    { "name": "b", "in": "query", "required": false, "schema": { "type": "integer" } }
                  ]
                }
                """),
            contentType: "application/json",
            authHeaders: [],
            authQueryParams: [])
        {
            Params = new Dictionary<string, object>
            {
                ["a"] = ParseElement("7"),
                ["b"] = ParseElement("5")
            }
        };

        var propertyResult = await propertyCaller.ExecuteLocalFunctionAsync();
        var methodResult = await methodCaller.ExecuteLocalFunctionAsync();

        propertyResult.Should().Be("1.2.3");
        methodResult.Should().Be(12);
    }

    [TestMethod]
    public void Clone_CreatesIndependentDictionaries()
    {
        var original = new ToolCaller(
            baseUrl: "https://api.example.com",
            path: "/items",
            method: "POST",
            operation: "createItem",
            methodSchema: ParseElement("""{}"""),
            contentType: "application/json",
            authHeaders: new Dictionary<string, string> { ["x-key"] = "one" },
            authQueryParams: new Dictionary<string, string> { ["q"] = "one" })
        {
            Params = new Dictionary<string, object> { ["name"] = "alpha" }
        };

        var clone = original.Clone();
        clone.AuthHeaders["x-key"] = "two";
        clone.AuthQueryParams["q"] = "two";
        clone.Params!["name"] = "beta";

        original.AuthHeaders["x-key"].Should().Be("one");
        original.AuthQueryParams["q"].Should().Be("one");
        original.Params!["name"].Should().Be("alpha");
    }

    private static ToolCaller CreateCaller(string baseUrl, string path, string method)
    {
        return new ToolCaller(
            baseUrl: baseUrl,
            path: path,
            method: method,
            operation: "operation",
            methodSchema: ParseElement("""{}"""),
            contentType: "application/json",
            authHeaders: [],
            authQueryParams: []);
    }

    private static JsonElement ParseElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content == null
                ? null
                : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json")
            });
        }
    }

    private static class SampleLocalTools
    {
        public static string BuildVersion => "1.2.3";

        public static int Add(int a, int b = 0)
        {
            return a + b;
        }
    }
}
