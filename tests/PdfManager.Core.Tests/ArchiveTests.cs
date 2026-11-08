using Microsoft.Data.Sqlite;
using PdfManager.Core.Database;
using PdfManager.Core.Library;

namespace PdfManager.Core.Tests;

// 決定ログ EA-1〜EA-8(外付け HDD への退避の候補)
public sealed class ArchiveTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"pdfmanager-test-{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    public ArchiveTests()
    {
        _connection = PdfManagerDatabase.Open(_path);
        PdfManagerDatabase.Initialize(_connection);
        Exec("INSERT INTO sub_libraries (id, name, normalized_name, library_file_path, created_at) VALUES ('sub', '過去', '過去', 'G:\\x.pdflib', '2026-01-01');");
        // old: 2 年前に登録して一度も開いていない / viewed: 2 年前に登録したが先月開いた / recent: 先月登録 / insub: サブにある
        Insert("old", "2024-10-01T00:00:00Z", null, null);
        Insert("viewed", "2024-10-01T00:00:00Z", "2026-10-01T00:00:00Z", null);
        Insert("recent", "2026-10-01T00:00:00Z", null, null);
        Insert("halfyear", "2024-01-01T00:00:00Z", "2026-03-01T00:00:00Z", null);
        Insert("insub", "2024-01-01T00:00:00Z", "2026-11-01T00:00:00Z", "sub");
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(_path + suffix);
        }
    }

    [Fact]
    public void Candidates_AreMainDocumentsNotUsedForMonths_OldestFirst()
    {
        Assert.Equal(["old", "halfyear"], ArchivePlanner.Candidates(_connection, 6, Now).Select(c => c.Id));
        Assert.Equal(["old"], ArchivePlanner.Candidates(_connection, 12, Now).Select(c => c.Id));
        Assert.Equal(["old", "halfyear", "recent", "viewed"], ArchivePlanner.Candidates(_connection, 0, Now).Select(c => c.Id));
    }

    [Fact]
    public void FrequentInSubs_CountsRecentViewsOfDocumentsInSubs()
    {
        Exec("""
            INSERT INTO view_events (document_id, viewed_at, pc) VALUES
              ('insub', '2026-11-01T00:00:00.0000000Z', 'PC'), ('insub', '2026-11-02T00:00:00.0000000Z', 'PC'),
              ('insub', '2025-01-01T00:00:00.0000000Z', 'PC'), ('viewed', '2026-11-01T00:00:00.0000000Z', 'PC');
            """);

        var frequent = Assert.Single(ArchivePlanner.FrequentInSubs(_connection, Now.AddDays(-90)));

        Assert.Equal(("insub", 2, "sub"), (frequent.Id, frequent.RecentViews, frequent.StorageId));
    }

    [Fact]
    public void IsSpaceLow_ZeroPercentNeverWarns()
    {
        Assert.False(ArchivePlanner.IsSpaceLow(Path.GetTempPath(), 0));
        Assert.True(ArchivePlanner.IsSpaceLow(Path.GetTempPath(), 101));
        Assert.NotNull(ArchivePlanner.DriveSpace(Path.GetTempPath()));
    }

    private void Insert(string id, string created, string? viewed, string? sub) => Exec($"""
        INSERT INTO documents (id, title, file_hash, original_file_name, file_size, created_at, updated_at, file_name, last_viewed_at, storage_status, archive_volume_id)
        VALUES ('{id}', '{id}', 'h{id}', '{id}.pdf', 100, '{created}', '{created}', '{id}.pdf', {(viewed is null ? "NULL" : $"'{viewed}'")},
                '{(sub is null ? "local" : "archived")}', {(sub is null ? "NULL" : $"'{sub}'")});
        """);

    private void Exec(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
