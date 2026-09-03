using System.IO;
using Microsoft.Extensions.Logging;

namespace Dsh.Wpf;

/// <summary>
/// Builds the application's logger factory. The desktop client has no DI host, so a
/// single file-backed factory is shared across all transport/service instances; logs
/// land next to the executable for post-hoc diagnosis.
/// </summary>
public static class Logging
{
    private static ILoggerFactory? _factory;

    /// <summary>The process-wide logger factory; created once, lazily.</summary>
    public static ILoggerFactory Factory => _factory ??= CreateFactory();

    /// <summary>A typed logger for the given category.</summary>
    public static ILogger<T> Get<T>() => Factory.CreateLogger<T>();

    /// <summary>Convenience static logger (category = caller) for one-off diagnostics.</summary>
    public static class Log
    {
        private static readonly ILogger _log = Factory.CreateLogger("Dsh.Wpf");
        public static void Info(string message) => _log.LogInformation("{M}", message);
        public static void Warn(string message) => _log.LogWarning("{M}", message);
        public static void Error(string message) => _log.LogError("{M}", message);
    }

    private static ILoggerFactory CreateFactory()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "dsh-client.log");

        // E3: default to Information so the file logger doesn't absorb every Debug trace (the
        // mux/WS loop and fold internals are chatty at Debug, which on a busy session wrote MBs
        // synchronously and could stall the calling thread). Set DSH_LOG_DEBUG=1 to get Debug
        // back for diagnosis; the decision is made once at first factory creation.
        var level = Environment.GetEnvironmentVariable("DSH_LOG_DEBUG") == "1"
            ? LogLevel.Debug
            : LogLevel.Information;

        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(level);
            builder.AddProvider(new FileLoggerProvider(path));
        });
    }
}

/// <summary>Minimal file logger: appends one line per entry; rotates by truncating at 5 MB.</summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;

    public FileLoggerProvider(string path) => _path = path;

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _path);

    public void Dispose()
    {
    }
}

internal sealed class FileLogger : ILogger
{
    private readonly string _category;

    /// <summary>All logger instances append to one shared file, so the writer is static and the
    /// path is captured once (the provider hands every logger the same path).</summary>
    private static string _path = "dsh-client.log";

    public FileLogger(string category, string path)
    {
        _category = category;
        _path = path;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    // ── Async write queue (2026-08-30 perf fix) ────────────────────────────────────────────
    // The previous implementation formatted AND wrote the line synchronously inside Log(), under
    // a lock, on the calling thread. Every logging call therefore did File.Exists + FileInfo +
    // File.AppendAllText (open/seek/append/close) on the UI thread. With instrumentation logging
    // during render this produced dozens of blocking disk round-trips per frame and froze the
    // window while scrolling. Formatting + I/O now happen on a single background writer, so a
    // logging call costs only a queue enqueue.
    private static readonly System.Collections.Concurrent.BlockingCollection<Func<string>> WriteQueue = new(new System.Collections.Concurrent.ConcurrentQueue<Func<string>>());
    private static int _writerStarted;
    private static DateTime _lastFlush = DateTime.UtcNow;

    private static void EnsureWriter()
    {
        if (Interlocked.Exchange(ref _writerStarted, 1) == 1) return;
        var thread = new Thread(() =>
        {
            // True batching: take one line (blocking), then drain everything else that is already
            // queued, and write the whole batch with a SINGLE file open/append/close. Previously
            // each line cost one open+seek+append+close, which is what made a chatty caller
            // (e.g. instrumentation during render) so expensive.
            var batch = new List<string>();
            foreach (var write in WriteQueue.GetConsumingEnumerable())
            {
                batch.Add(write());
                while (WriteQueue.TryTake(out var extra)) batch.Add(extra());

                FlushBatch(batch);
                batch.Clear();

                // Bounded coalescing window: let a burst accumulate a little before writing.
                Thread.Sleep(100);
            }
        })
        { IsBackground = true, Name = "Dsh.LogWriter" };
        thread.Start();
    }

    private static void FlushBatch(List<string> batch)
    {
        try
        {
            var path = _path;
            if (File.Exists(path) && new FileInfo(path).Length > 5 * 1024 * 1024)
            {
                // E3: rotate instead of truncate — keep the previous log as .1 so recent
                // history survives a rotation, then start a fresh file. (Deleting outright
                // threw away the evidence right before a crash.)
                string archive = path + ".1";
                try { if (File.Exists(archive)) File.Delete(archive); } catch { }
                try { File.Move(path, archive, overwrite: true); } catch { }
            }
            using var writer = new StreamWriter(path, append: true);
            foreach (var line in batch)
            {
                writer.WriteLine(line);
            }
        }
        catch
        {
            // Logging must never take down the app.
        }
        finally
        {
            _lastFlush = DateTime.UtcNow;
        }
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        // Format on the caller's thread (formatter captures state that may be pooled/mutated
        // right after the call returns), then hand the finished string to the writer thread.
        string line;
        try
        {
            line = $"{DateTimeOffset.Now:O} [{logLevel}] {_category}: {formatter(state, exception)}";
            if (exception is not null) line += $"{Environment.NewLine}{exception}";
        }
        catch
        {
            return; // Formatting must never throw into the caller.
        }

        EnsureWriter();
        // Bounded: if the writer somehow can't keep up, drop the line instead of growing memory
        // or blocking the UI. Logging is best-effort by contract.
        if (WriteQueue.Count > 10_000) return;
        WriteQueue.Add(() => line);
    }
}
