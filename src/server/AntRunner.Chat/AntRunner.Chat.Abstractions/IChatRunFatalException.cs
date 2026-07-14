namespace AntRunner.Chat.Abstractions;

/// <summary>
/// Marks a failure that must terminate the complete chat run, including any parent run that
/// invoked the failing run as a tool. Tool execution must not convert these failures into output.
/// </summary>
public interface IChatRunFatalException
{
}
