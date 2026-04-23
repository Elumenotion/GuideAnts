using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Diagnostics;
using System.Data.Common;

namespace GuideAntsApi.Extensions;

public static class LoggingExtensions
{
    /// <summary>
    /// Enhanced EF Core event logging that includes stack trace information
    /// </summary>
    public static void LogEfWarningWithSource(this ILogger logger, EventId eventId, string message)
    {
        if (!logger.IsEnabled(LogLevel.Warning))
            return;

        var stackTrace = new StackTrace(true);
        var callingFrame = FindRelevantStackFrame(stackTrace);
        
        var sourceInfo = callingFrame != null 
            ? $" [Source: {callingFrame.GetMethod()?.DeclaringType?.Name}.{callingFrame.GetMethod()?.Name} at line {callingFrame.GetFileLineNumber()}]"
            : "";

        logger.LogWarning(eventId, "{Message}{SourceInfo}", message, sourceInfo);
        
        // In development, also log the full stack trace for debugging
        #if DEBUG
        if (callingFrame != null)
        {
            logger.LogDebug("Full stack trace for EF warning:\n{StackTrace}", 
                string.Join("\n", stackTrace.GetFrames()
                    .Take(10) // Limit to first 10 frames to avoid clutter
                    .Select(f => $"  at {f.GetMethod()?.DeclaringType?.Name}.{f.GetMethod()?.Name} in {f.GetFileName()}:line {f.GetFileLineNumber()}")
                    .Where(s => !string.IsNullOrEmpty(s))));
        }
        #endif
    }

    private static StackFrame? FindRelevantStackFrame(StackTrace stackTrace)
    {
        var frames = stackTrace.GetFrames();
        if (frames == null) return null;

        // Skip EF Core internal frames and find the first application frame
        return frames.FirstOrDefault(frame =>
        {
            var method = frame.GetMethod();
            if (method?.DeclaringType == null) return false;
            
            var typeName = method.DeclaringType.FullName ?? "";
            
            // Skip EF Core internal types
            if (typeName.StartsWith("Microsoft.EntityFrameworkCore")) return false;
            if (typeName.StartsWith("Microsoft.Extensions.Logging")) return false;
            if (typeName.StartsWith("System.")) return false;
            
            // Include application types
            return typeName.StartsWith("GuideAntsApi");
        });
    }
}

/// <summary>
/// Custom EF Core logging interceptor to capture query warnings with source information
/// </summary>
public class EfQueryWarningInterceptor : IDbCommandInterceptor
{
    private readonly ILogger<EfQueryWarningInterceptor> _logger;

    public EfQueryWarningInterceptor(ILogger<EfQueryWarningInterceptor> logger)
    {
        _logger = logger;
    }

    public DbDataReader ReaderExecuted(
        DbCommand command, 
        CommandExecutedEventData eventData, 
        DbDataReader result)
    {
        LogQueryWithSource(command);
        return result;
    }

    public ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, 
        CommandExecutedEventData eventData, 
        DbDataReader result, 
        CancellationToken cancellationToken = default)
    {
        LogQueryWithSource(command);
        return ValueTask.FromResult(result);
    }

    private void LogQueryWithSource(DbCommand command)
    {
        // Check if this is a potentially problematic query
        var sql = command.CommandText;
        
        // Don't flag TOP(1) queries - they don't need ORDER BY for deterministic results
        var hasTakeWithoutOrder = sql.Contains("TOP(") && 
                                 !sql.Contains("ORDER BY") && 
                                 !sql.Contains("TOP(1)");
        
        var hasMultipleJoins = sql.Split("JOIN", StringSplitOptions.RemoveEmptyEntries).Length > 3;

        if (hasTakeWithoutOrder || hasMultipleJoins)
        {
            var stackTrace = new StackTrace(true);
            var relevantFrame = FindRelevantApplicationFrame(stackTrace);

            if (relevantFrame != null)
            {
                var problemType = hasTakeWithoutOrder ? "TOP without ORDER BY" : "Multiple JOINs";
                
                _logger.LogWarning("EF Query Issue: {ProblemType} detected in {MethodName} ({FileName}:{LineNumber})",
                    problemType,
                    $"{relevantFrame.GetMethod()?.DeclaringType?.Name}.{relevantFrame.GetMethod()?.Name}",
                    Path.GetFileName(relevantFrame.GetFileName()) ?? "Unknown",
                    relevantFrame.GetFileLineNumber());
            }
        }
    }

    private static StackFrame? FindRelevantApplicationFrame(StackTrace stackTrace)
    {
        var frames = stackTrace.GetFrames();
        if (frames == null) return null;

        // Skip interceptor, EF Core internal frames, and system frames to find the actual application code
        return frames.FirstOrDefault(frame =>
        {
            var method = frame.GetMethod();
            if (method?.DeclaringType == null) return false;
            
            var typeName = method.DeclaringType.FullName ?? "";
            
            // Skip interceptor frames
            if (typeName.Contains("EfQueryWarningInterceptor")) return false;
            
            // Skip EF Core internal types
            if (typeName.StartsWith("Microsoft.EntityFrameworkCore")) return false;
            if (typeName.StartsWith("Microsoft.Extensions.Logging")) return false;
            if (typeName.StartsWith("Microsoft.Extensions.DependencyInjection")) return false;
            if (typeName.StartsWith("System.")) return false;
            if (typeName.StartsWith("Castle.DynamicProxy")) return false; // Skip proxy frames
            
            // Include application types
            return typeName.StartsWith("GuideAntsApi");
        });
    }
}