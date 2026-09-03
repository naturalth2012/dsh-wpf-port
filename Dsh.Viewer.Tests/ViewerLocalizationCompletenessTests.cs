using Dsh.App.Services;

namespace Dsh.Viewer.Tests;

/// <summary>
/// i18n regression guard (P2-8 / 多语言): every Viewer.* key added to the shared Dsh.App resx
/// set must resolve to a non-empty string in every supported culture (zh-CN + en/fr/de/es/ko),
/// exactly like LocalizationCompletenessTests does for the main client keys. Mutates the shared
/// culture but always restores zh-CN in a finally; the assembly disables xunit parallelization so
/// other tests (which assume the default zh-CN labels) are never observed mid-switch.
/// </summary>
public class ViewerLocalizationCompletenessTests
{
    private static readonly string[] Keys =
    {
        "Viewer.Title",
        "Viewer.Toolbar.Session",
        "Viewer.Toolbar.RowsUnit",
        "Viewer.Role.User",
        "Viewer.Role.Assistant",
        "Viewer.Role.Tool",
        "Viewer.Role.Error",
        "Viewer.Role.Turn",
        "Viewer.Reasoning",
        "Viewer.Tool.Args",
        "Viewer.Tool.Result",
        "Viewer.Tool.ResultError",
        "Viewer.SearchTooltip",
        "Viewer.MatchUnit",
        "Viewer.Export",
        "Viewer.ExportMarkdown",
        "Viewer.ExportJsonl",
        "Viewer.Raw",
        "Viewer.StatsMsgs",
        "Viewer.StatsTools",
        "Viewer.StatsErrors",
        "Viewer.StatsTurns",
        "Viewer.DecodeSkip",
        "Viewer.ReadFail",
        "Viewer.Language",
        "Viewer.Tokens",
        "Viewer.Duration",
        "Viewer.TokenIn",
        "Viewer.TokenOut",
        "Viewer.ExportTranscriptTitle",
        "Viewer.ExportUntitled",
        "Viewer.ExportSection.User",
        "Viewer.ExportSection.Assistant",
        "Viewer.ExportSection.Error",
        "Viewer.ExportSection.Tool",
        "Viewer.ExportReasoningSummary",
        "Viewer.ExportToolResultSummary",
        "Viewer.PickSessionDir",
        "Viewer.ExportNoSession",
        "Viewer.ExportMarkdownTitle",
        "Viewer.ExportDone",
        "Viewer.ExportFailed",
        "Viewer.ExportJsonlTitle",
        "Viewer.ExportJsonlDone",
        "Viewer.Truncated",
    };

    [Fact]
    public void All_viewer_keys_resolve_in_every_supported_culture()
    {
        try
        {
            foreach (var lang in Localization.Supported)
            {
                Localization.SetLanguage(lang.Code);
                foreach (var key in Keys)
                {
                    string v = Localization.Get(key);
                    Assert.False(string.IsNullOrWhiteSpace(v),
                        $"[{lang.Code}] key '{key}' resolved empty (missing in {lang.Code} resx?)");
                    if (key is "Viewer.Toolbar.RowsUnit" or "Viewer.MatchUnit"
                        or "Viewer.ReadFail" or "Viewer.ExportFailed")
                    {
                        Assert.Contains("{0}", v, StringComparison.Ordinal);
                    }
                }
            }
        }
        finally
        {
            Localization.SetLanguage("zh-CN"); // restore default so other tests are unaffected
        }
    }
}
