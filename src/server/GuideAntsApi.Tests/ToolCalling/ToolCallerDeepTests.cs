using System.Net;
using System.Text;
using System.Text.Json;
using AntRunner.ToolCalling;
using AntRunner.ToolCalling.AssistantDefinitions;
using AntRunner.ToolCalling.Functions;
using FluentAssertions;
using GuideAntsApi.Services;

namespace GuideAntsApi.Tests.ToolCalling;

/// <summary>
/// Second-wave coverage for <see cref="ToolCaller"/> targeting branches not exercised by
/// <c>ToolCallerTests</c>: the no-auth/oauth factory overloads, response-schema projection,
/// non-object/textual request bodies, query/fragment handling, request-body schema validation,
/// JsonElement parameter conversion and schema default injection.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ToolCallerDeepTests
{
    [TestInitialize]
    public void Initialize() => ToolCaller.ConfigurationVariableResolver = null;

    [TestCleanup]
    public void Cleanup() => ToolCaller.ConfigurationVariableResolver = null;

    private static JsonElement ParseElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static JsonDocument Spec(string paths = """
        "/items": { "get": { "operationId": "listItems", "responses": { "200": { "description": "ok" } } } }
        """) =>
        JsonDocument.Parse($$"""
            {
              "openapi": "3.0.0",
              "servers": [{ "url": "https://api.example.com" }],
              "paths": { {{paths}} }
            }
            """);

    [TestMethod]
    public void GetToolCallers_SimpleOverload_UsesServerUrlAndNoAuth()
    {
        using var spec = Spec();

        var callers = ToolCaller.GetToolCallers(spec);

        callers.Should().ContainKey("listItems");
        callers["listItems"].BaseUrl.Should().Be("https://api.example.com");
        callers["listItems"].OAuth.Should().BeFalse();
        callers["listItems"].AuthHeaders.Should().BeEmpty();
    }

    [TestMethod]
    public void GetToolCallers_OAuthConfig_SetsOAuthFlag()
    {
        using var spec = Spec();
        var domainAuth = new DomainAuth
        {
            HostAuthorizationConfigurations = new Dictionary<string, ActionAuthConfig>
            {
                ["api.example.com"] = new() { AuthType = AuthType.oauth, HeaderKey = "Authorization" }
            }
        };

        var callers = ToolCaller.GetToolCallers(spec, domainAuth);

        callers["listItems"].OAuth.Should().BeTrue();
        callers["listItems"].AuthRequiredButMissing.Should().BeFalse();
    }

    [TestMethod]
    public void GetToolCallers_ServiceHeaderEnvVar_FallsBackToVariableNameWhenUnresolved()
    {
        using var spec = Spec();
        var domainAuth = new DomainAuth
        {
            HostAuthorizationConfigurations = new Dictionary<string, ActionAuthConfig>
            {
                ["api.example.com"] = new()
                {
                    AuthType = AuthType.service_http,
                    HeaderKey = "x-api-key",
                    // Not an env var that resolves; literal secret carried in the env-var field.
                    HeaderValueEnvironmentVariable = "literal-secret-value"
                }
            }
        };

        var callers = ToolCaller.GetToolCallers(spec, domainAuth);

        callers["listItems"].AuthHeaders.Should()
            .Contain(new KeyValuePair<string, string>("x-api-key", "literal-secret-value"));
    }

    [TestMethod]
    public void GetToolCallers_ServiceHeaderEnvVar_UsesConfigurationResolver()
    {
        using var spec = Spec();
        ToolCaller.ConfigurationVariableResolver = name => name == "MY_KEY" ? "resolved-by-config" : null;
        var domainAuth = new DomainAuth
        {
            HostAuthorizationConfigurations = new Dictionary<string, ActionAuthConfig>
            {
                ["api.example.com"] = new()
                {
                    AuthType = AuthType.service_http,
                    HeaderKey = "x-api-key",
                    HeaderValueEnvironmentVariable = "MY_KEY"
                }
            }
        };

        var callers = ToolCaller.GetToolCallers(spec, domainAuth);

        callers["listItems"].AuthHeaders.Should()
            .Contain(new KeyValuePair<string, string>("x-api-key", "resolved-by-config"));
    }

    [TestMethod]
    public void GetToolCallers_MaskedLiteral_FallsThroughToMissing()
    {
        using var spec = Spec();
        var domainAuth = new DomainAuth
        {
            HostAuthorizationConfigurations = new Dictionary<string, ActionAuthConfig>
            {
                ["api.example.com"] = new()
                {
                    AuthType = AuthType.service_query,
                    HeaderKey = "api_key",
                    HeaderValueLiteral = "••••••••"
                }
            }
        };

        var callers = ToolCaller.GetToolCallers(spec, domainAuth);

        callers["listItems"].AuthRequiredButMissing.Should().BeTrue();
    }

    [TestMethod]
    public void ResponseSchemas_ProjectsTwoHundredSchema()
    {
        var caller = new ToolCaller(
            baseUrl: "https://api.example.com",
            path: "/items",
            method: "GET",
            operation: "listItems",
            methodSchema: ParseElement("""
                {
                  "responses": {
                    "404": { "description": "missing" },
                    "200": { "content": { "application/json": { "schema": { "type": "object" } } } }
                  }
                }
                """),
            contentType: "application/json",
            authHeaders: [],
            authQueryParams: []);

        caller.ResponseSchemas.Should().ContainKey("200");
        caller.ResponseSchemas["200"].GetProperty("type").GetString().Should().Be("object");
    }

    [TestMethod]
    public void ResponseSchemas_WhenNoContent_ReturnsEmpty()
    {
        var caller = new ToolCaller(
            baseUrl: "https://api.example.com",
            path: "/items",
            method: "GET",
            operation: "listItems",
            methodSchema: ParseElement("""{ "responses": { "200": { "description": "ok" } } }"""),
            contentType: "application/json",
            authHeaders: [],
            authQueryParams: []);

        caller.ResponseSchemas.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ExecuteWebApiAsync_JsonArrayBody_UsesRequestBodyParam()
    {
        var methodSchema = ParseElement("""
            {
              "requestBody": {
                "content": {
                  "application/json": { "schema": { "type": "array", "items": { "type": "string" } } }
                }
              }
            }
            """);
        var caller = new ToolCaller(
            baseUrl: "https://api.example.com",
            path: "/bulk",
            method: "POST",
            operation: "bulk",
            methodSchema: methodSchema,
            contentType: "application/json",
            authHeaders: [],
            authQueryParams: [])
        {
            Params = new Dictionary<string, object> { ["requestBody"] = ParseElement("""["a","b"]""") }
        };

        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        await caller.ExecuteWebApiAsync(httpClient: client);

        handler.LastBody.Should().Be("""["a","b"]""");
        handler.LastContentType.Should().Be("application/json");
    }

    [TestMethod]
    public async Task ExecuteWebApiAsync_XhtmlBody_UsesTextualContent()
    {
        var methodSchema = ParseElement("""
            {
              "requestBody": {
                "content": { "application/xhtml+xml": { "schema": { "type": "string" } } }
              }
            }
            """);
        var caller = new ToolCaller(
            baseUrl: "https://api.example.com",
            path: "/page",
            method: "PUT",
            operation: "savePage",
            methodSchema: methodSchema,
            contentType: "application/xhtml+xml",
            authHeaders: [],
            authQueryParams: [])
        {
            Params = new Dictionary<string, object> { ["requestBody"] = "<p>hi</p>" }
        };

        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        await caller.ExecuteWebApiAsync(httpClient: client);

        handler.LastBody.Should().Be("<p>hi</p>");
        handler.LastContentType.Should().Be("application/xhtml+xml");
    }

    [TestMethod]
    public async Task ExecuteWebApiAsync_ContentTypeMismatch_FallsBackToFirstMediaType()
    {
        // ContentType "text/plain" is not present in the schema content; the first media type
        // (application/json) is selected as the fallback, exercising the chosen-media fallback path.
        var methodSchema = ParseElement("""
            {
              "requestBody": {
                "content": {
                  "application/json": {
                    "schema": { "type": "object", "properties": { "name": { "type": "string" } } }
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
            contentType: "text/plain",
            authHeaders: [],
            authQueryParams: [])
        {
            Params = new Dictionary<string, object> { ["name"] = "widget" }
        };

        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        await caller.ExecuteWebApiAsync(httpClient: client);

        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.GetProperty("name").GetString().Should().Be("widget");
    }

    [TestMethod]
    public async Task ExecuteWebApiAsync_AuthQueryParams_PreserveFragmentAndReplaceExistingQuery()
    {
        var caller = new ToolCaller(
            baseUrl: "https://api.example.com",
            path: "/search?page=1#section",
            method: "GET",
            operation: "search",
            methodSchema: ParseElement("""{}"""),
            contentType: "application/json",
            authHeaders: [],
            authQueryParams: new Dictionary<string, string> { ["api_key"] = "secret", ["page"] = "2" });

        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        await caller.ExecuteWebApiAsync(httpClient: client);

        var uri = handler.LastUri!.ToString();
        uri.Should().Contain("api_key=secret");
        uri.Should().Contain("page=2");
        uri.Should().EndWith("#section");
    }

    [TestMethod]
    public void ValidateParamsAgainstSchema_ObjectRequestBody_ReportsMissingRequired()
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
                          "required": ["name", "size"],
                          "properties": { "name": { "type": "string" }, "size": { "type": "integer" } }
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

        var result = caller.ValidateParamsAgainstSchema();

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("`size`");
        result.ErrorMessage.Should().Contain("missing required parameter");
    }

    [TestMethod]
    public void ValidateParamsAgainstSchema_NonObjectRequiredBody_RequiresRequestBodyParam()
    {
        var caller = new ToolCaller(
            baseUrl: "https://api.example.com",
            path: "/raw",
            method: "POST",
            operation: "saveRaw",
            methodSchema: ParseElement("""
                {
                  "requestBody": {
                    "required": true,
                    "content": { "text/plain": { "schema": { "type": "string" } } }
                  }
                }
                """),
            contentType: "text/plain",
            authHeaders: [],
            authQueryParams: []);

        var result = caller.ValidateParamsAgainstSchema();

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("`requestBody`");
    }

    [TestMethod]
    public void ValidateParamsAgainstSchema_ThreeUnknownParams_FormatsOxfordList()
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
                        "schema": { "type": "object", "properties": { "name": { "type": "string" } } }
                      }
                    }
                  }
                }
                """),
            contentType: "application/json",
            authHeaders: [],
            authQueryParams: [])
        {
            Params = new Dictionary<string, object> { ["x"] = 1, ["y"] = 2, ["z"] = 3 }
        };

        var result = caller.ValidateParamsAgainstSchema();

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("are not valid");
        result.ErrorMessage.Should().Contain("`x`, `y`, and `z`");
    }

    [TestMethod]
    public async Task ExecuteLocalFunctionAsync_ConvertsJsonElementPrimitivesAndCollections()
    {
        var caller = new ToolCaller(
            baseUrl: "tool://localhost",
            path: $"{typeof(DeepTools).FullName}.Combine",
            method: "POST",
            operation: "combine",
            methodSchema: ParseElement("""
                {
                  "parameters": [
                    { "name": "d", "in": "query", "required": true, "schema": { "type": "number" } },
                    { "name": "b", "in": "query", "required": true, "schema": { "type": "boolean" } },
                    { "name": "s", "in": "query", "required": true, "schema": { "type": "string" } },
                    { "name": "n", "in": "query", "required": true, "schema": { "type": "integer" } }
                  ]
                }
                """),
            contentType: "application/json",
            authHeaders: [],
            authQueryParams: [])
        {
            Params = new Dictionary<string, object>
            {
                ["d"] = ParseElement("1.5"),
                ["b"] = ParseElement("true"),
                ["s"] = ParseElement("\"hello\""),
                ["n"] = ParseElement("4")
            }
        };

        var result = await caller.ExecuteLocalFunctionAsync();

        result.Should().Be("1.5|True|hello|4");
    }

    [TestMethod]
    public async Task ExecuteLocalFunctionAsync_ConvertsEnumFromNumberAndList()
    {
        var enumCaller = new ToolCaller(
            baseUrl: "tool://localhost",
            path: $"{typeof(DeepTools).FullName}.Pick",
            method: "POST",
            operation: "pick",
            methodSchema: ParseElement("""
                { "parameters": [ { "name": "c", "in": "query", "required": true, "schema": { "type": "integer" } } ] }
                """),
            contentType: "application/json",
            authHeaders: [],
            authQueryParams: [])
        {
            Params = new Dictionary<string, object> { ["c"] = ParseElement("1") }
        };

        var listCaller = new ToolCaller(
            baseUrl: "tool://localhost",
            path: $"{typeof(DeepTools).FullName}.Sum",
            method: "POST",
            operation: "sum",
            methodSchema: ParseElement("""
                { "parameters": [ { "name": "nums", "in": "query", "required": true, "schema": { "type": "array" } } ] }
                """),
            contentType: "application/json",
            authHeaders: [],
            authQueryParams: [])
        {
            Params = new Dictionary<string, object> { ["nums"] = ParseElement("[1,2,3]") }
        };

        (await enumCaller.ExecuteLocalFunctionAsync()).Should().Be(DeepTools.Color.Green);
        (await listCaller.ExecuteLocalFunctionAsync()).Should().Be(6);
    }

    [TestMethod]
    public async Task ExecuteLocalFunctionAsync_ConvertsNonJsonElementEnumAndChangeType()
    {
        var caller = new ToolCaller(
            baseUrl: "tool://localhost",
            path: $"{typeof(DeepTools).FullName}.Pick",
            method: "POST",
            operation: "pick",
            methodSchema: ParseElement("""
                { "parameters": [ { "name": "c", "in": "query", "required": true, "schema": { "type": "string" } } ] }
                """),
            contentType: "application/json",
            authHeaders: [],
            authQueryParams: [])
        {
            // Plain string (not JsonElement) -> Enum.Parse path.
            Params = new Dictionary<string, object> { ["c"] = "Green" }
        };

        (await caller.ExecuteLocalFunctionAsync()).Should().Be(DeepTools.Color.Green);
    }

    [TestMethod]
    public async Task ExecuteLocalFunctionAsync_UnsupportedJsonElementTargetType_Throws()
    {
        var caller = new ToolCaller(
            baseUrl: "tool://localhost",
            path: $"{typeof(DeepTools).FullName}.TakesDate",
            method: "POST",
            operation: "takesDate",
            methodSchema: ParseElement("""
                { "parameters": [ { "name": "when", "in": "query", "required": true, "schema": { "type": "string" } } ] }
                """),
            contentType: "application/json",
            authHeaders: [],
            authQueryParams: [])
        {
            Params = new Dictionary<string, object> { ["when"] = ParseElement("\"2020-01-01\"") }
        };

        Func<Task> act = () => caller.ExecuteLocalFunctionAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Unsupported target type*");
    }

    [TestMethod]
    public async Task ExecuteLocalFunctionAsync_OptionalParameter_UsesDefaultValueWhenMissing()
    {
        var caller = new ToolCaller(
            baseUrl: "tool://localhost",
            path: $"{typeof(DeepTools).FullName}.Optional",
            method: "POST",
            operation: "optional",
            methodSchema: ParseElement("""
                { "parameters": [ { "name": "a", "in": "query", "required": true, "schema": { "type": "string" } } ] }
                """),
            contentType: "application/json",
            authHeaders: [],
            authQueryParams: [])
        {
            Params = new Dictionary<string, object> { ["a"] = "x" }
        };

        (await caller.ExecuteLocalFunctionAsync()).Should().Be("x3");
    }

    [TestMethod]
    public async Task ExecuteLocalFunctionAsync_IgnoresRuntimeInjectedCancellationTokenParameter()
    {
        var caller = new ToolCaller(
            baseUrl: "tool://localhost",
            path: $"{typeof(DeepTools).FullName}.WithCancellation",
            method: "POST",
            operation: "withCancellation",
            methodSchema: ParseElement("""
                {
                  "requestBody": {
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "object",
                          "properties": {
                            "value": { "type": "string" },
                            "cancellationToken": { "type": "string" }
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
            Params = new Dictionary<string, object>
            {
                ["value"] = "ok",
                ["cancellationToken"] = ""
            }
        };

        (await caller.ExecuteLocalFunctionAsync()).Should().Be("ok");
    }

    [TestMethod]
    public async Task ExecuteLocalFunctionAsync_IgnoresRuntimeInjectedContextParameter()
    {
        var methodPath = $"{typeof(SkillTools).FullName}.ListSkills";
        var caller = new ToolCaller(
            baseUrl: "tool://localhost",
            path: methodPath,
            method: "POST",
            operation: "skills_list",
            methodSchema: ParseElement("""
                {
                  "requestBody": {
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "object",
                          "properties": {},
                          "additionalProperties": false
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
            Params = new Dictionary<string, object>
            {
                ["context"] = new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
                ["assistantDefinition"] = new AssistantDefinition
                {
                    Id = Guid.NewGuid(),
                    Skills = []
                }
            }
        };

        var result = await caller.ExecuteLocalFunctionAsync();
        result.Should().NotBeNull();
        result!.ToString().Should().Be("[]");
    }

    [TestMethod]
    public void ValidateParamsAgainstSchema_IgnoresRuntimeInjectedCancellationTokenParameter()
    {
        var caller = new ToolCaller(
            baseUrl: "tool://localhost",
            path: $"{typeof(DeepTools).FullName}.WithCancellation",
            method: "POST",
            operation: "withCancellation",
            methodSchema: ParseElement("""
                {
                  "requestBody": {
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "object",
                          "properties": {
                            "value": { "type": "string" }
                          },
                          "required": ["value"]
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
            Params = new Dictionary<string, object>
            {
                ["value"] = "ok",
                ["cancellationToken"] = "stale-schema-leak"
            }
        };

        var (isValid, errorMessage) = caller.ValidateParamsAgainstSchema();

        isValid.Should().BeTrue();
        errorMessage.Should().BeNull();
    }

    [TestMethod]
    public void AddMissingRequiredParamsFromSchema_ExtractsNumberBoolValues()
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
                          "required": ["count", "enabled", "rate"],
                          "properties": {
                            "count": { "type": "integer", "default": 7 },
                            "enabled": { "type": "boolean", "default": true },
                            "rate": { "type": "number", "default": 1.25 }
                          }
                        }
                      }
                    }
                  }
                }
                """),
            contentType: "application/json",
            authHeaders: [],
            authQueryParams: []);

        caller.AddMissingRequiredParamsFromSchema();

        caller.Params!["count"].Should().Be(7L);
        caller.Params["enabled"].Should().Be(true);
        caller.Params["rate"].Should().Be(1.25);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }
        public string? LastBody { get; private set; }
        public string? LastContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            if (request.Content != null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
                LastContentType = request.Content.Headers.ContentType?.MediaType;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json")
            };
        }
    }

    private static class DeepTools
    {
        public enum Color { Red, Green }

        public static string Combine(double d, bool b, string s, int n) => $"{d}|{b}|{s}|{n}";

        public static Color Pick(Color c) => c;

        public static int Sum(List<int> nums) => nums.Sum();

        public static string Optional(string a, int n = 3) => $"{a}{n}";

        public static string WithCancellation(string value, CancellationToken cancellationToken = default) => value;

        public static string TakesDate(DateTime when) => when.ToString("o");
    }
}
