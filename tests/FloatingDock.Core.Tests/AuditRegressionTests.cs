using System.IO.Compression;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Icons;
using FloatingDock.Core.Models;
using FloatingDock.Core.Services;
using Xunit;

namespace FloatingDock.Core.Tests;

public sealed class AuditRegressionTests
{
    [Fact]
    public void InvalidHistoryEntriesDoNotBreakSuggestions()
    {
        var app = new AppRef { Name = "Test", ExePath = @"C:\Apps\test.exe" };
        using var dir = new TempDir();
        var path = dir.File("usage.json");
        JsonFile.WriteAtomic(path, "{\"" + app.Key.Replace("\\", "\\\\") + "\":null}");
        var usage = SearchUsage.Load(path);
        Assert.False(usage.HasEntries);
        Assert.Empty(usage.Suggestions(new[] { app }));
    }

    [Fact]
    public void HistoryCountsCannotOverflowAndLoadedKeysRemainCaseInsensitive()
    {
        var app = new AppRef { Name = "Test", ExePath = @"C:\Apps\test.exe" };
        var entries = new Dictionary<string, SearchUsage.Entry>
        {
            [app.Key.ToUpperInvariant()] = new(int.MaxValue, DateTime.UtcNow)
        };
        var usage = new SearchUsage(entries);
        Assert.Single(usage.Suggestions(new[] { app }));
        usage.Record(app, DateTime.UtcNow);
        Assert.Equal(1000000, usage.Snapshot()[app.Key].Count);
        Assert.Single(usage.Snapshot());
        Assert.Equal(int.MaxValue, entries.Values.Single().Count);
    }

    [Fact]
    public void LoadedHistoryIsBoundedBeforeItIsUsed()
    {
        var entries = Enumerable.Range(0, 150).ToDictionary(i => "id:" + i,
            i => new SearchUsage.Entry(1, DateTime.UnixEpoch.AddSeconds(i)));
        entries["id:negative"] = new(-1, DateTime.UtcNow);
        var usage = new SearchUsage(entries);
        Assert.Equal(100, usage.Snapshot().Count);
        Assert.DoesNotContain("id:0", usage.Snapshot().Keys);
        Assert.DoesNotContain("id:negative", usage.Snapshot().Keys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManifestlessImportUsesThePreviewNameInsteadOfTheStagingDirectory(bool wrapped)
    {
        using var dir = new TempDir();
        var path = dir.File("My icons.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry((wrapped ? "Pretty icons/" : "") + "test.png").Open();
            stream.Write(PngWriter.EncodeBgra(2, 2, new byte[16], false));
        }
        var service = new IconPackService(dir.File("packs"), dir.File("temp"));
        var preview = service.Inspect(path);
        var imported = service.Import(path);
        Assert.Equal(preview.Id, imported.Id);
        Assert.Equal(preview.Name, imported.Name);
        Assert.Equal(imported.Id, service.Import(path).Id);
        Assert.Single(service.ListPacks(), p => !p.IsBuiltIn);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("CUSTOM")]
    public void BuiltInPackProtectionIgnoresWindowsPathCasing(string id)
    {
        using var dir = new TempDir();
        var service = new IconPackService(dir.File("packs"), dir.File("temp"));
        Assert.True(IconPackService.IsBuiltInId(id));
        Assert.True(service.Load(id)!.IsBuiltIn);
        Assert.False(service.Delete(id));
    }
}
