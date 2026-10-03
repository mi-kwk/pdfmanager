using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PdfManager.Core.Documents;

public sealed record VersionInfo(string DocumentId, int VersionNo, string Label, string Title, string FileName, DateTimeOffset CreatedAt)
{
    public string DisplayName => VersionNames.Format(VersionNo, Label);
}

public static class VersionNames
{
    // 決定ログ N-3: 「第 3 版」、名前があれば「第 3 版(Rev.C)」
    public static string Format(int versionNo, string label) => label.Length == 0 ? $"第{versionNo}版" : $"第{versionNo}版({label})";
}

// 版管理(docs/04「版管理」、決定ログ N-1〜N-8)
public sealed class VersionRepository(SqliteConnection connection)
{
    public IReadOnlyList<VersionInfo> GetVersions(string documentId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.id, d.version_no, d.version_label, d.title, d.file_name, d.created_at
            FROM documents d
            WHERE d.series_id IS NOT NULL AND d.series_id = (SELECT series_id FROM documents WHERE id = $id)
            ORDER BY d.version_no;
            """;
        command.Parameters.AddWithValue("$id", documentId);
        using var reader = command.ExecuteReader();
        var versions = new List<VersionInfo>();
        while (reader.Read())
        {
            versions.Add(new VersionInfo(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture)));
        }
        return versions;
    }

    // 決定ログ N-1①: base の版系列の最新版として newDocument を加える(版系列がなければ作り、base を第 1 版にする)
    public void AddNewestVersion(string baseDocumentId, string newDocumentId)
    {
        using var transaction = connection.BeginTransaction();
        var seriesId = Scalar<string>("SELECT series_id FROM documents WHERE id = $id;", transaction, ("$id", baseDocumentId));
        if (seriesId is null)
        {
            seriesId = CreateSeries(baseDocumentId, transaction);
            Execute("UPDATE documents SET series_id = $series, version_no = 1 WHERE id = $id;", transaction, ("$series", seriesId), ("$id", baseDocumentId));
        }
        var next = Scalar<long>("SELECT MAX(version_no) FROM documents WHERE series_id = $series;", transaction, ("$series", seriesId)) + 1;
        Execute("UPDATE documents SET series_id = $series, version_no = $no WHERE id = $id;", transaction,
            ("$series", seriesId), ("$no", next), ("$id", newDocumentId));
        transaction.Commit();
    }

    // 決定ログ N-1②: 選んだ文書(と、それらがすでに属している版系列の文書)を登録日の古い順に 1 つの版系列にする
    public void Group(IReadOnlyCollection<string> documentIds)
    {
        using var transaction = connection.BeginTransaction();
        var members = new HashSet<string>(documentIds);
        var seriesIds = new HashSet<string>();
        foreach (var id in documentIds)
        {
            if (Scalar<string>("SELECT series_id FROM documents WHERE id = $id;", transaction, ("$id", id)) is { } series)
            {
                seriesIds.Add(series);
                foreach (var other in Query("SELECT id FROM documents WHERE series_id = $series;", transaction, r => r.GetString(0), ("$series", series)))
                {
                    members.Add(other);
                }
            }
        }
        if (members.Count < 2)
        {
            return;
        }

        var ordered = members
            .Select(id => (Id: id, Created: Scalar<string>("SELECT created_at FROM documents WHERE id = $id;", transaction, ("$id", id))!))
            .OrderBy(m => m.Created, StringComparer.Ordinal)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .Select(m => m.Id)
            .ToList();
        var target = seriesIds.FirstOrDefault() ?? CreateSeries(ordered[0], transaction);
        Renumber(target, ordered, transaction);
        DeleteEmptySeries(transaction);
        transaction.Commit();
    }

    // 決定ログ N-8: 独立した文書に戻し、残りを第 1 版から付け直す。残りが 1 件なら版系列をなくす
    public void Detach(string documentId)
    {
        using var transaction = connection.BeginTransaction();
        var seriesId = Scalar<string>("SELECT series_id FROM documents WHERE id = $id;", transaction, ("$id", documentId));
        if (seriesId is null)
        {
            return;
        }
        Execute("UPDATE documents SET series_id = NULL, version_no = NULL WHERE id = $id;", transaction, ("$id", documentId));
        var rest = Query("SELECT id FROM documents WHERE series_id = $series ORDER BY version_no;", transaction, r => r.GetString(0), ("$series", seriesId));
        if (rest.Count <= 1)
        {
            Execute("UPDATE documents SET series_id = NULL, version_no = NULL WHERE series_id = $series;", transaction, ("$series", seriesId));
        }
        else
        {
            Renumber(seriesId, rest, transaction);
        }
        DeleteEmptySeries(transaction);
        transaction.Commit();
    }

    public void SetLabel(string documentId, string label) =>
        Execute("UPDATE documents SET version_label = $label WHERE id = $id;", null, ("$label", label.Trim()), ("$id", documentId));

    // 文書を削除したあとに残った、空の版系列を消す
    public void DeleteEmptySeries() => DeleteEmptySeries(null);

    private void DeleteEmptySeries(SqliteTransaction? transaction) =>
        Execute("DELETE FROM version_series WHERE id NOT IN (SELECT series_id FROM documents WHERE series_id IS NOT NULL);", transaction);

    // UNIQUE(series_id, version_no) に引っかからないよう、いったん外してから付け直す
    private void Renumber(string seriesId, IReadOnlyList<string> orderedIds, SqliteTransaction transaction)
    {
        foreach (var id in orderedIds)
        {
            Execute("UPDATE documents SET series_id = NULL, version_no = NULL WHERE id = $id;", transaction, ("$id", id));
        }
        for (var i = 0; i < orderedIds.Count; i++)
        {
            Execute("UPDATE documents SET series_id = $series, version_no = $no WHERE id = $id;", transaction,
                ("$series", seriesId), ("$no", i + 1), ("$id", orderedIds[i]));
        }
    }

    private string CreateSeries(string firstDocumentId, SqliteTransaction transaction)
    {
        var id = Guid.NewGuid().ToString("D");
        var title = Scalar<string>("SELECT title FROM documents WHERE id = $id;", transaction, ("$id", firstDocumentId)) ?? "";
        Execute("INSERT INTO version_series (id, name) VALUES ($id, $name);", transaction, ("$id", id), ("$name", title));
        return id;
    }

    private T? Scalar<T>(string sql, SqliteTransaction? transaction, params (string Name, object Value)[] parameters)
    {
        using var command = Command(sql, transaction, parameters);
        return command.ExecuteScalar() is T value ? value : default;
    }

    private List<T> Query<T>(string sql, SqliteTransaction? transaction, Func<SqliteDataReader, T> map, params (string Name, object Value)[] parameters)
    {
        using var command = Command(sql, transaction, parameters);
        using var reader = command.ExecuteReader();
        var results = new List<T>();
        while (reader.Read())
        {
            results.Add(map(reader));
        }
        return results;
    }

    private void Execute(string sql, SqliteTransaction? transaction, params (string Name, object Value)[] parameters)
    {
        using var command = Command(sql, transaction, parameters);
        command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, SqliteTransaction? transaction, (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return command;
    }
}
