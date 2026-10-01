using System.Buffers;
using System.Globalization;
using System.Text;

namespace MediaFlow.Core.Pipelines;

/// <summary>A GStreamer fraction such as a framerate (30/1) or pixel aspect ratio.</summary>
public readonly record struct Fraction(int Numerator, int Denominator)
{
    public double Value => (double)Numerator / Denominator;
    public override string ToString() => $"{Numerator}/{Denominator}";
}

/// <summary>
/// Type-safe, testable builder for gst-launch style pipeline descriptions.
/// Instead of concatenating strings by hand (and fighting quoting bugs), you compose
/// elements, caps filters and tee branches, and the builder renders valid syntax.
/// </summary>
/// <example>
/// <code>
/// var description = PipelineDescription.Create()
///     .Element("videotestsrc", ("pattern", "ball"), ("is-live", true))
///     .Caps("video/x-raw", ("width", 1280), ("height", 720), ("framerate", new Fraction(30, 1)))
///     .Tee("split",
///         preview => preview.Queue().Element("autovideosink"),
///         archive => archive.Queue().Element("x264enc").Element("mp4mux").Element("filesink", ("location", "out.mp4")));
/// </code>
/// </example>
public sealed class PipelineDescription
{
    private static readonly SearchValues<char> NeedsQuoting = SearchValues.Create(" !\"'=,;()\\\t");
    private static readonly SearchValues<char> NeedsCapsQuoting = SearchValues.Create(" ,;=\"");

    private readonly List<string> _segments = [];
    private readonly List<string> _branches = [];

    private PipelineDescription() { }

    public static PipelineDescription Create() => new();

    /// <summary>Appends an element with optional properties (use ("name", "x") to name it).</summary>
    public PipelineDescription Element(string factory, params ReadOnlySpan<(string Key, object? Value)> properties)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(factory);
        var sb = new StringBuilder(factory);
        foreach (var (key, value) in properties)
        {
            if (value is null) continue;
            sb.Append(' ').Append(key).Append('=').Append(FormatValue(value));
        }
        _segments.Add(sb.ToString());
        return this;
    }

    /// <summary>Appends a caps filter, e.g. video/x-raw,width=1280,height=720.</summary>
    public PipelineDescription Caps(string mediaType, params ReadOnlySpan<(string Key, object? Value)> fields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        var sb = new StringBuilder(mediaType);
        foreach (var (key, value) in fields)
        {
            if (value is null) continue;
            sb.Append(',').Append(key).Append('=').Append(FormatCapsValue(value));
        }
        _segments.Add(sb.ToString());
        return this;
    }

    /// <summary>
    /// Appends a queue. A queue is a thread boundary in GStreamer: every branch of a tee
    /// needs one, otherwise one slow branch blocks all others (or deadlocks at preroll).
    /// A leaky queue drops old buffers instead of back-pressuring upstream (ideal for live analytics).
    /// </summary>
    public PipelineDescription Queue(bool leaky = false, int maxBuffers = 0, string? name = null) =>
        Element("queue",
            ("name", name),
            ("leaky", leaky ? "downstream" : null),
            ("max-size-buffers", maxBuffers > 0 ? maxBuffers : null),
            ("max-size-bytes", maxBuffers > 0 ? 0 : null),
            ("max-size-time", maxBuffers > 0 ? 0L : null));

    /// <summary>Appends a raw fragment (escape hatch for exotic syntax).</summary>
    public PipelineDescription Raw(string fragment)
    {
        _segments.Add(fragment);
        return this;
    }

    /// <summary>Splits the stream into N branches with a tee. Must be the last call on this chain.</summary>
    public PipelineDescription Tee(string name, params ReadOnlySpan<Action<PipelineDescription>> branches)
    {
        if (branches.Length == 0) throw new ArgumentException("A tee needs at least one branch.", nameof(branches));
        Element("tee", ("name", name), ("allow-not-linked", true));
        foreach (var configure in branches)
        {
            var branch = new PipelineDescription();
            configure(branch);
            _branches.Add($"{name}. ! {branch}");
        }
        return this;
    }

    /// <summary>Adds an independent chain to the same pipeline (e.g. audio next to video).</summary>
    public PipelineDescription Parallel(Action<PipelineDescription> configure)
    {
        var chain = new PipelineDescription();
        configure(chain);
        _branches.Add(chain.ToString());
        return this;
    }

    public override string ToString()
    {
        var main = string.Join(" ! ", _segments);
        return _branches.Count == 0 ? main : $"{main} {string.Join(' ', _branches)}";
    }

    internal static string FormatValue(object value) => value switch
    {
        bool b => b ? "true" : "false",
        string s => Quote(s),
        Fraction f => f.ToString(),
        Enum e => e.ToString().ToLowerInvariant(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => Quote(value.ToString() ?? string.Empty),
    };

    private static string FormatCapsValue(object value) => value switch
    {
        string s => s.AsSpan().ContainsAny(NeedsCapsQuoting) ? $"\"{s.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : s,
        _ => FormatValue(value),
    };

    private static string Quote(string s)
    {
        if (s.Length > 0 && !s.AsSpan().ContainsAny(NeedsQuoting)) return s;
        return $"\"{s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }
}
