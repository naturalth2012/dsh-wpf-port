using System.Diagnostics;
using System.IO;
using System.Threading;
using Dsh.Client;
using Dsh.Contract.Methods;

namespace Dsh.App.Services;

/// <summary>
/// Detects whether the DeepSeek Harness host service is up, and—when it is not—launches it
/// from a user-selected checkout. Mirrors the "start the backend" onboarding a first-time
/// user needs: pick the <c>deepseek-harness</c> directory, run <c>pnpm dsh web</c> in the
/// background, and poll until the loopback gateway (<c>host.describe</c>) answers.
/// </summary>
public sealed class HarnessLauncher
{
    /// <summary>Default loopback gateway base URL of <c>pnpm dsh web</c>.</summary>
    public const string DefaultBaseUrl = "http://127.0.0.1:3080";

    /// <summary>Exception types that are expected when interacting with a Process object
    /// (the process may have exited, been disposed, or the OS may reject the operation).</summary>
    private static readonly Type[] _expectedProcessExceptions =
    [
        typeof(InvalidOperationException),
        typeof(System.ComponentModel.Win32Exception),
        typeof(NotSupportedException),
        typeof(AggregateException),
    ];

    private readonly string _baseUrl;
    private readonly TimeSpan _unaryTimeout;

    public HarnessLauncher(string baseUrl = DefaultBaseUrl, TimeSpan? unaryTimeout = null)
    {
        _baseUrl = baseUrl;
        _unaryTimeout = unaryTimeout ?? TimeSpan.FromSeconds(3);
    }

    /// <summary>True when the host gateway answers <c>host.describe</c> (i.e. the service is up).</summary>
    public async Task<bool> IsRunningAsync(CancellationToken ct = default)
    {
        try
        {
            // A throwaway client with a short timeout: if the gateway is down the TCP
            // connect fails fast and IsRunningAsync returns false without a long wait.
            var client = new WpfApiClient(_baseUrl, unaryTimeout: _unaryTimeout);
            await client.Call<object>(RpcMethods.HostDescribe, null, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller cancelled the probe (shutdown / user abort). Propagate it instead of
            // reporting "not running", which would wrongly trigger the launch guide.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                     or OperationCanceledException or TimeoutException
                                     or System.Net.WebSockets.WebSocketException
                                     or System.Text.Json.JsonException)
        {
            // Gateway unreachable or not answering → report "not running" so the UI shows the
            // launch guide. (OperationCanceledException here means the client's own timeout,
            // not a caller cancellation — also just "not running".)
            System.Diagnostics.Debug.WriteLine($"[HarnessLauncher] probe failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Environment variable holding an explicit <c>deepseek-harness</c> checkout path. When set,
    /// it is probed first so each developer can point the launcher at their own checkout without
    /// any machine-specific paths being baked into the source.
    /// </summary>
    public const string HarnessDirEnv = "DSH_HARNESS_DIR";

    /// <summary>
    /// Probe for a <c>deepseek-harness</c> checkout and return the first that looks like one
    /// (has a <c>pnpm-workspace.yaml</c>). Resolution order:
    /// <list type="number">
    ///   <item>The directory named by the <c>DSH_HARNESS_DIR</c> environment variable (if set).</item>
    ///   <item><c>~/deepseek-harness</c> (user profile).</item>
    ///   <item><c>~/dsh/deepseek-harness</c> (user profile).</item>
    /// </list>
    /// Returns <c>null</c> when none is found, in which case the user is prompted to pick a
    /// directory in the service-management panel. No machine-specific absolute paths are used.
    /// </summary>
    public static string? TryLocateDefaultDirectory()
    {
        var candidates = new List<string>();

        string? envDir = Environment.GetEnvironmentVariable(HarnessDirEnv);
        if (!string.IsNullOrWhiteSpace(envDir)) candidates.Add(envDir!);

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(profile, "deepseek-harness"));
        candidates.Add(Path.Combine(profile, "dsh", "deepseek-harness"));

        foreach (var dir in candidates)
        {
            if (LooksLikeHarnessCheckout(dir)) return dir;
        }
        return null;
    }

    /// <summary>Heuristic: the directory contains <c>pnpm-workspace.yaml</c> (harness is a pnpm monorepo).</summary>
    public static bool LooksLikeHarnessCheckout(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;
        return File.Exists(Path.Combine(directory, "pnpm-workspace.yaml"));
    }

    /// <summary>
    /// True when the checkout has not been built yet — i.e. the web client bundle
    /// (<c>apps/web/dist</c>) is missing. <c>pnpm dsh web</c> fails with a
    /// <c>MissingClientBundleError</c> until <c>pnpm run build</c> produces it, so callers
    /// should run <see cref="InitializeAsync"/> first when this returns true.
    /// </summary>
    /// <remarks>
    /// Older drafts of this check used <c>apps/web-dist</c> (with a hyphen) which is not a
    /// real path in the checkout — the dist directory is produced under <c>apps/web/dist</c>
    /// by Vite. We also accept the legacy name if it ever appears so a user with a stale
    /// checkout isn't shown a "build missing" status twice.
    /// </remarks>
    public static bool NeedsBuild(string? directory)
    {
        if (!LooksLikeHarnessCheckout(directory)) return false;
        string root = directory!;
        bool modernBuilt = Directory.Exists(Path.Combine(root, "apps", "web", "dist"));
        bool legacyBuilt = Directory.Exists(Path.Combine(root, "apps", "web-dist"));
        return !(modernBuilt || legacyBuilt);
    }

    /// <summary>
    /// Environment variable that lets the lefthook installer override a user-owned
    /// <c>core.hooksPath</c>. Without it, <c>pnpm install</c> aborts when the user's git
    /// config already manages hooks — which would make initialization fail on a fresh checkout.
    /// </summary>
    public const string LefthookOverrideEnv = "DSH_LEFTHOOK_ALLOW_HOOKS_PATH_OVERRIDE";

    /// <summary>
    /// Run the one-time backend preparation for a harness checkout: <c>pnpm install</c> then
    /// <c>pnpm run build</c>. Each output line is delivered to <paramref name="onOutput"/>.
    /// Returns true when both steps succeed (the web client bundle now exists). Non-blocking:
    /// stream both stdout/stderr and pump them so large pnpm output cannot deadlock the child.
    /// </summary>
    /// <exception cref="ArgumentException">when <paramref name="directory"/> is not a checkout.</exception>
    public async Task<bool> InitializeAsync(string directory, Action<string>? onOutput = null, CancellationToken ct = default)
    {
        if (!LooksLikeHarnessCheckout(directory))
            throw new ArgumentException("Not a deepseek-harness checkout", nameof(directory));

        bool installOk = await RunStepAsync(directory, "install", null, onOutput, ct).ConfigureAwait(false);
        if (!installOk) return false;

        bool buildOk = await RunStepAsync(directory, "run", "build", onOutput, ct).ConfigureAwait(false);
        return buildOk && !NeedsBuild(directory);
    }

    /// <summary>
    /// Run a single pnpm step (<c>pnpm &lt;args…&gt;</c>) in the checkout, streaming output.
    /// Returns false on a non-zero exit code or when pnpm itself cannot be located.
    /// </summary>
    private async Task<bool> RunStepAsync(string directory, string arg, string? extraArg = null, Action<string>? onOutput = null, CancellationToken ct = default)
    {
        string shim = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "pnpm.cmd");
        bool hasShim = File.Exists(shim);

        var psi = new ProcessStartInfo
        {
            FileName = hasShim ? shim : "pnpm",
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(arg);
        if (extraArg is not null) psi.ArgumentList.Add(extraArg);
        // Allow the lefthook postinstall to take over a user-owned git hooksPath instead of
        // aborting the install on a fresh checkout.
        psi.EnvironmentVariables[LefthookOverrideEnv] = "1";

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new List<string>();
        var stderr = new List<string>();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.Add(e.Data); onOutput?.Invoke(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.Add(e.Data); onOutput?.Invoke(e.Data); } };

        if (!TryStart(process, hasShim)) return false;

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Kill the whole pnpm tree if the caller cancels (e.g. user hits 取消). Without this
        // the child would keep running detached while WaitForExitAsync throws on the token.
        using var reg = ct.Register(() =>
        {
            try { KillProcessTree(process); }
            catch (Exception ex) when (_expectedProcessExceptions.Contains(ex.GetType()))
            {
                // Process already exited.
                System.Diagnostics.Debug.WriteLine($"[HarnessLauncher] kill tree skipped: {ex.Message}");
            }
        });

        // Mirror the shell's exit-code propagation so the caller sees the real result.
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode == 0;
    }

    /// <summary>
    /// Best-effort termination of a process and its descendants. pnpm spawns child node
    /// processes; killing only the parent leaves orphans that keep holding the port.
    /// </summary>
    private static void KillProcessTree(Process process)
    {
        if (process.HasExited) return;
        try { process.Kill(true); }
        catch (Exception ex) when (_expectedProcessExceptions.Contains(ex.GetType()))
        {
            // Access denied / already gone.
            System.Diagnostics.Debug.WriteLine($"[HarnessLauncher] kill skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// Start pnpm, falling back from the explicit .cmd shim to a shell-resolved lookup.
    /// Returns false when no pnpm could be launched at all.
    /// </summary>
    private static bool TryStart(Process process, bool hasShim)
    {
        try
        {
            return process.Start();
        }
        catch (System.ComponentModel.Win32Exception) when (!hasShim)
        {
            // .cmd shim absent and pnpm not on PATH — nothing we can launch.
            return false;
        }
    }

    /// <summary>
    /// Start <c>pnpm dsh web</c> in the given checkout, with stdout/stderr redirected so the
    /// caller can surface progress and failures. Each output line (stdout and stderr) is
    /// delivered to <paramref name="onOutput"/> asynchronously; consuming the streams also
    /// prevents the pipe buffer from blocking the child. Returns the launched process; null
    /// when the directory is not a harness checkout or pnpm could not be located.
    /// </summary>
    public Process? Launch(string directory, Action<string>? onOutput = null)
    {
        if (!LooksLikeHarnessCheckout(directory)) return null;

        var psi = new ProcessStartInfo
        {
            FileName = "pnpm",
            ArgumentList = { "dsh", "web" },
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // `pnpm` resolves via pnpm.cmd on Windows; without shell execution we must name it
        // explicitly so the .cmd shim is located.
        psi.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "pnpm.cmd");
        if (!File.Exists(psi.FileName))
        {
            // pnpm.cmd shim not found — fall back to shell execution (no redirected output).
            psi.FileName = "pnpm";
            psi.UseShellExecute = true;
            psi.RedirectStandardOutput = false;
            psi.RedirectStandardError = false;
        }

        var process = new Process { StartInfo = psi };
        process.Start();

        if (onOutput is not null && psi.RedirectStandardOutput)
        {
            process.OutputDataReceived += (_, e) => OnData(e, onOutput);
            process.ErrorDataReceived += (_, e) => OnData(e, onOutput);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        return process;
    }

    private static void OnData(DataReceivedEventArgs e, Action<string> onOutput)
    {
        if (e.Data is not null) onOutput(e.Data);
    }

    /// <summary>
    /// Poll the gateway until <see cref="IsRunningAsync"/> is true or the timeout elapses.
    /// </summary>
    public async Task<bool> WaitUntilReadyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await IsRunningAsync(ct).ConfigureAwait(false)) return true;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        return await IsRunningAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Describe the current service state for status display (1): whether the gateway
    /// answers <c>host.describe</c>, and the PID of the process listening on the gateway
    /// port (0 = none). The PID comes from <c>netstat</c>, so it works even when the service
    /// was started outside this app.
    /// </summary>
    public async Task<HarnessStatus> DescribeStatusAsync(CancellationToken ct = default)
    {
        bool running = await IsRunningAsync(ct).ConfigureAwait(false);
        int listenerPid = FindListenerPid();
        return new HarnessStatus(running, listenerPid);
    }

    /// <summary>
    /// Stop the harness backend (2). Terminates the process we launched (whole tree, with a
    /// force-kill fallback) and, if the gateway still answers afterwards (the service was
    /// started externally), kills whatever process is listening on the gateway port.
    /// </summary>
    public void Stop(Process? launched, TimeSpan waitForExit = default)
    {
        if (waitForExit == default) waitForExit = TimeSpan.FromSeconds(5);

        if (launched is not null)
        {
            TryKillTree(launched, waitForExit);
            launched.Dispose();
        }

        // External process: kill whatever holds the gateway port.
        int pid = FindListenerPid();
        if (pid > 0)
        {
            TryKillPid(pid);
        }
    }

    private static void TryKillTree(Process process, TimeSpan wait)
    {
        try
        {
            if (process.HasExited) return;
            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit((int)wait.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (_expectedProcessExceptions.Contains(ex.GetType()))
        {
            // Process already exited or access denied; the gateway check decides success.
            System.Diagnostics.Debug.WriteLine($"[HarnessLauncher] stop failed: {ex.Message}");
        }
    }

    private static void TryKillPid(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (_expectedProcessExceptions.Contains(ex.GetType()) || ex is ArgumentException)
        {
            // Already gone or access denied. (ArgumentException: GetProcessById found no process.)
            System.Diagnostics.Debug.WriteLine($"[HarnessLauncher] kill pid failed: {ex.Message}");
        }
    }

    /// <summary>PID of the process listening on the gateway port via <c>netstat</c>, or 0.</summary>
    public int FindListenerPid()
    {
        try
        {
            var uri = new Uri(_baseUrl);
            int port = uri.Port;
            var psi = new ProcessStartInfo
            {
                FileName = "netstat",
                ArgumentList = { "-ano" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return 0;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();

            foreach (string line in output.Split('\n'))
            {
                // "  TCP    0.0.0.0:3080    0.0.0.0:0    LISTENING    12345"
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5 &&
                    parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase) &&
                    parts[1].EndsWith($":{port}", StringComparison.Ordinal) &&
                    parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(parts[4], out int pid))
                {
                    return pid;
                }
            }
        }
        catch (Exception ex) when (_expectedProcessExceptions.Contains(ex.GetType()) || ex is ArgumentException or FormatException)
        {
            // netstat missing or parsing failed; report no listener.
            System.Diagnostics.Debug.WriteLine($"[HarnessLauncher] netstat probe failed: {ex.Message}");
        }
        return 0;
    }
}

/// <summary>Snapshot of the harness backend state for the status bar.</summary>
public readonly record struct HarnessStatus(bool Running, int ListenerPid);
