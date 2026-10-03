using PdfManager.Core.Database;
using PdfManager.Core.Library;
using PdfManager.Core.Settings;
using PdfManager.Core.Text;

namespace PdfManager.Core.Tests;

public sealed class LibraryTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pdfmanager-test-{Guid.NewGuid():N}")).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Create_MakesLibraryFolderWithFileAndDataFolders()
    {
        var info = LibraryService.Create(_root, "PDF管理");

        Assert.Equal(Path.Combine(_root, "PdfManager", "PDF管理.pdflib"), info.LibraryFilePath);
        Assert.Equal(Path.Combine(_root, "PdfManager", "data", "db", "pdfmanager.db"), info.DataFolder.DatabasePath);
        foreach (var name in new[] { "db", "pdf", "thumbs" })
        {
            Assert.True(Directory.Exists(Path.Combine(_root, "PdfManager", "data", name)), name);
        }
        Assert.Contains("\"name\": \"PDF管理\"", File.ReadAllText(info.LibraryFilePath));
    }

    [Fact]
    public void CreateThenOpen_ReturnsSameLibrary()
    {
        var created = LibraryService.Create(_root, "PDF管理");

        var opened = LibraryService.Open(created.LibraryFilePath);

        Assert.Equal(created, opened);
    }

    [Fact]
    public void Create_RefusesSecondLibraryInSameFolder()
    {
        LibraryService.Create(_root, "1つ目");

        var ex = Assert.Throws<LibraryException>(() => LibraryService.Create(_root, "2つ目"));

        Assert.Contains("すでにライブラリがあります", ex.Message);
    }

    [Fact]
    public void Create_ReplacesCharactersNotAllowedInFileNames()
    {
        var info = LibraryService.Create(_root, "売上/報告:2026?");

        Assert.Equal("売上_報告_2026_.pdflib", Path.GetFileName(info.LibraryFilePath));
        Assert.Equal("売上/報告:2026?", info.Name);
    }

    [Fact]
    public void Open_RejectsFileThatIsNotALibrary()
    {
        var path = Path.Combine(_root, "other.pdflib");
        File.WriteAllText(path, "{ \"hello\": 1 }");

        var ex = Assert.Throws<LibraryException>(() => LibraryService.Open(path));

        Assert.Contains("ライブラリファイルではありません", ex.Message);
    }

    [Fact]
    public void Open_RejectsWhenDataFolderWasNotCopied()
    {
        var info = LibraryService.Create(_root, "PDF管理");
        var copyRoot = Directory.CreateDirectory(Path.Combine(_root, "copy")).FullName;
        var onlyFile = Path.Combine(copyRoot, Path.GetFileName(info.LibraryFilePath));
        File.Copy(info.LibraryFilePath, onlyFile);

        var ex = Assert.Throws<LibraryException>(() => LibraryService.Open(onlyFile));

        Assert.Contains("データが見つかりません", ex.Message);
    }

    [Fact]
    public void Open_RejectsDatabaseFromNewerAppVersion()
    {
        var info = LibraryService.Create(_root, "PDF管理");
        using (var connection = PdfManagerDatabase.Open(info.DataFolder.DatabasePath))
        {
            PdfManagerDatabase.Execute(connection, $"PRAGMA user_version = {PdfManagerDatabase.SchemaVersion + 1};");
        }

        var ex = Assert.Throws<LibraryException>(() => LibraryService.Open(info.LibraryFilePath));

        Assert.Contains("新しいバージョン", ex.Message);
    }

    [Fact]
    public void Open_MissingFileExplainsPossibleCauses()
    {
        var ex = Assert.Throws<LibraryException>(() => LibraryService.Open(Path.Combine(_root, "none.pdflib")));

        Assert.Contains("見つかりません", ex.Message);
    }

    [Theory]
    [InlineData("IODATA-MPC-WE1", "IODATA-MPC-WE1")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j", "a_b_c_d_e_f_g_h_i_j")]
    [InlineData("報告書. ", "報告書")]
    [InlineData("CON", "_CON")]
    [InlineData("  ", "fallback")]
    public void Sanitize_ProducesValidWindowsFileNames(string input, string expected)
    {
        Assert.Equal(expected, FileNames.Sanitize(input, "fallback"));
    }

    [Fact]
    public void Settings_RoundTripAndRememberMostRecentFirstWithoutDuplicates()
    {
        var store = new AppSettingsStore(Path.Combine(_root, "settings"));
        var settings = AppSettings.Empty;
        settings = AppSettingsStore.RememberOpened(settings, "A", @"C:\a\PdfManager\A.pdflib");
        settings = AppSettingsStore.RememberOpened(settings, "B", @"C:\b\PdfManager\B.pdflib");
        settings = AppSettingsStore.RememberOpened(settings, "A", @"c:\A\pdfmanager\a.PDFLIB");

        store.Save(settings);
        var loaded = store.Load();

        Assert.Equal(@"c:\A\pdfmanager\a.PDFLIB", loaded.LastLibraryFilePath);
        Assert.Equal(["A", "B"], loaded.RecentLibraries.Select(r => r.Name));
    }

    [Fact]
    public void Settings_KeepViewPreferencesAcrossOtherChanges()
    {
        var store = new AppSettingsStore(Path.Combine(_root, "settings"));
        var settings = AppSettings.Empty with { View = new ViewPreferences("Field", 7, Descending: false, ListView: true) };
        settings = AppSettingsStore.RememberOpened(settings, "A", @"C:\a\PdfManager\A.pdflib");

        store.Save(settings);

        Assert.Equal(new ViewPreferences("Field", 7, false, true), store.Load().View);
    }

    [Fact]
    public void Settings_KeepsAtMostTenRecentLibraries()
    {
        var settings = AppSettings.Empty;
        for (var i = 0; i < 15; i++)
        {
            settings = AppSettingsStore.RememberOpened(settings, $"L{i}", $@"C:\l{i}\PdfManager\L{i}.pdflib");
        }

        Assert.Equal(AppSettingsStore.MaxRecentLibraries, settings.RecentLibraries.Count);
        Assert.Equal("L14", settings.RecentLibraries[0].Name);
    }

    [Fact]
    public void Settings_ForgetRemovesFromRecentAndLast()
    {
        var settings = AppSettingsStore.RememberOpened(AppSettings.Empty, "A", @"C:\a\PdfManager\A.pdflib");

        settings = AppSettingsStore.Forget(settings, @"C:\a\PdfManager\A.pdflib");

        Assert.Null(settings.LastLibraryFilePath);
        Assert.Empty(settings.RecentLibraries);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ \"lastLibraryFilePath\": \"C:\\\\x.pdflib\" }")]
    public void Settings_BrokenOrPartialFileStillLoads(string content)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "settings")).FullName;
        File.WriteAllText(Path.Combine(dir, "settings.json"), content);

        var loaded = new AppSettingsStore(dir).Load();

        Assert.NotNull(loaded.RecentLibraries);
    }
}
