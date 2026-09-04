using System.Threading;
using Dsh.App;
using Dsh.App.Services;
using Dsh.Client;
using Dsh.Contract.Frames;
using Dsh.Contract.Methods;
using Microsoft.Extensions.Logging;
using Application = System.Windows.Application;
using Log = Dsh.Wpf.Logging.Log;

namespace Dsh.Wpf;

/// <summary>
/// Shared connection scope for multi-window support (P1-14). Owns the single
/// <see cref="WpfApiClient"/>, the downstream dual-stream receive loop, the connection
/// generation and reconnect policy. <see cref="MainViewModel"/> instances subscribe to the
/// frame events instead of owning a client/streams, so multiple windows share one connection.
/// The scope stays alive while at least one subscriber is attached; when the last subscriber
/// detaches it stops the streams. State that is per-session/per-window (fold, approval, queue,
/// jobs, projections) stays in each <see cref="MainViewModel"/>.
/// </summary>
public sealed class ConnectionScope : IDisposable
{
    private static readonly Lazy<ConnectionScope> _instance = new(() => new ConnectionScope());
    public static ConnectionScope Instance => _instance.Value;

    private readonly object _gate = new();
    private readonly ReconnectPolicy _reconnect = new();
    private CancellationTokenSource? _streamCts;
    private int _subscriberCount;
    private volatile bool _started;
    private bool _attached;
    private WpfApiClient? _client;
    private string? _baseUrl;

    private ConnectionScope() { }

    /// <summary>
    /// Shared HTTP/WS client. Created by <see cref="SetBaseUrl"/> so a view model can read it
    /// immediately after configuring the URL (before any stream starts).
    /// </summary>
    public WpfApiClient Client => _client ?? throw new InvalidOperationException("ConnectionScope.SetBaseUrl not called yet.");

    /// <summary>Stateless session service over the shared client.</summary>
    public SessionService Sessions => _sessions ?? throw new InvalidOperationException("ConnectionScope.SetBaseUrl not called yet.");
    private SessionService? _sessions;

    /// <summary>True once the stream loop has been started and not fully stopped.</summary>
    public bool IsRunning => _started;

    /// <summary>
    /// True when the connection handshake has succeeded (P1-14 step 4). Updated by the loop so a
    /// newly opened secondary window can detect an already-established connection and pull its
    /// own tree instead of waiting for the next connect event.
    /// </summary>
    public bool IsConnected { get; private set; }

    /// <summary>Raised on the UI thread for every mux frame (already generation-filtered).</summary>
    public event Action<MuxFrame>? MuxFrameReceived;

    /// <summary>Raised on the UI thread for every host frame.</summary>
    public event Action<HostFrame>? HostFrameReceived;

    /// <summary>Raised when the connection drops / reconnects so every window's status bar updates.</summary>
    public event Action<bool>? ConnectionStateChanged;

    /// <summary>
    /// Configure the shared base URL and (on first call) create the single client/service. Must
    /// be called before any window reads <see cref="Client"/>/<see cref="Sessions"/>. Idempotent
    /// for the same URL (a second window with the same host URL is a no-op).
    /// </summary>
    public void SetBaseUrl(string baseUrl)
    {
        lock (_gate)
        {
            if (_client is not null)
            {
                _baseUrl = baseUrl.TrimEnd('/');
                return;
            }
            _baseUrl = baseUrl.TrimEnd('/');
            _client = new WpfApiClient(_baseUrl, logger: Logging.Get<WpfApiClient>());
            _sessions = new SessionService(_client);
        }
    }

    /// <summary>
    /// Mark the connection as established (P1-14 step 4). The view model calls this after its
    /// initial <c>host.describe</c> handshake succeeds, before attaching, so a secondary window
    /// opened later sees <see cref="IsConnected"/> already true and pulls its own tree.
    /// </summary>
    public void MarkConnected() => IsConnected = true;

    /// <summary>
    /// Attach a window/subscriber. Starts the stream loop on the first subscriber.
    /// B1: idempotent — a re-connect that calls Attach again while already attached does not
    /// inflate <see cref="_subscriberCount"/> (previously each ConnectAsync bumped the count and
    /// Detach was only called once at exit, so the count grew monotonically and the stream loop
    /// could never stop short of process exit).
    /// </summary>
    public bool Attach()
    {
        lock (_gate)
        {
            if (_attached) return true;
            _attached = true;
            _subscriberCount++;
            if (!_started)
            {
                _started = true;
                _ = Task.Run(() => RunLoopAsync());
            }
            return true;
        }
    }

    /// <summary>Detach a subscriber; stops the streams when the last one leaves.
    /// B1: only decrements when actually attached, so an exit-time Detach reliably reaches zero.</summary>
    public void Detach()
    {
        lock (_gate)
        {
            if (!_attached) return;
            _attached = false;
            _subscriberCount = Math.Max(0, _subscriberCount - 1);
            if (_subscriberCount == 0)
            {
                StopStreams();
                _started = false;
            }
        }
    }

    // -----------------------------------------------------------------------------------------
    // P1-fix (event-leak): the scope used to expose raw `event Action<T>` for the three UI-thread
    // signals. Subscribers could subscribe but the scope had no way to unsubscribe them on
    // window close — every disposed MainViewModel stayed reachable through the singleton and
    // continued processing frames in the background (memory leak + ghost VM mutation). These
    // Subscribe overloads return a disposable handle whose Dispose() unsubscribes atomically,
    // keeping the lifetime tied to the calling window/VM instead of the singleton.
    // -----------------------------------------------------------------------------------------
    public IDisposable Subscribe(Action<MuxFrame> handler)
    {
        MuxFrameReceived += handler;
        return new Subscription(() => MuxFrameReceived -= handler);
    }

    public IDisposable SubscribeHost(Action<HostFrame> handler)
    {
        HostFrameReceived += handler;
        return new Subscription(() => HostFrameReceived -= handler);
    }

    public IDisposable SubscribeConnection(Action<bool> handler)
    {
        ConnectionStateChanged += handler;
        return new Subscription(() => ConnectionStateChanged -= handler);
    }

    private sealed class Subscription : IDisposable
    {
        private Action? _unsubscribe;
        public Subscription(Action unsubscribe) { _unsubscribe = unsubscribe; }
        public void Dispose()
        {
            // Single-fire + null-check so double-Dispose is a no-op (safe with `using`).
            var u = System.Threading.Interlocked.Exchange(ref _unsubscribe, null);
            u?.Invoke();
        }
    }

    private void StopStreams()
    {
        var cts = _streamCts;
        if (cts is null) return;
        cts.Cancel();
        _streamCts = null;
    }

    /// <summary>The stream receive loop with reconnect backoff; runs on a background thread.</summary>
    private async Task RunLoopAsync()
    {
        string baseUrl = _baseUrl!;
        _streamCts = new CancellationTokenSource();
        var ct = _streamCts.Token;
        _reconnect.Reset();

        while (!ct.IsCancellationRequested)
        {
            int generation = Client.Generation.Advance();

            try
            {
                var muxTask = ReadMuxLoop(ct, generation, baseUrl);
                var hostTask = ReadHostLoop(ct, generation, baseUrl);
                var completed = await Task.WhenAny(muxTask, hostTask);
                await completed; // rethrow if the completed one faulted
                // B3: WhenAny only awaits the first-completed stream; the other is no longer
                // awaited. Observe its fault so an independent failure doesn't surface as an
                // unobserved task exception (which can crash the process). The outer catch
                // already handles the reconnect, so swallowing here is safe.
                var other = ReferenceEquals(completed, muxTask) ? hostTask : muxTask;
                _ = other.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                _streamCts?.Cancel();
                _streamCts = new CancellationTokenSource();
                ct = _streamCts.Token;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // One stream failed; fall through to reconnect.
            }

            if (ct.IsCancellationRequested) return;
            var delay = _reconnect.NextDelay();
            IsConnected = false; // P1-14: reflect the drop for newly-opened windows too.
            RaiseConnectionState(false); // dropped; every window reflects it.
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }

            // Re-handshake on reconnect; re-fetching tree/projections is each VM's job.
            try
            {
                await Client.Call<object>(RpcMethods.HostDescribe, new { });
                _reconnect.Reset();
                IsConnected = true;
                RaiseConnectionState(true); // reconnected.
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Handshake failed; the outer loop backs off and retries.
            }
        }
    }

    /// <summary>
    /// Raise <see cref="ConnectionStateChanged"/> on the UI thread. Called from the background
    /// stream loop; without marshalling, a window's handler would mutate observable collections
    /// off the UI thread and throw (the "无法加载会话内容" regression root cause).
    /// </summary>
    private void RaiseConnectionState(bool up)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            ConnectionStateChanged?.Invoke(up);
            return;
        }
        dispatcher.MarshalAsync(() => ConnectionStateChanged?.Invoke(up));
    }

    private async Task ReadMuxLoop(CancellationToken ct, int generation, string baseUrl)
    {
        try
        {
            Log.Info($"[Stream] ReadMuxLoop start (gen={generation}, url={baseUrl})");
            bool first = true;
            await foreach (var (_, frame) in DownstreamStreams.ReadMux(baseUrl, Logging.Get<ConnectionScope>(), ct))
            {
                if (first)
                {
                    Log.Info($"[Stream] ReadMuxLoop first frame arrived kind={frame?.GetType().Name}");
                    first = false;
                }
                if (generation != Client.Generation.Current) return;
                if (frame is null) continue;
                _ = Application.Current.Dispatcher.BeginInvoke(new Action(() => MuxFrameReceived?.Invoke(frame)));
            }
            Log.Info("[Stream] ReadMuxLoop ended (stream closed by host)");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[Stream] ReadMuxLoop failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task ReadHostLoop(CancellationToken ct, int generation, string baseUrl)
    {
        try
        {
            await foreach (var (_, frame) in DownstreamStreams.ReadHost(baseUrl, Logging.Get<ConnectionScope>(), ct))
            {
                if (generation != Client.Generation.Current) return;
                _ = Application.Current.Dispatcher.BeginInvoke(new Action(() => HostFrameReceived?.Invoke(frame)));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    /// <summary>Shared client URL is settable before the first window attaches.</summary>
    public void Dispose() => StopStreams();
}
