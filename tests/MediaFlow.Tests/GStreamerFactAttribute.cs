using System.Runtime.CompilerServices;
using MediaFlow.Core;

namespace MediaFlow.Tests;

/// <summary>A [Fact] that is skipped when the native GStreamer runtime is not available.</summary>
public sealed class GStreamerFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Unavailable = new(() =>
    {
        try
        {
            GstRuntime.EnsureInitialized();
            return null;
        }
        catch (Exception ex)
        {
            return $"GStreamer runtime not available: {ex.GetType().Name}";
        }
    });

    public GStreamerFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (Unavailable.Value is { } reason) Skip = reason;
    }
}
