using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using PdfManager.Core.Catalog;
using PdfManager.Core.Text;

namespace PdfManager.Core.Search;

// 保存した検索の補助項目 1 つ分(画面に入力した形のまま持つ)
public sealed record SavedFieldCondition(long FieldId, string Text, string From, string To, IReadOnlyList<string> Values);

// 保存する絞り込みの条件(決定ログ X-1)。並び順と表示の種類は含めない。画面に入力した形のまま持ち、呼び出したときにそのまま戻す
public sealed record SavedSearchCondition
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Keywords { get; init; } = "";

    public bool TagAny { get; init; }

    public IReadOnlyList<long> IncludedTagIds { get; init; } = [];

    public IReadOnlyList<long> ExcludedTagIds { get; init; } = [];

    public bool UntaggedOnly { get; init; }

    public IReadOnlyList<SavedFieldCondition> Fields { get; init; } = [];

    public int? MinRating { get; init; }

    public bool UnratedOnly { get; init; }

    public string RegisteredFrom { get; init; } = "";

    public string RegisteredTo { get; init; } = "";

    public string LastViewedFrom { get; init; } = "";

    public string LastViewedTo { get; init; } = "";

    public bool NeverViewedOnly { get; init; }

    public bool IncludeOldVersions { get; init; }

    public bool FilterByStorage { get; init; }

    public string? StorageId { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    // 読めなければ条件なしとして扱う
    public static SavedSearchCondition FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SavedSearchCondition>(json, JsonOptions) ?? new SavedSearchCondition();
        }
        catch (JsonException)
        {
            return new SavedSearchCondition();
        }
    }
}

public sealed record SavedSearch(long Id, string Name, SavedSearchCondition Condition);

// 検索条件の保存(F-16、決定ログ X-1〜X-4)。ライブラリの中に持つ(X-2)
public sealed class SavedSearchRepository(SqliteConnection connection)
{
    public IReadOnlyList<SavedSearch> GetAll()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, condition_json FROM saved_searches ORDER BY sort_order, id;";
        using var reader = command.ExecuteReader();
        var searches = new List<SavedSearch>();
        while (reader.Read())
        {
            searches.Add(new SavedSearch(reader.GetInt64(0), reader.GetString(1), SavedSearchCondition.FromJson(reader.GetString(2))));
        }
        return searches;
    }

    public long Add(string name, SavedSearchCondition condition)
    {
        var cleaned = Validate(name, exceptId: null);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO saved_searches (name, condition_json, sort_order)
            VALUES ($name, $json, (SELECT COALESCE(MAX(sort_order), 0) + 1 FROM saved_searches)) RETURNING id;
            """;
        command.Parameters.AddWithValue("$name", cleaned);
        command.Parameters.AddWithValue("$json", condition.ToJson());
        return (long)command.ExecuteScalar()!;
    }

    public void Rename(long id, string name) =>
        Execute("UPDATE saved_searches SET name = $name WHERE id = $id;", ("$name", Validate(name, id)), ("$id", id));

    public void Delete(long id) => Execute("DELETE FROM saved_searches WHERE id = $id;", ("$id", id));

    public void Move(long id, int offset)
    {
        var ordered = GetAll().Select(s => s.Id).ToList();
        var index = ordered.IndexOf(id);
        var newIndex = index + offset;
        if (index < 0 || newIndex < 0 || newIndex >= ordered.Count)
        {
            return;
        }
        ordered.RemoveAt(index);
        ordered.Insert(newIndex, id);
        for (var i = 0; i < ordered.Count; i++)
        {
            Execute("UPDATE saved_searches SET sort_order = $order WHERE id = $id;", ("$order", i + 1), ("$id", ordered[i]));
        }
    }

    // 名前は空にできず、ほかの保存した検索と重ならない
    private string Validate(string name, long? exceptId)
    {
        var cleaned = NameNormalizer.Clean(name);
        if (cleaned.Length == 0)
        {
            throw new CatalogException("名前を入力してください。");
        }
        if (cleaned.Length > CatalogRepository.MaxNameLength)
        {
            throw new CatalogException($"名前は {CatalogRepository.MaxNameLength} 文字以内で入力してください。");
        }
        if (GetAll().Any(s => s.Id != exceptId && NameNormalizer.Normalize(s.Name) == NameNormalizer.Normalize(cleaned)))
        {
            throw new CatalogException("同じ名前の保存した検索がすでにあります。");
        }
        return cleaned;
    }

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        command.ExecuteNonQuery();
    }
}
