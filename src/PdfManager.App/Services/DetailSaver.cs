using Microsoft.Data.Sqlite;
using PdfManager.App.ViewModels;
using PdfManager.Core.Catalog;
using PdfManager.Core.Database;
using PdfManager.Core.Documents;
using PdfManager.Core.Library;
using PdfManager.Core.Storage;

namespace PdfManager.App.Services;

// 登録済みの文書の編集内容を保存する(詳細エリアと、タグの付け漏れ確認の画面で共通)
public static class DetailSaver
{
    private const int MaxReportedProblems = 5;

    // 保存できなければ false(理由は表示済み)。extra は同じ変更の中で一緒に行う処理(版の名前など)
    public static async Task<bool> SaveAsync(DataFolder dataFolder, StorageAccess storage, IUserDialogs dialogs, DocumentEditor editor,
        IReadOnlyList<DetailItem> items, Action<SqliteConnection>? extra = null)
    {
        var ids = items.Select(i => i.Detail.Id).ToList();
        // 決定ログ S-12: つながっていないサブの文書は編集できない
        if (StorageIdsOf(dataFolder, ids).Values.Distinct().FirstOrDefault(s => !storage.Storage.IsAvailable(s)) is { } unavailable)
        {
            await dialogs.ShowErrorAsync(storage.Storage.UnavailableMessage(unavailable), "保存できません");
            return false;
        }
        var parsed = new Dictionary<EditableDocument, Dictionary<long, FieldValue>>();
        var errors = editor.ParseFieldValues(items, parsed);
        if (errors.Count > 0)
        {
            await dialogs.ShowErrorAsync("次の項目の入力を直してから保存してください。\n\n" + string.Join("\n", errors.Take(MaxReportedProblems)));
            return false;
        }
        // 決定ログ R-10・T-5・T-7: ファイル名を変えて同じ名前になるときは、確認せずに「_2」などを付ける
        var names = editor.AssignNames(items.Cast<EditableDocument>().ToList());

        var token = await storage.RunAsync(s => s.BeginChange(ids));
        using (var connection = PdfManagerDatabase.Open(dataFolder.DatabasePath))
        {
            var tagIds = editor.ResolveTagIds(new CatalogRepository(connection), items.SelectMany(i => i.TagKeys));
            var repository = new DocumentRepository(connection);
            foreach (var item in items)
            {
                var (source, sourceId) = editor.FileNameSourceFor(item, tagIds);
                repository.Update(new DocumentUpdate(
                    item.Detail.Id, names[item].Title, item.Memo, item.Rating, names[item].FileName,
                    source, sourceId, item.UseTitleAsFileName ? "" : item.Suffix, parsed[item],
                    item.TagKeys.Select(k => tagIds[k]).ToList()), DateTimeOffset.UtcNow);
            }
            extra?.Invoke(connection);
        }
        // サブへ書き込めなかったことは知らせるが、メインの変更はそのまま
        var problems = await storage.RunAsync(s => s.CompleteChange(token));
        if (problems.Count > 0)
        {
            await dialogs.ShowErrorAsync(string.Join("\n\n", problems.Take(MaxReportedProblems)), "サブライブラリ");
        }
        return true;
    }

    private static IReadOnlyDictionary<string, string?> StorageIdsOf(DataFolder dataFolder, IEnumerable<string> documentIds)
    {
        using var connection = PdfManagerDatabase.Open(dataFolder.DatabasePath);
        return new SubLibraryRepository(connection).GetStorageIds(documentIds);
    }
}
