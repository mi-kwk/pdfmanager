using PdfManager.Core.Database;
using PdfManager.Core.Library;
using PdfManager.Core.Update;

namespace PdfManager.Core.Tests;

// 決定ログ HA-1〜HA-7: exe の差し替えと、バージョンが変わったときのデータベースの扱い
public sealed class UpdateTests : IDisposable
{
    private const long Big = ExeUpdater.MinimumStandaloneSize;

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pdfmanager-test-{Guid.NewGuid():N}")).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Evaluate_AcceptsOnlyNewerStandalonePdfManager()
    {
        var current = new Version(2026, 11, 10, 1845);

        Assert.Equal(new ExeCheck(ExeCheckStatus.Newer, "2026.11.20.0930"), ExeUpdater.Evaluate("PdfManager", "2026.11.20.0930", Big, current));
        // 時分は数として比べる(0930 < 1845)
        Assert.Equal(ExeCheckStatus.SameOrOlder, ExeUpdater.Evaluate("PdfManager", "2026.11.10.0930", Big, current).Status);
        Assert.Equal(ExeCheckStatus.SameOrOlder, ExeUpdater.Evaluate("PdfManager", "2026.11.10.1845", Big, current).Status);
        Assert.Equal(ExeCheckStatus.Newer, ExeUpdater.Evaluate("PdfManager", "2026.11.10.1846+abc", Big, current).Status);
        Assert.Equal(ExeCheckStatus.NotPdfManager, ExeUpdater.Evaluate("Notepad", "10.0.1.1", Big, current).Status);
        Assert.Equal(ExeCheckStatus.NotPdfManager, ExeUpdater.Evaluate("PdfManager", null, Big, current).Status);
        Assert.Equal(ExeCheckStatus.NotStandalone, ExeUpdater.Evaluate("PdfManager", "2026.11.20.0930", 300_000, current).Status);
    }

    [Fact]
    public void Check_RejectsTheRunningExeItselfAndNonExe()
    {
        var exe = Path.Combine(_root, "PdfManager.exe");
        File.WriteAllText(exe, "now");
        var text = Path.Combine(_root, "memo.exe");
        File.WriteAllText(text, "not a program");
        var current = new Version(2026, 1, 1, 0);

        Assert.Equal(ExeCheckStatus.SameFile, ExeUpdater.Check(exe, exe, current).Status);
        Assert.Equal(ExeCheckStatus.NotPdfManager, ExeUpdater.Check(text, exe, current).Status);
    }

    [Fact]
    public void StageThenComplete_PutsNewExeAndKeepsOneOldExe()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "app")).FullName;
        var exe = Path.Combine(folder, "PdfManager.exe");
        File.WriteAllText(exe, "v1");
        File.WriteAllText(Path.Combine(folder, "PdfManager.old.exe"), "v0");
        var usb = Path.Combine(_root, "PdfManager.exe");
        File.WriteAllText(usb, "v2");

        // ① 写すだけ。今の exe には触らない
        var staged = ExeUpdater.Stage(exe, usb);
        Assert.Equal(Path.Combine(folder, "PdfManager.new.exe"), staged);
        Assert.Equal("v1", File.ReadAllText(exe));

        // ③ 前の PdfManager が終わってから入れ替える(〇〇.new.exe は起動し直した PdfManager が消す)
        ExeUpdater.Complete(exe, staged);

        Assert.Equal("v2", File.ReadAllText(exe));
        Assert.Equal("v1", File.ReadAllText(Path.Combine(folder, "PdfManager.old.exe")));
        Assert.True(ExeUpdater.TryDelete(staged));
        Assert.Equal<string>(["PdfManager.exe", "PdfManager.old.exe"], Directory.GetFiles(folder).Select(f => Path.GetFileName(f)!).Order().ToArray());
        Assert.True(ExeUpdater.CanWrite(exe));
    }

    [Fact]
    public void Stage_LeavesNothingWhenNewOneCannotBeRead()
    {
        var exe = Path.Combine(_root, "PdfManager.exe");
        File.WriteAllText(exe, "v1");

        Assert.ThrowsAny<IOException>(() => ExeUpdater.Stage(exe, Path.Combine(_root, "none.exe")));

        Assert.Equal("v1", File.ReadAllText(exe));
        Assert.Equal<string>(["PdfManager.exe"], Directory.GetFiles(_root).Select(f => Path.GetFileName(f)!).ToArray());
    }

    [Fact]
    public void Complete_KeepsCurrentExeWhenItIsStillLocked()
    {
        var exe = Path.Combine(_root, "PdfManager.exe");
        File.WriteAllText(exe, "v1");
        var staged = Path.Combine(_root, "PdfManager.new.exe");
        File.WriteAllText(staged, "v2");

        using (new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => ExeUpdater.Complete(exe, staged));
        }

        Assert.Equal("v1", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".tmp"));
    }

    [Fact]
    public void Open_SavesCopyOfOlderDatabaseBeforeChangingIt()
    {
        var info = LibraryService.Create(_root, "PDF管理");
        var db = info.DataFolder.DatabasePath;
        // 古い形の控えが前にもあれば、1 つだけ残す
        File.WriteAllText(db + ".v9.bak", "old");
        // 1 つ前の形(V11 の親のタグの列がない)に戻す
        using (var connection = PdfManagerDatabase.Open(db))
        {
            PdfManagerDatabase.Execute(connection, "DROP INDEX ix_tags_parent; ALTER TABLE tags DROP COLUMN parent_tag_id;");
            PdfManagerDatabase.Execute(connection, $"PRAGMA user_version = {PdfManagerDatabase.SchemaVersion - 1};");
        }

        LibraryService.Open(info.LibraryFilePath);

        var copy = db + $".v{PdfManagerDatabase.SchemaVersion - 1}.bak";
        Assert.True(File.Exists(copy));
        Assert.False(File.Exists(db + ".v9.bak"));
        using (var connection = PdfManagerDatabase.Open(copy))
        {
            Assert.Equal(PdfManagerDatabase.SchemaVersion - 1, PdfManagerDatabase.GetUserVersion(connection));
        }
        using (var connection = PdfManagerDatabase.Open(db))
        {
            Assert.Equal(PdfManagerDatabase.SchemaVersion, PdfManagerDatabase.GetUserVersion(connection));
        }

        // 同じ形のときは控えを取り直さない
        File.Delete(copy);
        LibraryService.Open(info.LibraryFilePath);
        Assert.False(File.Exists(copy));
    }

    [Fact]
    public void Open_ByOlderApp_TellsRequiredVersion()
    {
        var info = LibraryService.Create(_root, "PDF管理");
        using (var connection = PdfManagerDatabase.Open(info.DataFolder.DatabasePath))
        {
            PdfManagerDatabase.Execute(connection,
                $"INSERT INTO library_meta (key, value) VALUES ('{PdfManagerDatabase.SchemaAppVersionKey}', '2027.1.5.0930') ON CONFLICT(key) DO UPDATE SET value = excluded.value;");
            PdfManagerDatabase.Execute(connection, $"PRAGMA user_version = {PdfManagerDatabase.SchemaVersion + 1};");
        }

        var ex = Assert.Throws<LibraryException>(() => LibraryService.Open(info.LibraryFilePath));

        Assert.Contains("バージョン 2027.1.5.0930 以降の PdfManager", ex.Message);
    }

    [Fact]
    public void Initialize_RecordsAppVersionThatChangedTheDatabase()
    {
        var path = Path.Combine(_root, "test.db");
        PdfManagerDatabase.AppVersion = "2026.11.11.1200";
        try
        {
            using var connection = PdfManagerDatabase.Open(path);
            PdfManagerDatabase.Initialize(connection);

            Assert.Equal("2026.11.11.1200", PdfManagerDatabase.ReadSchemaAppVersion(connection));
        }
        finally
        {
            PdfManagerDatabase.AppVersion = null;
        }
    }

    [Fact]
    public void HandoffFromNewerApp_TellsItsVersion()
    {
        var json = """{"format":"PdfManagerHandoff","version":99,"libraryId":"x","libraryName":"x","pc":"x","exportedAt":"2026-11-11T00:00:00+09:00","groups":[],"tags":[],"fields":[],"documents":[],"links":[],"appVersion":"2027.2.1.1000"}""";

        var ex = Assert.Throws<LibraryException>(() => Export.HandoffFormat.Deserialize(json));

        Assert.Contains("バージョン 2027.2.1.1000 以降に更新", ex.Message);
    }
}
