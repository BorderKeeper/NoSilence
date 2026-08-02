using System.Text.Json;
using NoSilence.Settings;

namespace NoSilence.Tests;

public class SettingsSerializationTests
{
    private static JsonDocument Parse(AppSettings settings) =>
        JsonDocument.Parse(SettingsService.Serialize(settings));

    /// <summary>
    /// The whole point. Writing every property meant an install could never receive an
    /// improved default — a real config here silently pinned a 20 s release window and a
    /// rules list from before console hosts were excluded.
    /// </summary>
    [Fact]
    public void UntouchedSettings_SerialiseToAlmostNothing()
    {
        using JsonDocument document = Parse(new AppSettings());

        // schemaVersion is always kept; nothing else should be.
        Assert.Single(document.RootElement.EnumerateObject());
        Assert.True(document.RootElement.TryGetProperty("schemaVersion", out _));
    }

    [Fact]
    public void ChangedValuesArePersisted()
    {
        var settings = new AppSettings();
        settings.Output.VolumePercent = 42;

        using JsonDocument document = Parse(settings);

        Assert.Equal(42, document.RootElement.GetProperty("output").GetProperty("volumePercent").GetInt32());
    }

    [Fact]
    public void UnchangedSiblingsOfAChangedValueAreNotPersisted()
    {
        var settings = new AppSettings();
        settings.Output.VolumePercent = 42;

        using JsonDocument document = Parse(settings);
        JsonElement output = document.RootElement.GetProperty("output");

        Assert.False(output.TryGetProperty("latencyMs", out _));
        Assert.False(output.TryGetProperty("keepStreamOpenWhileDucked", out _));
    }

    [Fact]
    public void UntouchedSectionsAreOmittedEntirely()
    {
        var settings = new AppSettings();
        settings.Output.VolumePercent = 42;

        using JsonDocument document = Parse(settings);

        Assert.False(document.RootElement.TryGetProperty("detection", out _));
        Assert.False(document.RootElement.TryGetProperty("library", out _));
    }

    [Fact]
    public void ChangedDetectionValuesSurviveARoundTrip()
    {
        var settings = new AppSettings();
        settings.Detection.ReleaseMs = 12345;
        settings.Detection.ThresholdDb = -42;

        AppSettings restored = JsonSerializer.Deserialize<AppSettings>(SettingsService.Serialize(settings), JsonOptions.Default)!;

        Assert.Equal(12345, restored.Detection.ReleaseMs);
        Assert.Equal(-42, restored.Detection.ThresholdDb);

        // And an untouched value still tracks the current default.
        Assert.Equal(new DetectionConfigProbe().MinDurationMs, restored.Detection.MinDurationMs);
    }

    /// <summary>
    /// A customised rules list is persisted whole rather than merged element by element,
    /// which would be unpredictable when the built-in list changes.
    /// </summary>
    [Fact]
    public void ACustomisedRulesListIsPersistedInFull()
    {
        var settings = new AppSettings();
        settings.Detection.Rules.Add(new NoSilence.Detection.ProcessRule("mygame.exe", Mode: NoSilence.Detection.RuleMode.Ignore));

        AppSettings restored = JsonSerializer.Deserialize<AppSettings>(SettingsService.Serialize(settings), JsonOptions.Default)!;

        Assert.Equal(settings.Detection.Rules.Count, restored.Detection.Rules.Count);
        Assert.Contains(restored.Detection.Rules, r => r.Match == "mygame.exe");
        Assert.Contains(restored.Detection.Rules, r => r.Match == "chrome.exe");
    }

    [Fact]
    public void LibraryFoldersRoundTrip()
    {
        var settings = new AppSettings();
        settings.Library.Folders.Add(@"D:\");
        settings.Library.Recursive = false;

        AppSettings restored = JsonSerializer.Deserialize<AppSettings>(SettingsService.Serialize(settings), JsonOptions.Default)!;

        Assert.Equal([@"D:\"], restored.Library.Folders);
        Assert.False(restored.Library.Recursive);
    }

    /// <summary>Reads the current defaults without depending on a specific number here.</summary>
    private sealed class DetectionConfigProbe
    {
        public int MinDurationMs { get; } = new NoSilence.Detection.DetectionConfig().MinDurationMs;
    }
}
