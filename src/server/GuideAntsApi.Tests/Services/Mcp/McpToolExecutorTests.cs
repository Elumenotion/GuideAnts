using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.Services.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GuideAntsApi.Tests.Services.Mcp;

[TestClass]
public sealed class McpToolExecutorTests
{
    [TestMethod]
    public async Task StreamableHttp_CallToolAsync_ReturnPolicy_HappyPath()
    {
        await using var server = await TestMcpHttpServer.StartAsync(request =>
        {
            request.Name.Should().Be("return_policy");
            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = "Returns are accepted within 30 days of purchase.",
                    },
                ],
            };
        });

        var result = await McpStreamableHttpToolClient.CallToolAsync(
            new Uri(server.EndpointUrl),
            new Dictionary<string, string>(),
            "return_policy",
            new Dictionary<string, object>(),
            TimeSpan.FromSeconds(10),
            CancellationToken.None);

        McpCallToolResultFormatter.Format(result)
            .Should().Be("Returns are accepted within 30 days of purchase.");
    }

    [TestMethod]
    public async Task StreamableHttp_CallToolAsync_TimesOut_PerCall()
    {
        await using var server = await TestMcpHttpServer.StartAsync(
            _ => new CallToolResult { Content = [new TextContentBlock { Text = "late" }] },
            callDelay: TimeSpan.FromSeconds(3));

        Func<Task> act = () => McpStreamableHttpToolClient.CallToolAsync(
            new Uri(server.EndpointUrl),
            new Dictionary<string, string>(),
            "return_policy",
            null,
            TimeSpan.FromMilliseconds(200),
            CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [TestMethod]
    public void McpApi_ActionType_IsNotClientHandled_SoTurnDoesNotPauseForClient()
    {
        var scheme = new Uri("mcp+api://worm").Scheme;
        scheme.Should().Be("mcp+api");

        AntRunner.ToolCalling.Functions.ActionType.McpApi
            .Should().NotBe(AntRunner.ToolCalling.Functions.ActionType.ClientHandled);
    }

    private sealed class TestMcpHttpServer : IAsyncDisposable
    {
        private WebApplication? _app;

        public string EndpointUrl { get; private set; } = string.Empty;

        public static async Task<TestMcpHttpServer> StartAsync(
            Func<CallToolRequestParams, CallToolResult> handler,
            TimeSpan? callDelay = null)
        {
            var instance = new TestMcpHttpServer();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
            builder.Services.AddMcpServer()
                .WithHttpTransport(options => options.Stateless = true)
                .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
                {
                    Tools =
                    [
                        new Tool
                        {
                            Name = "return_policy",
                            InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement,
                        },
                    ],
                }))
                .WithCallToolHandler(async (context, cancellationToken) =>
                {
                    if (callDelay.HasValue)
                    {
                        await Task.Delay(callDelay.Value, cancellationToken);
                    }

                    return handler(context.Params ?? new CallToolRequestParams { Name = "return_policy" });
                });

            instance._app = builder.Build();
            instance._app.MapMcp("/mcp");
            await instance._app.StartAsync();
            instance.EndpointUrl = instance._app.Urls.First().TrimEnd('/') + "/mcp";
            return instance;
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is not null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
            }
        }
    }
}
