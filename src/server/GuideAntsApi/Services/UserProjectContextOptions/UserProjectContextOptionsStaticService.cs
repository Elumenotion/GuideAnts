using System.Text.Json.Serialization;
using AntRunner.ToolCalling;
using AntRunner.ToolCalling.Attributes;

namespace GuideAntsApi.Services.UserProjectContextOptions
{
    public static class UserProjectContextOptionsStaticService
    {
        private static IServiceProvider? _staticServiceProvider;

        public static void InitializeServiceProvider(IServiceProvider serviceProvider)
            => _staticServiceProvider = serviceProvider;

        [Tool(
            OperationId = "set_user_context_options",
            Summary = "Set user context options"
        )]
        [RequiresNotebookContext]
        public static async Task<object> SetUserContextOptions(
            [Parameter(Description = "List of context option items to set")] List<ContextOptionItem> requestBody,
            [Parameter(Description = "Invocation context", Hidden = true)] InvocationContext? context = null)
        {
            using var scope = _staticServiceProvider!.CreateScope();
            var impl = scope.ServiceProvider.GetRequiredService<IUserProjectContextOptionsService>();
            
            var keyValuePairs = requestBody.Select(opt => 
                new KeyValuePair<string, string>(opt.Key, opt.Value));
                
            if (context?.ProjectId == null)
            {
                throw new InvalidOperationException("Project ID is required.");
            }
            
            await impl.UpsertAsync(
                Guid.Empty,
                context.ProjectId,
                keyValuePairs);
            return new { status = "ok" };
        }
    }

    public class ContextOptionItem
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;
        
        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;
    }
}

