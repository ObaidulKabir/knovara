using System.Xml.Linq;
using Knovara.Api.Controllers;
using Knovara.Core.Models;

namespace Knovara.Api.Tests;

/// <summary>
/// The desktop app hand-writes its request bodies, so a dropdown value the API's enum does not have only
/// shows up at runtime as a 400 (Search once offered "Fast"/"Deep"). These tests read the desktop's XAML
/// and compare each dropdown with the enum the API binds it to, so the drift fails the build instead.
/// </summary>
public class DesktopApiContractTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void SearchModeDropdown_OffersExactlyTheApisRetrievalModes()
        => AssertDropdownMatchesEnum<RetrievalMode>("SearchModeCombo");

    [Fact]
    public void IngestTypeDropdown_OffersExactlyTheTypesTheApiWillIngest()
        // Code is in the enum, but POST /ingest answers 400 for it until code ingestion exists
        // (ResourcesControllerLifecycleTests.Ingest_Code_IsRejected pins that), so it must not be offered.
        => AssertDropdownMatchesEnum<SourceType>("IngestTypeCombo", notIngestable: [nameof(SourceType.Code)]);

    [Fact]
    public void SearchModeDropdown_DefaultsToTheApisDefaultMode()
    {
        // SearchRequest declares Adaptive as its default; the first item is what the desktop selects.
        var first = DropdownValues("SearchModeCombo").First();
        Assert.Equal(new SearchRequest("q").Mode.ToString(), first);
    }

    private static void AssertDropdownMatchesEnum<TEnum>(string comboName, string[]? notIngestable = null) where TEnum : struct, Enum
    {
        var offered = DropdownValues(comboName);
        notIngestable ??= [];

        // Every offered value must bind (the API parses case-insensitively, as System.Text.Json does)
        // and must be one the API actually accepts.
        foreach (var value in offered)
        {
            Assert.True(Enum.TryParse<TEnum>(value, ignoreCase: true, out _),
                $"{comboName} offers '{value}', which {typeof(TEnum).Name} does not define; the API would answer 400.");
            Assert.DoesNotContain(value, notIngestable, StringComparer.OrdinalIgnoreCase);
        }

        // And none may be missing, otherwise the desktop silently cannot reach part of the API.
        var missing = Enum.GetNames<TEnum>().Except(notIngestable).Except(offered, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(missing.Count == 0,
            $"{comboName} does not offer {string.Join(", ", missing)} from {typeof(TEnum).Name}.");
    }

    private static List<string> DropdownValues(string comboName)
    {
        var combo = XDocument.Load(FindDesktopXaml()).Descendants(Wpf + "ComboBox")
            .SingleOrDefault(c => (string?)c.Attribute(X + "Name") == comboName);
        Assert.True(combo is not null, $"ComboBox '{comboName}' not found in MainWindow.xaml; update this test if it was renamed.");

        return combo!.Elements(Wpf + "ComboBoxItem")
            .Select(i => (string?)i.Attribute("Content") ?? string.Empty)
            .ToList();
    }

    private static string FindDesktopXaml()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Knovara.Desktop", "MainWindow.xaml");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Could not locate src/Knovara.Desktop/MainWindow.xaml above the test output directory.");
    }
}
