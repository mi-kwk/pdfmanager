using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PdfManager.Core.Documents;

// 続き・関連の相手 1 件分
public sealed record LinkedDocument(string Id, string Title, string FileName, DateTimeOffset CreatedAt);

// 続き(前編 → 後編、順番あり)と関連(順番なし)(docs/04「続き・関連」、決定ログ V-1〜V-8)。
// 続きは document_links の type = 'next'(from の次が to)。1 つの文書の前と次はそれぞれ 1 つまで。
// 関連は type = 'related' で、from_id < to_id の向きで 1 行だけ持つ
public sealed class LinkRepository(SqliteConnection connection)
{
    // この文書を含む続きの並び(前から順)。続きがなければ空
    public IReadOnlyList<LinkedDocument> GetChain(string documentId)
    {
        var head = documentId;
        var seen = new HashSet<string> { head };
        while (PreviousOf(head) is { } previous && seen.Add(previous))
        {
            head = previous;
        }
        var chain = new List<string> { head };
        while (NextOf(chain[^1]) is { } next && !chain.Contains(next))
        {
            chain.Add(next);
        }
        return chain.Count < 2 ? [] : chain.Select(Read).OfType<LinkedDocument>().ToList();
    }

    public string? PreviousOf(string documentId) =>
        Scalar("SELECT from_id FROM document_links WHERE type = 'next' AND to_id = $id;", ("$id", documentId));

    public string? NextOf(string documentId) =>
        Scalar("SELECT to_id FROM document_links WHERE type = 'next' AND from_id = $id;", ("$id", documentId));

    public bool IsInChain(string documentId) => PreviousOf(documentId) is not null || NextOf(documentId) is not null;

    // 決定ログ V-2: documentId の次に other をつなぐ。documentId にすでに次があれば、その間に入れる
    public void InsertNext(string documentId, string other)
    {
        EnsureCanJoin(documentId, other);
        using var transaction = connection.BeginTransaction();
        if (NextOf(documentId) is { } oldNext)
        {
            Execute("DELETE FROM document_links WHERE type = 'next' AND from_id = $a;", transaction, ("$a", documentId));
            AddNext(other, oldNext, transaction);
        }
        AddNext(documentId, other, transaction);
        transaction.Commit();
    }

    // documentId の前に other をつなぐ。documentId にすでに前があれば、その間に入れる
    public void InsertPrevious(string documentId, string other)
    {
        EnsureCanJoin(documentId, other);
        using var transaction = connection.BeginTransaction();
        if (PreviousOf(documentId) is { } oldPrevious)
        {
            Execute("DELETE FROM document_links WHERE type = 'next' AND to_id = $a;", transaction, ("$a", documentId));
            AddNext(oldPrevious, other, transaction);
        }
        AddNext(other, documentId, transaction);
        transaction.Commit();
    }

    // 決定ログ V-6: 続きから外し、前と次をつなぎ直す
    public void RemoveFromChain(string documentId)
    {
        var previous = PreviousOf(documentId);
        var next = NextOf(documentId);
        using var transaction = connection.BeginTransaction();
        Execute("DELETE FROM document_links WHERE type = 'next' AND (from_id = $id OR to_id = $id);", transaction, ("$id", documentId));
        if (previous is not null && next is not null)
        {
            AddNext(previous, next, transaction);
        }
        transaction.Commit();
    }

    public IReadOnlyList<LinkedDocument> GetRelated(string documentId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CASE WHEN from_id = $id THEN to_id ELSE from_id END
            FROM document_links WHERE type = 'related' AND (from_id = $id OR to_id = $id);
            """;
        command.Parameters.AddWithValue("$id", documentId);
        var ids = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                ids.Add(reader.GetString(0));
            }
        }
        return ids.Select(Read).OfType<LinkedDocument>().OrderBy(d => d.CreatedAt).ToList();
    }

    public void AddRelated(string documentId, string other)
    {
        if (documentId == other)
        {
            throw new InvalidOperationException("自分自身とは関連づけられません。");
        }
        var (from, to) = Ordered(documentId, other);
        Execute("INSERT OR IGNORE INTO document_links (from_id, to_id, type) VALUES ($from, $to, 'related');", null, ("$from", from), ("$to", to));
    }

    public void RemoveRelated(string documentId, string other)
    {
        var (from, to) = Ordered(documentId, other);
        Execute("DELETE FROM document_links WHERE type = 'related' AND from_id = $from AND to_id = $to;", null, ("$from", from), ("$to", to));
    }

    // 決定ログ V-7: 新しい版が前の版の位置に入る(続きの前後と関連を引き継ぎ、前の版はつながりから外れる)
    public void TransferLinks(string oldDocumentId, string newDocumentId)
    {
        using var transaction = connection.BeginTransaction();
        // 新しい版がすでに同じ相手と関連づいているときは重なるため、OR IGNORE で残った分を消す
        Execute("""
            UPDATE OR IGNORE document_links SET from_id = $new WHERE from_id = $old AND to_id <> $new;
            UPDATE OR IGNORE document_links SET to_id = $new WHERE to_id = $old AND from_id <> $new;
            DELETE FROM document_links WHERE from_id = $old OR to_id = $old;
            UPDATE OR IGNORE document_links SET from_id = to_id, to_id = from_id WHERE type = 'related' AND from_id > to_id;
            DELETE FROM document_links WHERE type = 'related' AND from_id > to_id;
            """, transaction, ("$old", oldDocumentId), ("$new", newDocumentId));
        transaction.Commit();
    }

    // 続き・関連の相手の文書(一覧の印・検索に使う)。続きの位置は、並びの何番目か / 全体の数
    public static IReadOnlyDictionary<string, (int Position, int Length)> ChainPositions(SqliteConnection connection)
    {
        var next = new Dictionary<string, string>();
        var hasPrevious = new HashSet<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT from_id, to_id FROM document_links WHERE type = 'next';";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                next[reader.GetString(0)] = reader.GetString(1);
                hasPrevious.Add(reader.GetString(1));
            }
        }
        var positions = new Dictionary<string, (int, int)>();
        foreach (var head in next.Keys.Where(k => !hasPrevious.Contains(k)))
        {
            var chain = new List<string> { head };
            while (next.TryGetValue(chain[^1], out var following) && !chain.Contains(following))
            {
                chain.Add(following);
            }
            for (var i = 0; i < chain.Count; i++)
            {
                positions[chain[i]] = (i + 1, chain.Count);
            }
        }
        return positions;
    }

    // つなげるのは、まだどの続きにも入っていない文書だけ(決定ログ V-3)
    private void EnsureCanJoin(string documentId, string other)
    {
        if (documentId == other)
        {
            throw new InvalidOperationException("自分自身とはつなげません。");
        }
        if (IsInChain(other))
        {
            throw new InvalidOperationException("選んだ PDF は、すでに別の続きに入っています。");
        }
    }

    private void AddNext(string from, string to, SqliteTransaction transaction) =>
        Execute("INSERT INTO document_links (from_id, to_id, type) VALUES ($from, $to, 'next');", transaction, ("$from", from), ("$to", to));

    private static (string From, string To) Ordered(string a, string b) => string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);

    private LinkedDocument? Read(string id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT title, file_name, created_at FROM documents WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new LinkedDocument(id, reader.GetString(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture))
            : null;
    }

    private string? Scalar(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return command.ExecuteScalar() as string;
    }

    private void Execute(string sql, SqliteTransaction? transaction, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        command.ExecuteNonQuery();
    }
}
