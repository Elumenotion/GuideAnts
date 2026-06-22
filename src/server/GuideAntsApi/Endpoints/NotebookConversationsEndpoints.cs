using Microsoft.AspNetCore.Mvc;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Models;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.DataModel;
using GuideAntsApi.Utils;
using GuideAntsApi.Services.SystemGuide;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace GuideAntsApi.Endpoints;

public static class NotebookConversationsEndpoints
{
    public static void MapNotebookConversationsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}/notebooks/{notebookId:guid}/conversations")
            .WithTags("NotebookConversations")
            .RequireAuthorization("RequireApprovedUser")
            .WithSystemProjectAccessGuard();

        // List
        group.MapGet("/", async ([FromServices] IConversationService svc, Guid notebookId) =>
        {
            var list = await svc.GetListAsync(notebookId);
            return Results.Ok(list);
        })
        .Produces<IReadOnlyList<NotebookConversationListDto>>(StatusCodes.Status200OK);

        // Get single conversation with messages
        group.MapGet("/{convoId:guid}", async ([FromServices] IConversationService svc, Guid convoId) =>
        {
            var conversation = await svc.GetConversationWithMessagesAsync(convoId);
            return conversation == null ? Results.NotFound() : Results.Ok(conversation);
        })
        .Produces<NotebookConversationWithMessagesDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        // Create
        group.MapPost("/", async ([FromServices] IConversationService svc, Guid notebookId, [FromBody] CreateConversationRequest req) =>
        {
            var dto = await svc.CreateConversationAsync(notebookId, req.Title);
            return Results.Ok(dto);
        })
        .RequireAuthorization("RequireContributor")
        .Produces<NotebookConversationListDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status402PaymentRequired);

        // Rename
        group.MapPut("/{convoId:guid}", async ([FromServices] IConversationService svc, Guid convoId, [FromBody] RenameConversationRequest req) =>
        {
            await svc.RenameConversationAsync(convoId, req.Title);
            return Results.NoContent();
        })
        .RequireAuthorization("RequireContributor")
        .Produces(StatusCodes.Status204NoContent);

        // Generate and set conversation title using Conversation Title Generator assistant
        group.MapPost("/{convoId:guid}/title/generate", async (
            [FromServices] IConversationService svc,
            [FromServices] IServiceScopeFactory scopeFactory,
            Guid convoId) =>
        {
            // 1) Load conversation with full context
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            
            var conv = await db.NotebookConversations
                .Include(c => c.Notebook)
                    .ThenInclude(n => n.Project)
                .Include(c => c.Messages)
                .FirstOrDefaultAsync(c => c.Id == convoId);
                
            if (conv == null)
                return Results.NotFound();
            
            var notebookId = conv.NotebookId;
            var projectId = conv.Notebook.ProjectId;
            
            // Verify project access

// 2) Build conversation dialog text (user and assistant messages only)
            var sb = new System.Text.StringBuilder();
            foreach (var m in conv.Messages.OrderBy(m => m.TurnIndex).ThenBy(m => m.MessageSequence))
            {
                if (string.IsNullOrWhiteSpace(m.Content)) continue;
                if (m.Role != ChatRole.User && m.Role != ChatRole.Assistant) continue;
                var role = m.Role == ChatRole.User ? "User" : "Assistant";
                sb.Append(role).Append(": ").AppendLine(m.Content.Trim());
            }
            var dialogText = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(dialogText))
                dialogText = "(No substantive messages in this conversation.)";
            
            // 3) Create InvocationContext with all required IDs
            var context = new AntRunner.ToolCalling.InvocationContext(
                ProjectId: projectId,
                NotebookId: notebookId,
                ConversationId: convoId);
            
            // 4) Use Agent.Invoke with proper context
            var instructions = "Create a concise 4–8 word, title case, single-line title without punctuation.\n\nConversation:\n" + dialogText;
            var result = await AntRunner.Chat.Agent.Invoke("Conversation Title Generator", instructions, context);
            
            // 5) Sanitize and clamp to DB constraints (nvarchar(255))
            var title = (result?.StandardOutput ?? string.Empty).Trim().Trim('\"').Trim();
            if (title.EndsWith('.') || title.EndsWith('!') || title.EndsWith('?'))
            {
                title = title.TrimEnd('.', '!', '?').TrimEnd();
            }
            if (title.Length > 255)
            {
                title = title.Substring(0, 255).Trim();
            }

            // 6) Persist only if we have a non-empty title; otherwise keep the existing one
            if (!string.IsNullOrWhiteSpace(title))
            {
                await svc.RenameConversationAsync(convoId, title);
            }
            else
            {
                title = conv.Title;
            }

            // 8) Return the final title
            return Results.Ok(new { title });
        })
        .RequireAuthorization("RequireContributor")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .WithSummary("Generate and set a conversation title using the Conversation Title Generator assistant");

        // Delete
        group.MapDelete("/{convoId:guid}", async ([FromServices] IConversationService svc, Guid convoId) =>
        {
            await svc.DeleteConversationAsync(convoId);
            return Results.NoContent();
        })
        .RequireAuthorization("RequireContributor")
        .Produces(StatusCodes.Status204NoContent);

        // POST send message (requires SSE via Accept: text/event-stream)
        group.MapPost("/{convoId:guid}/messages", async (
            HttpContext ctx, 
            [FromServices] IConversationService service, 
            [FromServices] GuideAntsApi.Services.LlamaCpp.INotebookModelRuntimeService runtimeService,
            [FromServices] ApplicationDbContext dbContext,
            Guid notebookId, 
            Guid convoId, 
            [FromBody] SendMessageRequest request) =>
        {
            var accept = ctx.Request.Headers["Accept"].ToString();

            if (!accept.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "This endpoint requires 'Accept: text/event-stream' header for streaming responses." });
            }

            // Determine target assistant ID if name is provided
            Guid? targetAssistantId = null;
            if (!string.IsNullOrEmpty(request.AssistantName))
            {
                var assistant = await dbContext.Assistants
                    .Where(a => a.Name == request.AssistantName)
                    .FirstOrDefaultAsync(CancellationToken.None);
                if (assistant != null)
                {
                    targetAssistantId = assistant.Id;
                }
            }

            // Preflight local runtime readiness (do not link to client disconnect; streaming setup must still run)
            var runtimeStatus = await runtimeService.GetRuntimeStatusAsync(notebookId, targetAssistantId, CancellationToken.None);
            if (runtimeStatus.State != "ready" && runtimeStatus.RequiredModels.Any(m => m.RuntimeConfig != null))
            {
                // Auto-reload on model switching: if the required local llama
                // model is not loaded, start (or join) the notebook runtime load
                // operation and wait for completion before streaming.
                var loadOperation = await runtimeService
                    .StartLoadOperationAsync(notebookId, targetAssistantId, CancellationToken.None);

                if (!string.Equals(loadOperation.State, "ready", StringComparison.OrdinalIgnoreCase))
                {
                    var timeoutAt = DateTime.UtcNow.AddMinutes(15);
                    while (DateTime.UtcNow < timeoutAt)
                    {
                        var current = await runtimeService.GetOperationStatusAsync(
                            notebookId,
                            loadOperation.OperationId,
                            CancellationToken.None);

                        if (current is not null)
                        {
                            loadOperation = current;
                        }

                        if (string.Equals(loadOperation.State, "ready", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }

                        if (IsTerminalFailedState(loadOperation.State))
                        {
                            return Results.Conflict(new
                            {
                                error = "Local model load failed.",
                                runtimeStatus,
                                operation = loadOperation
                            });
                        }

                        await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
                    }
                }

                runtimeStatus = await runtimeService.GetRuntimeStatusAsync(notebookId, targetAssistantId, CancellationToken.None);
                if (runtimeStatus.State != "ready")
                {
                    return Results.Conflict(new
                    {
                        error = "Local models are not ready.",
                        runtimeStatus,
                        operation = loadOperation
                    });
                }
            }

            ctx.Response.Headers["Content-Type"] = "text/event-stream";

            try
            {
                await foreach (var ev in service.SendMessageStreamToConversationAsync(convoId, request, ctx.RequestAborted))
                {
                    await ctx.Response.WriteSseEventAsync(ev.EventType, ev.Payload, ctx.RequestAborted);
                }
            }
            catch (UnauthorizedAccessException)
            {
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await ctx.Response.WriteAsJsonAsync(new { error = "User does not have access to this project" });
                }
            }
            catch (KeyNotFoundException)
            {
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Conversation not found" });
                }
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Conversation is locked by", StringComparison.OrdinalIgnoreCase))
            {
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                    await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            }
            catch (ToolOAuthReconnectRequiredException ex)
            {
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                    await ctx.Response.WriteAsJsonAsync(new
                    {
                        code = "OAUTH_RECONNECT_REQUIRED",
                        message = "Reconnect required for one or more OAuth providers.",
                        providers = ex.ProviderIds
                    });
                }
            }

            return Results.Empty;
        })
        .RequireAuthorization("RequireContributor")
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status409Conflict)
        .Produces(StatusCodes.Status402PaymentRequired);

        // PATCH edit message
        group.MapPatch("/{convoId:guid}/messages/{messageId:guid}", async ([FromServices] IConversationService service, Guid convoId, Guid messageId, [FromBody] EditMessageRequest request) =>
        {
            try
            {
                await service.EditMessageAsync(messageId, request.Content);
                return Results.NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        })
        .RequireAuthorization("RequireContributor")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status402PaymentRequired)
        .Produces(StatusCodes.Status404NotFound);

        // DELETE undo last
        group.MapDelete("/{convoId:guid}/messages/last", async ([FromServices] IConversationService service, Guid notebookId, Guid convoId) =>
        {
            try
            {
                await service.UndoLastForConversationAsync(convoId);
                return Results.NoContent();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = "Conversation not found" });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Conversation is locked by", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Conflict(new { error = ex.Message });
            }
        })
        .RequireAuthorization("RequireContributor")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict)
        .Produces(StatusCodes.Status402PaymentRequired);

        // DELETE undo specific
        group.MapDelete("/{convoId:guid}/messages/{messageId:guid}", async ([FromServices] IConversationService service, Guid notebookId, Guid convoId, Guid messageId) =>
        {
            try
            {
                await service.UndoForConversationAsync(convoId, messageId);
                return Results.NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Conversation is locked by", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Conflict(new { error = ex.Message });
            }
        })
        .RequireAuthorization("RequireContributor")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict)
        .Produces(StatusCodes.Status402PaymentRequired);

        // POST save conversation as markdown file
        group.MapPost("/{convoId:guid}/save-as", async (
            Guid projectId,
            Guid notebookId,
            Guid convoId,
            [FromServices] IConversationService conversationService,
            [FromServices] INotebookFileService notebookFileService) =>
        {
            try
            {
                // Get conversation with messages
                var conversation = await conversationService.GetConversationWithMessagesAsync(convoId);
                if (conversation == null)
                {
                    return Results.NotFound(new { message = "Conversation not found." });
                }

                // Convert to markdown
                var markdownContent = ConvertConversationToMarkdown(conversation);
                
                // Convert any absolute API URLs to relative paths (safety check - services should have already done this)
                markdownContent = MarkdownUrlConverter.ConvertAbsoluteToRelative(markdownContent);
                
                // Adjust paths to be relative to conversations/ folder where markdown file is saved
                markdownContent = AdjustPathsForMarkdownLocation(markdownContent);

                // Generate safe filename from conversation title
                var safeFileName = SanitizeFileName(conversation.Title);
                var fileName = $"{safeFileName}.md";
                var relativePath = $"conversations/{fileName}";

                // Save file using NotebookFileService
                var fileDto = await notebookFileService.CreateTextFileAsync(
                    projectId,
                    notebookId,
                    relativePath,
                    markdownContent
                );

                return Results.Created(
                    $"/api/projects/{projectId}/notebooks/{notebookId}/files/content?path={Uri.EscapeDataString(fileDto.RelativePath)}",
                    fileDto
                );
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Unauthorized();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500
                );
            }
        })
        .WithName("SaveConversationAsMarkdown")
        .RequireAuthorization("RequireContributor")
        .Produces<NotebookFileDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status400BadRequest);
    }

    public record CreateConversationRequest(string Title);
    public record RenameConversationRequest(string Title);
    public record EditMessageRequest(string Content);

    private static bool IsTerminalFailedState(string? state)
    {
        return string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "invalid", StringComparison.OrdinalIgnoreCase);
    }

    private static string ConvertConversationToMarkdown(NotebookConversationWithMessagesDto conversation)
    {
        var sb = new System.Text.StringBuilder();
        
        // Header
        sb.AppendLine($"# {conversation.Title}");
        sb.AppendLine();
        sb.AppendLine($"**Created:** {conversation.Created:yyyy-MM-dd HH:mm:ss UTC}");
        if (conversation.LastActivity.HasValue)
        {
            sb.AppendLine($"**Last Activity:** {conversation.LastActivity.Value:yyyy-MM-dd HH:mm:ss UTC}");
        }
        if (!string.IsNullOrEmpty(conversation.AssistantName))
        {
            sb.AppendLine($"**Assistant:** {conversation.AssistantName}");
        }
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // Messages (already filtered by GetConversationWithMessagesAsync to remove duplicates)
        foreach (var message in conversation.Messages)
        {
            var roleLabel = message.Role switch
            {
                ChatRole.User => "**User**",
                ChatRole.Assistant => $"**Assistant**{(string.IsNullOrEmpty(message.AssistantName) ? "" : $" ({message.AssistantName})")}",
                ChatRole.System => "**System**",
                ChatRole.Tool => "**Tool**",
                _ => $"**{message.Role}**"
            };

            sb.AppendLine(roleLabel);
            sb.AppendLine();
            sb.AppendLine(message.Content);
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "conversation";
        }

        // Remove invalid characters
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", fileName.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
        
        // Remove leading/trailing spaces and dots
        sanitized = sanitized.Trim().TrimEnd('.');
        
        // Limit length
        if (sanitized.Length > 100)
        {
            sanitized = sanitized.Substring(0, 100);
        }

        // Ensure not empty
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "conversation";
        }

        return sanitized;
    }

    /// <summary>
    /// Adjusts relative paths in markdown content to be relative to the conversations/ folder.
    /// Paths like ./Output/chart.png become ../Output/chart.png when markdown is in conversations/ folder.
    /// </summary>
    private static string AdjustPathsForMarkdownLocation(string markdownContent)
    {
        if (string.IsNullOrEmpty(markdownContent))
            return markdownContent;

        // Pattern to match markdown links/images: ![alt](path) or [text](path)
        var mdPattern = new Regex(@"(!?\[[^\]]*\]\()(\.?\.?/?)([^)]+)(\))", RegexOptions.Compiled);
        
        // Pattern to match HTML src/href attributes: src="path" or href="path"
        var htmlPattern = new Regex(@"((?:src|href)\s*=\s*['""])(\.?\.?/?)([^'""]+)(['""])", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        var result = markdownContent;

        // Adjust markdown links/images
        result = mdPattern.Replace(result, match =>
        {
            var linkPart = match.Groups[1].Value;  // ![alt]( or [text](
            var prefix = match.Groups[2].Value;     // ./ or ../ or empty
            var path = match.Groups[3].Value;       // The actual path
            var closing = match.Groups[4].Value;    // )

            // Skip if it's an absolute URL, external link, or special protocol
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("#", StringComparison.OrdinalIgnoreCase))
            {
                return match.Value;
            }

            // Remove ./ or ../ prefix if present
            var cleanPath = path;
            if (prefix.StartsWith("./", StringComparison.Ordinal))
            {
                cleanPath = path;
            }
            else if (prefix.StartsWith("../", StringComparison.Ordinal))
            {
                cleanPath = path;
            }
            else if (prefix == ".")
            {
                cleanPath = path;
            }

            // Prepend ../ to make path relative to conversations/ folder
            var adjustedPath = cleanPath.StartsWith("../") ? cleanPath : $"../{cleanPath}";

            return $"{linkPart}{adjustedPath}{closing}";
        });

        // Adjust HTML src/href attributes
        result = htmlPattern.Replace(result, match =>
        {
            var attrPart = match.Groups[1].Value;   // src=" or href="
            var prefix = match.Groups[2].Value;     // ./ or ../ or empty
            var path = match.Groups[3].Value;       // The actual path
            var quote = match.Groups[4].Value;      // " or '

            // Skip if it's an absolute URL or special protocol
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return match.Value;
            }

            // Remove ./ or ../ prefix if present
            var cleanPath = path;
            if (prefix.StartsWith("./", StringComparison.Ordinal))
            {
                cleanPath = path;
            }
            else if (prefix.StartsWith("../", StringComparison.Ordinal))
            {
                cleanPath = path;
            }

            // Prepend ../ to make path relative to conversations/ folder
            var adjustedPath = cleanPath.StartsWith("../") ? cleanPath : $"../{cleanPath}";

            return $"{attrPart}{adjustedPath}{quote}";
        });

        return result;
    }
} 

