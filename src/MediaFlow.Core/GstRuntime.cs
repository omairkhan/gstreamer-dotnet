namespace MediaFlow.Core;

/// <summary>
/// One-time, thread-safe bootstrap of the native GStreamer runtime.
/// GStreamer must be initialised exactly once per process before any element is created.
/// </summary>
public static class GstRuntime
{
    private static readonly Lock Gate = new();
    private static bool _initialized;

    /// <summary>Initialises GirCore's native bindings and GStreamer itself (idempotent).</summary>
    public static void EnsureInitialized()
    {
        if (Volatile.Read(ref _initialized)) return;

        lock (Gate)
        {
            if (_initialized) return;

            Gst.Module.Initialize();
            GstApp.Module.Initialize();

            var args = Array.Empty<string>();
            Gst.Functions.Init(ref args);

            Volatile.Write(ref _initialized, true);
        }
    }

    /// <summary>e.g. "GStreamer 1.24.2".</summary>
    public static string Version
    {
        get
        {
            EnsureInitialized();
            return Gst.Functions.VersionString();
        }
    }

    /// <summary>Returns true when an element factory (plugin feature) is installed.</summary>
    public static bool HasElement(string factoryName)
    {
        EnsureInitialized();
        using var factory = Gst.ElementFactory.Find(factoryName);
        return factory is not null;
    }

    /// <summary>Checks a set of required elements and returns the ones that are missing.</summary>
    public static IReadOnlyList<string> FindMissing(params ReadOnlySpan<string> factoryNames)
    {
        var missing = new List<string>();
        foreach (var name in factoryNames)
        {
            if (!HasElement(name)) missing.Add(name);
        }
        return missing;
    }

    /// <summary>Fails fast with a helpful message when a pipeline needs plugins that are not installed.</summary>
    public static void Require(params ReadOnlySpan<string> factoryNames)
    {
        var missing = FindMissing(factoryNames);
        if (missing.Count > 0)
        {
            throw new MissingGstPluginException(missing);
        }
    }
}

public sealed class MissingGstPluginException(IReadOnlyList<string> missing)
    : InvalidOperationException(
        $"Missing GStreamer element(s): {string.Join(", ", missing)}. " +
        "Install the matching gstreamer1.0-plugins-* packages (see README).")
{
    public IReadOnlyList<string> Missing { get; } = missing;
}
