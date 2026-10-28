using System.Globalization;
using System.Runtime.CompilerServices;
using AirDrop.App.Localization;

namespace AirDrop.Tests;

/// <summary>
/// Every test sees the Turkish source strings regardless of the machine's display language: assertions on UI text are
/// written against tr. Tests that need another language read the resources with an explicit culture instead of
/// switching <see cref="Loc"/> (it is process-wide and test classes run in parallel).
/// </summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void Init() => Loc.SetLanguage("tr", CultureInfo.GetCultureInfo("tr-TR"));
}
