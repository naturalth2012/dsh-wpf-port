namespace Dsh.App;

partial class SessionFold
{
    /// <summary>
    /// Lightweight streaming telemetry (A2/A3 monitoring hook): counts chunks and cumulative
    /// AppendChunk time so the UI can decide whether to enable the deferred optimizations
    /// (A2 incremental-append for O(n²) text rebuilds; A3 frame-coalescing at high frequency).
    /// A <see cref="System.Diagnostics.Stopwatch"/> is started/stopped around AppendChunk and
    /// the deltas folded into <see cref="StreamingStats"/>.
    /// </summary>
    public sealed record StreamingStats
    {
        /// <summary>Chunks (text/reasoning deltas) folded since the last reset.</summary>
        public long ChunkCount { get; init; }

        /// <summary>Cumulative AppendChunk wall-clock time (µs) since the last reset.</summary>
        public long AppendMicros { get; init; }

        /// <summary>Peak Rows.Count observed since the last reset.</summary>
        public int PeakRows { get; init; }

        /// <summary>Average µs per chunk (0 when no chunks).</summary>
        public double AvgAppendMicros => ChunkCount == 0 ? 0 : (double)AppendMicros / ChunkCount;
    }
}
