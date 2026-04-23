using AntRunner.ToolCalling.Functions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Models;

namespace GuideAntsApi.Endpoints;

public static class SandboxEndpoints
{
    private static readonly string[] sourceArray = ["Bash", "PowerShell", "Python"];

    public static void MapSandboxEndpoints(this WebApplication app)
    {
        // Generic sandbox endpoint
        // The optional {id} parameter at the end of the path serves no functional purpose.
        // It exists solely to support multiple identical endpoint registrations in external systems
        // that require unique paths for the same logical endpoint.
        app.MapPost("/sandbox/run/{containerName}/{scriptType}/{id?}", async (
            [FromBody] ScriptExecutionRequest request,
            [FromRoute] string containerName,
            [FromRoute] ScriptType scriptType,
            [FromRoute] int? id = null) =>
        {
            if (request?.Script == null)
            {
                return Results.BadRequest("Script cannot be null.");
            }

            var result = await DockerScriptService.ExecuteDockerScript(
                request.Script,
                containerName,
                scriptType);

            return Results.Ok(result);
        })
        .WithTags("GenericSandbox")
        .WithOpenApi(operation =>
        {
            operation.Parameters[1].Schema = new OpenApiSchema
            {
                Type = "string",
                Enum = [.. sourceArray.Select(v => new Microsoft.OpenApi.Any.OpenApiString(v) as Microsoft.OpenApi.Any.IOpenApiAny)]
            };
            return operation;
        })
        .Produces<ScriptExecutionResult>(StatusCodes.Status200OK, "text/plain")
        .Produces<ScriptExecutionResult>(StatusCodes.Status200OK, "application/json")
        .Produces<ScriptExecutionResult>(StatusCodes.Status200OK, "text/json")
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized);
    }

}

public class ScriptExecutionRequest
{
    public string? Script { get; set; }
}
