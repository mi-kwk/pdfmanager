#:project ../src/PdfManager.Core/PdfManager.Core.csproj
#:package PdfPig@0.1.16
#:property TargetFramework=net10.0-windows10.0.19041.0
#:property PublishAot=false

// 性能確認用のダミーライブラリを作る(docs/06、決定ログ L-7)。実際の業務の PDF は使わない。
// 使い方: dotnet run tools/make-dummy-library.cs -- <作成先フォルダ> <件数>
using System.Diagnostics;
using PdfManager.Core.Catalog;
using PdfManager.Core.Database;
using PdfManager.Core.Documents;
using PdfManager.Core.Library;
using PdfManager.Core.Pdf;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

if (args.Length < 2 || !int.TryParse(args[1], out var count))
{
    Console.WriteLine("使い方: dotnet run tools/make-dummy-library.cs -- <作成先フォルダ> <件数>");
    return 1;
}
var parent = Directory.CreateDirectory(args[0]).FullName;
var library = LibraryService.Create(parent, $"ダミー{count}件");
var random = new Random(20261003);
var watch = Stopwatch.StartNew();

// 1 つの PDF とサムネイルを作り、全件でコピーして使う(取り込みの速さではなく、検索・表示の速さを測るため)
var templatePdf = Path.Combine(parent, "template.pdf");
var builder = new PdfDocumentBuilder();
var font = builder.AddStandard14Font(Standard14Font.Helvetica);
builder.AddPage(PageSize.A4).AddText("Dummy document for performance test", 20, new PdfPoint(50, 760), font);
File.WriteAllBytes(templatePdf, builder.Build());
var templateThumb = Path.Combine(parent, "template.jpg");
await PdfPageRenderer.RenderFirstPageToJpegAsync(templatePdf, templateThumb);

using var connection = PdfManagerDatabase.Open(library.DataFolder.DatabasePath);
var catalog = new CatalogRepository(connection);
// エリア(順位 1、項目)とマネージャー(順位 2、エリアに紐づく)のタググループ(決定ログ R-11)
var areaGroup = catalog.AddGroup("エリア", 1);
catalog.SetGroupPinned(areaGroup, true);
var managerGroup = catalog.AddGroup("マネージャー", 2);
string[] areas = ["上野エリア", "秋葉原エリア", "新宿エリア", "渋谷エリア", "池袋エリア", "品川エリア"];
var areaTags = areas.Select(a => catalog.AddTag(a, areaGroup)).ToArray();
var month = catalog.AddField("年月", FieldType.YearMonth);
var amount = catalog.AddField("金額", FieldType.Number);
var store = catalog.AddField("店舗", FieldType.Text);

var managers = Enumerable.Range(1, 30).Select(i =>
{
    return catalog.AddTag($"担当{i:00}マネージャー", managerGroup, [areaTags[i % areaTags.Length]]);
}).ToArray();
string[] otherTagNames = ["IODATA", "要確認", "重要", "繁盛エリア", "蕎麦屋", "ラーメン", "カフェ", "物販", "保管期限あり", "社外秘"];
var otherTags = otherTagNames.Select(n => catalog.AddTag(n)).ToArray();

string[] kinds = ["売上報告", "月次報告", "稟議書", "取扱説明書", "仕様書", "議事録", "見積書", "請求書"];
var repository = new DocumentRepository(connection);
var start = DateTimeOffset.Now.AddYears(-3);
for (var i = 0; i < count; i++)
{
    var id = Guid.NewGuid().ToString("D");
    var areaIndex = random.Next(areas.Length);
    var created = start.AddMinutes(random.Next(3 * 365 * 24 * 60));
    var kind = kinds[random.Next(kinds.Length)];
    var title = $"{areas[areaIndex].Replace("エリア", "")}{random.Next(1, 40)}号店 {kind} {created:yyyy年M月}";
    var fileName = $"{kind}-{i:000000}.pdf";

    var tags = new HashSet<long> { areaTags[areaIndex], managers[random.Next(managers.Length)] };
    for (var t = random.Next(3); t > 0; t--)
    {
        tags.Add(otherTags[random.Next(otherTags.Length)]);
    }
    var values = new Dictionary<long, FieldValue>
    {
        [month] = new(null, null, $"{created:yyyy-MM}-01"),
        [store] = new($"{random.Next(1, 40)}号店", null, null),
    };
    if (random.Next(3) > 0)
    {
        values[amount] = new(null, random.Next(1000, 5_000_000), null);
    }

    repository.Insert(new NewDocument(id, title, $"dummy-{i}", fileName, 1024, 1, fileName),
        [new PageText(1, title, TextSource.Embedded)], created, values, tags);
    var stored = library.DataFolder.GetPdfPath(id);
    Directory.CreateDirectory(Path.GetDirectoryName(stored)!);
    File.Copy(templatePdf, stored);
    var thumb = library.DataFolder.GetThumbnailPath(id);
    Directory.CreateDirectory(Path.GetDirectoryName(thumb)!);
    File.Copy(templateThumb, thumb);

    var rating = random.Next(6);
    var views = random.Next(4) == 0 ? 0 : random.Next(1, 30);
    using var command = connection.CreateCommand();
    command.CommandText = "UPDATE documents SET rating = $r, view_count = $v, last_viewed_at = $l WHERE id = $id;";
    command.Parameters.AddWithValue("$r", rating);
    command.Parameters.AddWithValue("$v", views);
    var viewed = created.AddDays(random.Next(1, 300));
    command.Parameters.AddWithValue("$l", views == 0 ? DBNull.Value : (viewed < DateTimeOffset.Now ? viewed : DateTimeOffset.Now).UtcDateTime.ToString("O"));
    command.Parameters.AddWithValue("$id", id);
    command.ExecuteNonQuery();

    if ((i + 1) % 5000 == 0)
    {
        Console.WriteLine($"{i + 1:N0} 件 ({watch.Elapsed.TotalSeconds:0} 秒)");
    }
}
File.Delete(templatePdf);
File.Delete(templateThumb);
Console.WriteLine($"完了: {count:N0} 件 ({watch.Elapsed.TotalSeconds:0} 秒)");
Console.WriteLine(library.LibraryFilePath);
return 0;
