using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PdfManager.Core.Catalog;
using PdfManager.Core.Database;
using PdfManager.Core.Documents;
using PdfManager.Core.Export;
using PdfManager.Core.Library;

namespace PdfManager.Core.Tests;

// 決定ログ EB-7〜EB-12(文書の受け渡し)
public sealed class HandoffTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pdfmanager-test-{Guid.NewGuid():N}")).FullName;
    private readonly LibraryInfo _sender;
    private readonly LibraryInfo _receiver;
    private readonly LibraryStorage _senderStorage;
    private readonly LibraryStorage _receiverStorage;

    public HandoffTests()
    {
        _sender = LibraryService.Create(Folder("PC2"), "PDF管理");
        _receiver = LibraryService.Create(Folder("PC1"), "PDF管理");
        _senderStorage = new LibraryStorage(_sender, () => [], "PC2");
        _receiverStorage = new LibraryStorage(_receiver, () => [], "PC1");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Folder(params string[] parts) => Directory.CreateDirectory(Path.Combine([_root, .. parts])).FullName;

    private static SqliteConnection Open(LibraryInfo library) => PdfManagerDatabase.Open(library.DataFolder.DatabasePath);

    private static void AddDocument(LibraryInfo library, string id, string title, string content, params long[] tagIds)
    {
        var pdf = library.DataFolder.GetPdfPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(pdf)!);
        File.WriteAllText(pdf, $"%PDF-1.4 {content}");
        using var connection = Open(library);
        new DocumentRepository(connection).Insert(
            new NewDocument(id, title, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pdf))), id + ".pdf", new FileInfo(pdf).Length, 1, title + ".pdf"),
            [new PageText(1, "本文 " + title, TextSource.Embedded)], DateTimeOffset.UtcNow.AddDays(-3), null, tagIds.Length == 0 ? null : tagIds);
    }

    private static T Scalar<T>(LibraryInfo library, string sql)
    {
        using var connection = Open(library);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    private static string TagsOf(LibraryInfo library, string id)
    {
        using var connection = Open(library);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT group_concat(name, ',') FROM (SELECT t.name FROM document_tags d JOIN tags t ON t.id = d.tag_id WHERE d.document_id = '{id}' ORDER BY t.name);";
        return command.ExecuteScalar() as string ?? "";
    }

    private ExportResult Export(IReadOnlyCollection<string> ids, bool zip = false) =>
        ExportService.Run(_senderStorage, ids, Folder("USB"), new ExportOptions(Zip: zip, WriteList: false, Handoff: true), DateTimeOffset.Now);

    [Fact]
    public async Task NewDocuments_AreRegisteredWithTagsGroupsFieldsVersionsAndLinks()
    {
        long shop, menu, recipe, field;
        using (var connection = Open(_sender))
        {
            var catalog = new CatalogRepository(connection);
            var shops = catalog.AddGroup("店", 1);
            var menus = catalog.AddGroup("メニュー", 2);
            shop = catalog.AddTag("とんこつ屋", shops);
            menu = catalog.AddTag("ラーメン", menus, [shop]);
            recipe = catalog.AddTag("レシピ");
            field = catalog.AddField("店舗コード", FieldType.Text);
        }
        AddDocument(_sender, "a1", "1巻", "a1", shop, menu);
        AddDocument(_sender, "b2", "2巻", "b2", recipe);
        AddDocument(_sender, "v1", "手順 第1版", "v1");
        AddDocument(_sender, "v2", "手順 第2版", "v2");
        using (var connection = Open(_sender))
        {
            new LinkRepository(connection).InsertNext("a1", "b2");
            new VersionRepository(connection).Group(["v1", "v2"]);
            using var command = connection.CreateCommand();
            command.CommandText = $"INSERT INTO document_field_values (document_id, field_id, value_text) VALUES ('a1', {field}, 'T-01');";
            command.ExecuteNonQuery();
        }

        // 版は 1 つ選べばまとまりごと渡す
        var exported = Export(["a1", "b2", "v2"]);
        Assert.Equal(4, exported.Exported);
        Assert.StartsWith(HandoffFormat.OutputPrefix, Path.GetFileName(exported.OutputPath));
        Assert.True(HandoffPackage.IsPackage(exported.OutputPath));

        using var package = HandoffPackage.Open(exported.OutputPath);
        var plan = HandoffImporter.Plan(_receiverStorage, package);
        Assert.Equal(4, plan.NewDocuments.Count);
        Assert.Equal(["店", "メニュー"], plan.NewGroups);
        Assert.Contains("ラーメン", plan.NewTags);
        Assert.Equal(["店舗コード"], plan.NewFields);
        Assert.False(plan.SameLibrary);

        var result = await HandoffImporter.ImportAsync(_receiverStorage, package, plan, new Dictionary<string, HandoffChoice>(), DateTimeOffset.Now);

        Assert.Equal(4, result.Added);
        Assert.Empty(result.Problems);
        Assert.True(File.Exists(_receiver.DataFolder.GetPdfPath("a1")));
        Assert.Equal("1巻", Scalar<string>(_receiver, "SELECT title FROM documents WHERE id = 'a1';"));
        Assert.Equal("とんこつ屋,ラーメン", TagsOf(_receiver, "a1"));
        Assert.Equal(1L, Scalar<long>(_receiver, "SELECT COUNT(*) FROM tag_links l JOIN tags c ON c.id = l.child_id JOIN tags p ON p.id = l.parent_id WHERE c.name = 'ラーメン' AND p.name = 'とんこつ屋';"));
        Assert.Equal(2L, Scalar<long>(_receiver, "SELECT rank FROM tag_groups WHERE name = 'メニュー';"));
        Assert.Equal("T-01", Scalar<string>(_receiver, "SELECT value_text FROM document_field_values WHERE document_id = 'a1';"));
        Assert.Equal("b2", Scalar<string>(_receiver, "SELECT to_id FROM document_links WHERE from_id = 'a1' AND type = 'next';"));
        Assert.Equal(2L, Scalar<long>(_receiver, "SELECT version_no FROM documents WHERE id = 'v2';"));
        Assert.Equal(0L, Scalar<long>(_receiver, "SELECT view_count FROM documents WHERE id = 'a1';"));
        Assert.Equal(1L, Scalar<long>(_receiver, "SELECT COUNT(*) FROM page_texts WHERE document_id = 'a1';"));
        // 決定ログ EB-12: 渡した側はそのまま
        Assert.Equal(4L, Scalar<long>(_sender, "SELECT COUNT(*) FROM documents;"));

        // もう一度取り込んでも、同じものは増えない
        var again = HandoffImporter.Plan(_receiverStorage, package);
        Assert.Empty(again.NewDocuments);
        Assert.Empty(again.Different);
        Assert.Equal(4, again.Same);
    }

    [Fact]
    public async Task SamePdf_DefaultAddsTags_OverwriteFollowsSender_KeepLeaves()
    {
        long senderTag, receiverTag;
        using (var connection = Open(_sender))
        {
            senderTag = new CatalogRepository(connection).AddTag("重要");
        }
        using (var connection = Open(_receiver))
        {
            receiverTag = new CatalogRepository(connection).AddTag("受け取り側のタグ");
            new CatalogRepository(connection).AddTag("重要");
        }
        AddDocument(_sender, "a1", "PC2 のタイトル", "same-a", senderTag);
        AddDocument(_sender, "b2", "PC2 のタイトル B", "same-b", senderTag);
        AddDocument(_sender, "c3", "PC2 のタイトル C", "same-c", senderTag);
        // 受け取る側には、同じ中身の PDF が別の ID で入っている
        AddDocument(_receiver, "x1", "PC1 のタイトル", "same-a", receiverTag);
        AddDocument(_receiver, "x2", "PC1 のタイトル B", "same-b", receiverTag);
        AddDocument(_receiver, "x3", "PC1 のタイトル C", "same-c", receiverTag);

        var exported = Export(["a1", "b2", "c3"], zip: true);
        Assert.EndsWith(".zip", exported.OutputPath);
        Assert.True(HandoffPackage.IsPackage(exported.OutputPath));
        using var package = HandoffPackage.Open(exported.OutputPath);
        var plan = HandoffImporter.Plan(_receiverStorage, package);
        Assert.Empty(plan.NewDocuments);
        Assert.Equal(3, plan.Different.Count);
        Assert.Contains(plan.Different[0].Differences, d => d.StartsWith("タイトル"));

        var choices = new Dictionary<string, HandoffChoice> { ["b2"] = HandoffChoice.Overwrite, ["c3"] = HandoffChoice.Keep };
        var result = await HandoffImporter.ImportAsync(_receiverStorage, package, plan, choices, DateTimeOffset.Now);

        Assert.Equal(2, result.Updated);
        Assert.Equal("PC1 のタイトル", Scalar<string>(_receiver, "SELECT title FROM documents WHERE id = 'x1';"));
        Assert.Equal("受け取り側のタグ,重要", TagsOf(_receiver, "x1"));
        Assert.Equal("PC2 のタイトル B", Scalar<string>(_receiver, "SELECT title FROM documents WHERE id = 'x2';"));
        Assert.Equal("重要", TagsOf(_receiver, "x2"));
        Assert.Equal("PC1 のタイトル C", Scalar<string>(_receiver, "SELECT title FROM documents WHERE id = 'x3';"));
        Assert.Equal("受け取り側のタグ", TagsOf(_receiver, "x3"));
        Assert.Equal(3L, Scalar<long>(_receiver, "SELECT COUNT(*) FROM documents;"));
        // ZIP を展開した作業用のフォルダは、閉じると消える
        var folder = package.Folder;
        package.Dispose();
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void ChangedSince_ListsDocumentsAddedOrEditedAfterLastHandoff()
    {
        AddDocument(_sender, "a1", "古い", "a1");
        AddDocument(_sender, "b2", "あとで直した", "b2");
        var last = DateTimeOffset.UtcNow.AddDays(-1);
        using var connection = Open(_sender);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE documents SET updated_at = $now WHERE id = 'b2';";
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
            command.ExecuteNonQuery();
        }

        Assert.Equal(["a1", "b2"], HandoffFormat.ChangedSince(connection, null).Order());
        // b2 は登録日を古くしたが、変更日が前回のあと
        Assert.Equal(["b2"], HandoffFormat.ChangedSince(connection, last));
    }

    [Fact]
    public void Open_RejectsFolderWithoutManifest()
    {
        var folder = Folder("not-handoff");
        Assert.False(HandoffPackage.IsPackage(folder));
        Assert.Throws<LibraryException>(() => HandoffPackage.Open(folder));
    }
}
