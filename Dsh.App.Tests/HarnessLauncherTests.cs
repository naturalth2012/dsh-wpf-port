using System.IO;
using Dsh.App.Services;
using Xunit;

namespace Dsh.App.Tests;

/// <summary>
/// Unit tests for the pure heuristics of <see cref="HarnessLauncher"/>: checkout detection and
/// default-directory location. Network/process behavior (IsRunningAsync/Launch/WaitUntilReady)
/// is exercised end-to-end against a real harness checkout instead.
/// </summary>
public sealed class HarnessLauncherTests
{
    [Fact]
    public void LooksLikeHarnessCheckout_false_for_missing_directory()
    {
        Assert.False(HarnessLauncher.LooksLikeHarnessCheckout(null));
        Assert.False(HarnessLauncher.LooksLikeHarnessCheckout(@"C:\nonexistent\path"));
        Assert.False(HarnessLauncher.LooksLikeHarnessCheckout(""));
    }

    [Fact]
    public void LooksLikeHarnessCheckout_true_only_with_pnpm_workspace_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), "harness-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // No pnpm-workspace.yaml → not a harness checkout.
            Assert.False(HarnessLauncher.LooksLikeHarnessCheckout(dir));

            // With the marker file it is.
            File.WriteAllText(Path.Combine(dir, "pnpm-workspace.yaml"), "packages:\n  - packages/*\n");
            Assert.True(HarnessLauncher.LooksLikeHarnessCheckout(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryLocateDefaultDirectory_finds_real_checkout()
    {
        string? located = HarnessLauncher.TryLocateDefaultDirectory();

        // Default probe paths are environment-driven (DSH_HARNESS_DIR) or relative to the
        // user profile (~/deepseek-harness), so they may not exist in every environment.
        if (located is null)
        {
            // Not an assertion failure: just means neither default probe path exists here.
            return;
        }

        Assert.True(HarnessLauncher.LooksLikeHarnessCheckout(located));
    }

    [Fact]
    public void NeedsBuild_false_for_non_checkout()
    {
        Assert.False(HarnessLauncher.NeedsBuild(null));
        Assert.False(HarnessLauncher.NeedsBuild(""));
        Assert.False(HarnessLauncher.NeedsBuild(@"C:\nonexistent\path"));
    }

    [Fact]
    public void NeedsBuild_true_when_web_dist_missing_false_once_present()
    {
        // The real bundle path produced by `pnpm run build` is apps/web/dist/ (Vite output of
        // @deepseek/host-web). Earlier drafts of this check used apps/web-dist (a hyphen)
        // which was a hallucinated path; the modern detector is apps/web/dist/, while the
        // legacy apps/web-dist/ is still accepted as a back-compat fallback so stale checkouts
        // aren't re-initialised.
        string dir = Path.Combine(Path.GetTempPath(), "harness-needsbuild-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "pnpm-workspace.yaml"), "packages:\n  - packages/*\n");
        try
        {
            // Fresh checkout: web client bundle not built yet.
            Assert.True(HarnessLauncher.NeedsBuild(dir));

            // The actual modern path makes NeedsBuild false.
            Directory.CreateDirectory(Path.Combine(dir, "apps", "web", "dist"));
            Assert.False(HarnessLauncher.NeedsBuild(dir));

            // Removing the modern path leaves nothing, so NeedsBuild becomes true again.
            Directory.Delete(Path.Combine(dir, "apps", "web", "dist"), recursive: true);
            Assert.True(HarnessLauncher.NeedsBuild(dir));

            // The legacy apps/web-dist still counts as "built" so we don't re-init a stale
            // checkout. (This is compatibility for users who already ran an older build that
            // produced that directory by accident or via a future code path; harmless either way.)
            Directory.CreateDirectory(Path.Combine(dir, "apps", "web-dist"));
            Assert.False(HarnessLauncher.NeedsBuild(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_throws_on_non_checkout()
    {
        var launcher = new HarnessLauncher();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            launcher.InitializeAsync(@"C:\nonexistent\path"));
    }

    [Fact]
    public async Task InitializeAsync_returns_false_when_pnpm_unavailable()
    {
        // No real pnpm on PATH in CI/headless: the launcher cannot run install/build.
        // It must report failure (false) rather than throw, so the UI can surface guidance.
        string dir = Path.Combine(Path.GetTempPath(), "harness-init-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "pnpm-workspace.yaml"), "packages:\n  - packages/*\n");
        try
        {
            var launcher = new HarnessLauncher();
            bool ok = await launcher.InitializeAsync(dir, _ => { }, CancellationToken.None);
            Assert.False(ok);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
