using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfManager.Core.Database;
using PdfManager.Core.Documents;
using PdfManager.Core.Pdf;
using PdfManager.Core.Search;
using PdfManager.Core.Storage;

namespace PdfManager.App.Services;

// 1 ページ目が似ている登録済みの文書 1 件(画面に出すもの、決定ログ DH-4〜DH-6)
public sealed record DuplicateMatch(string DocumentId, string Title, string FileName, DateTimeOffset CreatedAt, ImageSource? Thumbnail, double Similarity)
{
    public string SimilarityText => $"{Similarity * 100:0}% 同じ";

    public string CreatedText => $"登録日 {CreatedAt.ToLocalTime():yyyy-MM-dd}";
}

// 決定ログ DH-1〜DH-6: 1 ページ目の見た目で、登録済みの文書と比べる(取り込み画面・削除予定の確認画面・重複チェック画面で共通)
public static class DuplicateLookup
{
    // 指紋のない登録済みの文書(この機能より前に登録したもの)の指紋を作る。初めの 1 回は時間がかかる
    public static Task<int> PrepareAsync(DataFolder dataFolder, IProgress<(int Done, int Total)>? progress = null) =>
        Task.Run(() => DuplicateFinder.EnsureFingerprintsAsync(dataFolder, progress));

    // tagIds のどれかが付いている文書(null なら全件)と、pdfPath の 1 ページ目を比べる
    public static async Task<IReadOnlyList<DuplicateMatch>> FindAsync(DataFolder dataFolder, string pdfPath, IReadOnlyCollection<long>? tagIds, bool loose)
    {
        var fingerprint = await Task.Run(() => PageFingerprint.FromPdfAsync(pdfPath));
        return await Task.Run(() =>
        {
            using var connection = PdfManagerDatabase.Open(dataFolder.DatabasePath);
            var candidates = DuplicateFinder.Load(connection, tagIds is null ? null : DocumentsWithTags(connection, tagIds));
            return Describe(dataFolder, connection, DuplicateFinder.FindSimilar(fingerprint, candidates, DuplicateFinder.Threshold(loose)));
        });
    }

    public static HashSet<string> DocumentsWithTags(Microsoft.Data.Sqlite.SqliteConnection connection, IReadOnlyCollection<long> tagIds)
    {
        var ids = new HashSet<string>();
        foreach (var tagId in tagIds)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT document_id FROM document_tags WHERE tag_id = $tag;";
            command.Parameters.AddWithValue("$tag", tagId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetString(0));
            }
        }
        return ids;
    }

    public static IReadOnlyList<DuplicateMatch> Describe(DataFolder dataFolder, Microsoft.Data.Sqlite.SqliteConnection connection, IReadOnlyList<SimilarDocument> similar)
    {
        var details = new DocumentRepository(connection).GetDetails(similar.Select(s => s.DocumentId).ToList()).ToDictionary(d => d.Id);
        return similar
            .Where(s => details.ContainsKey(s.DocumentId))
            .Select(s =>
            {
                var detail = details[s.DocumentId];
                return new DuplicateMatch(s.DocumentId, detail.Title, detail.FileName, detail.CreatedAt, LoadImage(dataFolder.GetThumbnailPath(s.DocumentId)), s.Similarity);
            })
            .ToList();
    }

    // ファイルをロックしないよう、読み込み終わったら閉じる
    public static ImageSource? LoadImage(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            return null;
        }
    }
}
