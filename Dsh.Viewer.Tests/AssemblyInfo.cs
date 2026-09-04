using System.Runtime.CompilerServices;
using Dsh.App.Services;
using Xunit;

// Viewer tests assume a shared zh-CN default UI culture (e.g. SurfaceItem role labels) and one
// test temporarily switches the global Localization culture; disable xunit class-level parallelism
// so tests never observe the global culture mid-switch.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Dsh.Viewer.Tests;

/// <summary>
/// Pins this test assembly's UI culture so assertions over localized labels are deterministic.
///
/// <see cref="Localization"/> seeds its culture from <c>CultureInfo.CurrentUICulture</c>, which is
/// host-dependent: a zh-CN Windows box starts at zh-CN, but a GitHub Actions windows-latest runner
/// starts at en-US. Because <c>Strings.en.resx</c> is complete, <see cref="Dsh.App.Strings.Get"/>
/// resolves the English value on the first attempt and never falls back to zh-CN — so every test
/// that pins a Chinese label (SurfaceItem role labels, TranscriptExporter section headers) fails on
/// CI while passing locally. That was the v0.2.2-alpha CI failure.
///
/// Selecting zh-CN here, before any test runs, makes the "default is zh-CN" assumption that
/// <see cref="ViewerLocalizationCompletenessTests"/> documents true on every runner.
/// </summary>
internal static class TestCultureDefaults
{
    [ModuleInitializer]
    internal static void PinDefaultUiCulture() => Localization.SetLanguage("zh-CN");
}
