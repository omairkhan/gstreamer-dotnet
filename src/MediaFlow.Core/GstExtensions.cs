using System.Globalization;

namespace MediaFlow.Core;

/// <summary>
/// C# 14 extension members that smooth over the raw GObject-Introspection surface.
/// (Extension blocks let us add properties as well as methods to types we do not own.)
/// </summary>
public static class GstExtensions
{
    extension(TimeSpan span)
    {
        /// <summary>GStreamer measures time in nanoseconds.</summary>
        public Gst.ClockTime ToClockTime() => new((ulong)span.ToNanoseconds());

        public long ToNanoseconds() => span.Ticks * TimeSpan.NanosecondsPerTick;
    }

    extension(long nanoseconds)
    {
        public TimeSpan NanosecondsToTimeSpan() => TimeSpan.FromTicks(nanoseconds / TimeSpan.NanosecondsPerTick);
    }

    extension(Gst.Structure structure)
    {
        public string? TryGetString(string field) => structure.HasField(field) ? structure.GetString(field) : null;

        public int? TryGetInt(string field) => structure.GetInt(field, out var value) ? value : null;

        public Pipelines.Fraction? TryGetFraction(string field) =>
            structure.GetFraction(field, out var n, out var d) && d != 0 ? new(n, d) : null;
    }

    extension(Gst.Element element)
    {
        /// <summary>
        /// Sets any GObject property from a CLR value using GStreamer's own deserialiser,
        /// so enums ("ball"), fractions ("30/1") and caps strings all work.
        /// </summary>
        public void Set(string property, object value) =>
            Gst.Functions.UtilSetObjectArg(element, property, value switch
            {
                bool b => b ? "true" : "false",
                Enum e => e.ToString().ToLowerInvariant(),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? string.Empty,
            });
    }

    extension(Gst.Buffer buffer)
    {
        /// <summary>Presentation timestamp (null when the buffer carries none).</summary>
        public TimeSpan? Pts
        {
            get
            {
                var pts = buffer.Handle.GetPts();
                return pts == Gst.Constants.CLOCK_TIME_NONE ? null : ((long)pts).NanosecondsToTimeSpan();
            }
            set => buffer.Handle.SetPts(value is { } v ? (ulong)v.ToNanoseconds() : Gst.Constants.CLOCK_TIME_NONE);
        }

        public TimeSpan? Duration
        {
            get
            {
                var duration = buffer.Handle.GetDuration();
                return duration == Gst.Constants.CLOCK_TIME_NONE ? null : ((long)duration).NanosecondsToTimeSpan();
            }
            set => buffer.Handle.SetDuration(value is { } v ? (ulong)v.ToNanoseconds() : Gst.Constants.CLOCK_TIME_NONE);
        }
    }
}
