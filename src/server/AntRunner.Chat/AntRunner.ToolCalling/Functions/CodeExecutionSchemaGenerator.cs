namespace AntRunner.ToolCalling.Functions;

/// <summary>
/// Generates OpenAPI schema for Python and Bash code execution tools (runPython, runBash, makeDiagram)
/// This is a temporary solution until the Docker script methods can be fully migrated to the registry system.
/// </summary>
public static class CodeExecutionSchemaGenerator
{
    public static string GetSchema()
    {
        var schema = new
        {
            openapi = "3.0.1",
            info = new { title = "Code Execution Tools", version = "v1" },
            servers = new[] { new { url = "tool://localhost" } },
            paths = new Dictionary<string, object>
            {
                ["GuideAntsApi.Services.NotebookDockerScriptService.ExecuteDockerScript"] = new
                {
                    python = BuildPythonMethod(),
                    bash = BuildBashMethod(),
                    plantumlScript = BuildDiagramMethod()
                }
            }
        };

        return JsonSerializer.Serialize(schema, new JsonSerializerOptions { WriteIndented = true });
    }

    private static object BuildPythonMethod() => new
    {
        tags = new[] { "GenericSandbox" },
        summary = "Execute python in a container",
        description = "Executes a specified python script",
        operationId = "runPython",
        parameters = Array.Empty<object>(),
        requestBody = BuildRequestBody(defaultScriptType: 2),
        responses = BuildResponses()
    };

    private static object BuildBashMethod() => new
    {
        tags = new[] { "GenericSandbox" },
        summary = "Execute bash in a container",
        description = "Executes a specified bash script",
        operationId = "runBash",
        parameters = Array.Empty<object>(),
        requestBody = BuildRequestBody(defaultScriptType: 0),
        responses = BuildResponses()
    };

    private static object BuildDiagramMethod() => new
    {
        tags = new[] { "GenericSandbox" },
        summary = "Execution of plantuml",
        description = "Executes a bash script to create a plantuml image in png format from a puml file",
        operationId = "makeDiagram",
        parameters = Array.Empty<object>(),
        requestBody = BuildRequestBody(defaultScriptType: 0, containerDefault: "plantuml"),
        responses = BuildResponses()
    };

    private static object BuildRequestBody(int defaultScriptType, string containerDefault = "guideants-ai") => new
    {
        required = true,
        description = "Script execution request payload.",
        content = new
        {
            application_json = new
            {
                schema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["script"] = new { type = "string", description = "The script to be executed." },
                        ["containerName"] = new { type = "string", description = containerDefault, @default = containerDefault },
                        ["scriptType"] = new { type = "number", description = defaultScriptType.ToString(), @default = defaultScriptType }
                    },
                    required = new[] { "script", "containerName", "scriptType" },
                    additionalProperties = false
                }
            }
        }
    };

    private static object BuildResponses() => new
    {
        _200 = new
        {
            description = "OK",
            content = new
            {
                application_json = new
                {
                    schema = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            ["standardOutput"] = new { type = "string", description = "Standard output" },
                            ["standardError"] = new { type = "string", description = "Standard error" }
                        },
                        required = new[] { "standardOutput", "standardError" },
                        additionalProperties = false
                    }
                }
            }
        }
    };
}
