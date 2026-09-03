using System.Text.Json;
using Dsh.App;
using Xunit;

namespace Dsh.App.Tests;

public class SessionFoldExtractTests
{
    [Fact]
    public void Extract_two_locations_returns_two_paths()
    {
        var view = JsonDocument.Parse(
            """{"card":"generic","kind":"edit","locations":[{"path":"/a/one.ts"},{"path":"/a/two.ts"}]}"""
        ).RootElement;
        var paths = SessionFold.ExtractProducedPaths(view);
        Assert.Equal(2, paths.Length);
        Assert.Equal("/a/one.ts", paths[0]);
        Assert.Equal("/a/two.ts", paths[1]);
    }
}
