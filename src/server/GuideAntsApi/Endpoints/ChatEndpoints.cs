using System.Security.Claims;
using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using GuideAntsApi.Services.Routing;
using Microsoft.AspNetCore.Mvc;

namespace GuideAntsApi.Endpoints;

public static class ChatEndpoints
{
    private sealed class LogCategory;

    public static void MapChatEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/chat")
            .WithTags("ChatRunThread")
            .WithOpenApi();

        group.MapPost("/run/{assistantName}", async (
            ClaimsPrincipal user,
            string assistantName,
            IChatCompletionClientFactory chatClientFactory,
            IChatModelResolver chatModelResolver,
            ILogger<LogCategory> logger,
            [FromBody] ChatRunOptions chatRunOptions) =>
        {
            if (string.IsNullOrWhiteSpace(assistantName))
            {
                return Results.BadRequest("Assistant name is required");
            }

            if (chatRunOptions == null)
            {
                return Results.BadRequest("Request body is required");
            }

            if (string.IsNullOrWhiteSpace(chatRunOptions.Instructions))
            {
                return Results.BadRequest("The request has no instructions");
            }

            chatRunOptions.AssistantName = assistantName;

            try
            {
                var assistantDef = await AssistantUtility.GetAssistantCreateRequest(assistantName);
                var resolved = chatModelResolver.Resolve(chatRunOptions.DeploymentId ?? assistantDef?.Model);
                chatRunOptions.DeploymentId = resolved.ModelId;
                if (!chatRunOptions.OverrideTemperature.HasValue)
                {
                    chatRunOptions.OverrideTemperature = resolved.OverrideTemperature;
                }
                if (!chatRunOptions.OverrideTopP.HasValue)
                {
                    chatRunOptions.OverrideTopP = resolved.OverrideTopP;
                }
                if (string.IsNullOrWhiteSpace(chatRunOptions.OverrideReasoningEffort))
                {
                    chatRunOptions.OverrideReasoningEffort = resolved.OverrideReasoningEffort;
                }

                var output = await ChatRunner.RunThread(chatRunOptions, chatClientFactory);
                if (output != null)
                {
                    return Results.Ok(output.Dialog);
                }

                return Results.BadRequest("Unable to process request");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Chat run failed for assistant {AssistantName} using deployment {DeploymentId}",
                    assistantName,
                    chatRunOptions.DeploymentId ?? "(unset)");
                return Results.StatusCode(500);
            }
        })
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status500InternalServerError);
    }
} 
