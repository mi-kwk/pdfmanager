using PdfManager.Core.Database;
using PdfManager.Core.Storage;
using PdfManager.Core.Text;

namespace PdfManager.Core.Documents;

// docs/04「削除」(決定ログ K-5)
public sealed class DocumentRemover(DataFolder dataFolder, Action<string>? disposeFile = null)
{
    // テストではごみ箱の代わりに別の処理を渡せる
    private readonly Action<string> _disposeFile = disposeFile ?? DisposeToRecycleBin;

    public bool UsesRecycleBin => RecycleBin.IsAvailableFor(dataFolder.Root);

    // PDF をごみ箱へ移せなかった場合は例外を投げ、データベースからは削除しない。
    // サブライブラリにある文書は storageFolder にサブの場所を渡す(サブの写しの削除は呼び出し側で行う、決定ログ S-12)
    public void Delete(string documentId, string fileName, DataFolder? storageFolder = null)
    {
        var stored = (storageFolder ?? dataFolder).GetPdfPath(documentId);
        if (File.Exists(stored))
        {
            // ごみ箱で中身がわかるよう、利用者向けのファイル名に変えてから移す
            var named = UniquePath(Path.GetDirectoryName(stored)!, FileNames.Sanitize(Path.GetFileNameWithoutExtension(fileName), documentId));
            File.Move(stored, named);
            try
            {
                _disposeFile(named);
            }
            catch
            {
                File.Move(named, stored);
                throw;
            }
        }

        foreach (var thumbnail in new[] { dataFolder.GetThumbnailPath(documentId), (storageFolder ?? dataFolder).GetThumbnailPath(documentId) }.Distinct())
        {
            if (File.Exists(thumbnail))
            {
                File.Delete(thumbnail);
            }
        }

        using var connection = PdfManagerDatabase.Open(dataFolder.DatabasePath);
        new DocumentRepository(connection).Delete(documentId);
        // 決定ログ N-7: 残った版はそのまま(1 つ前の版が最新版になる)。空になった版系列だけ消す
        new VersionRepository(connection).DeleteEmptySeries();
    }

    private static string UniquePath(string folder, string baseName)
    {
        var path = Path.Combine(folder, baseName + ".pdf");
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{baseName} ({n}).pdf");
        }
        return path;
    }

    private static void DisposeToRecycleBin(string path)
    {
        if (RecycleBin.IsAvailableFor(path))
        {
            RecycleBin.MoveFile(path);
        }
        else
        {
            File.Delete(path);
        }
    }
}
