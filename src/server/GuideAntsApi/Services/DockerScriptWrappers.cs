using AntRunner.ToolCalling.Attributes;
using GuideAntsApi.Services;

namespace AntRunner.ToolCalling.Functions;

/// <summary>
/// Wrapper methods for Docker script execution that provide different operation IDs
/// for the same underlying ExecuteDockerScript method. This follows the recasting pattern
/// where multiple tool operations map to a single implementation method.
/// </summary>
public static class DockerScriptWrappers
{
    /// <summary>
    /// Executes a Python script in a Docker container.
    /// </summary>
    [Tool(
        OperationId = "run_python",
        Summary = "Execute python in a container"
    )]
    [RequiresNotebookContext]
    public static async Task<object> RunPython(
        [Parameter(Description = "The script to be executed.")]
        string script,
        
        [Parameter(Description = "guideants-ai")]
        string containerName = "guideants-ai",
        
        [Parameter(Description = "2", Hidden = true)]
        int scriptType = 2,
        
        [Parameter(Description = "Invocation context", Hidden = true)]
        InvocationContext? context = null)
    {
        return await CallDockerScriptService(script, containerName, scriptType, context);
    }

    /// <summary>
    /// Executes a Bash script in a Docker container.
    /// </summary>
    [Tool(
        OperationId = "run_bash",
        Summary = "Execute bash in a container"
    )]
    [RequiresNotebookContext]
    public static async Task<object> RunBash(
        [Parameter(Description = "The script to be executed.")]
        string script,
        
        [Parameter(Description = "guideants-ai")]
        string containerName = "guideants-ai",
        
        [Parameter(Description = "0", Hidden = true)]
        int scriptType = 0,
        
        [Parameter(Description = "Invocation context", Hidden = true)]
        InvocationContext? context = null)
    {
        return await CallDockerScriptService(script, containerName, scriptType, context);
    }

    /// <summary>
    /// Creates a PlantUML diagram by executing a bash script.
    /// </summary>
    [Tool(
        OperationId = "make_diagram",
        Summary = "Execution of plantuml"
    )]
    [RequiresNotebookContext]
    public static async Task<object> MakeDiagram(
        [Parameter(Description = "A here document, followed by execution of plantuml")]
        string script,
        
        [Parameter(Description = "plantuml")]
        string containerName = "plantuml",
        
        [Parameter(Description = "0", Hidden = true)]
        int scriptType = 0,
        
        [Parameter(Description = "Invocation context", Hidden = true)]
        InvocationContext? context = null)
    {
        // Validate that the script begins with a here document for PlantUML file creation
        var trimmedScript = script.TrimStart();
        if (!trimmedScript.StartsWith("cat << 'EOF' >") && !trimmedScript.StartsWith("cat << \"EOF\" >") && !trimmedScript.StartsWith("cat << EOF >"))
        {
            return "Invalid script format. The script must BEGIN with a here document using the format:\n\n" +
                   "cat << 'EOF' > filename.puml\n" +
                   "@startuml\n" +
                   "!theme mars\n" +
                   "' Your PlantUML syntax here\n" +
                   "@enduml\n" +
                   "EOF\n\n" +
                   "plantuml filename.puml\n\n" +
                   "Please revise your script to begin with the here document and try again.";
        }

        var result = await CallDockerScriptService(script, containerName, scriptType, context);

        var standardErrorProperty = result?.GetType().GetProperty("StandardError");
        var standardErrorObject = standardErrorProperty?.GetValue(result);
        var standardErrorText = standardErrorObject?.ToString();

        if (!string.IsNullOrEmpty(standardErrorText))
        {
            return $"There were one or more issues with your tool call and diagram creation FAILED!\n\nThe error was `{standardErrorText}`.\nCorrect the error and try again";
        }

        return result ?? "No output";
    }

    /// <summary>
    /// Helper method to call the actual Docker script service directly.
    /// </summary>
    private static async Task<object> CallDockerScriptService(string script, string containerName, int scriptType, InvocationContext? context)
    {
        var scriptTypeEnum = (ScriptType)scriptType;
        return await NotebookDockerScriptService.ExecuteDockerScript(script, containerName, scriptTypeEnum, context);
    }
}

