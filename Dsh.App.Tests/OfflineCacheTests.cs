using System.IO;
using Dsh.App.Services;
using Dsh.Contract.Methods;
using Xunit;

namespace Dsh.App.Tests;

/// <summary>
/// P2-11 regression tests: the offline cache round-trips the last successful workspace +
/// session list so the sidebar can be browsed without a live host connection.
/// </summary>
public class OfflineCacheTests
{
    private static string TempPath([System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        Path.Combine(Path.GetTempPath(), $"dsh-offline-{name}-{Guid.NewGuid():N}.json");

    [Fact]
    public void Save_And_TryLoad_RoundTrips_Snapshot()
    {
        var file = TempPath();
        try
        {
            var ws = new WorkspaceView
            {
                WorkspaceId = "w1",
                Path = "/repo",
                Title = "repo",
                SessionIds = new[] { "s1", "s2" },
                CreatedAt = "t0",
                UpdatedAt = "t1",
            };
            var sessions = new[]
            {
                new SessionSummary
                {
                    SessionId = "s1",
                    UpdatedAt = 100,
                    Running = true,
                    Blank = false,
                    Cwd = "/repo",
                },
                new SessionSummary
                {
                    SessionId = "s2",
                    UpdatedAt = 200,
                    Running = false,
                    Blank = false,
                    Cwd = "/repo",
                },
            };

            OfflineCache.Save(new[] { ws }, sessions, file);
            var loaded = OfflineCache.TryLoad(file);

            Assert.NotNull(loaded);
            Assert.Single(loaded.Workspaces);
            Assert.Equal(2, loaded.Sessions.Length);
            Assert.Equal("w1", loaded.Workspaces[0].WorkspaceId);
            Assert.Equal("s1", loaded.Sessions[0].SessionId);
            Assert.Equal(200, loaded.Sessions[1].UpdatedAt);
            Assert.True(loaded.SavedAt > 0);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public void Save_With_Empty_Lists_Still_RoundTrips()
    {
        var file = TempPath();
        try
        {
            OfflineCache.Save([], [], file);
            var loaded = OfflineCache.TryLoad(file);
            Assert.NotNull(loaded);
            Assert.Empty(loaded.Workspaces);
            Assert.Empty(loaded.Sessions);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }
}
