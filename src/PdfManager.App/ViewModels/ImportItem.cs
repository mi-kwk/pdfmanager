using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfManager.Core.Import;

namespace PdfManager.App.ViewModels;

public enum ImportItemStatus
{
    Preparing,
    Ready,
    Duplicate,
    Failed,
}

// 取り込み画面の一覧の 1 件
public partial class ImportItem(string sourcePath) : EditableDocument
{
    public string SourcePath { get; } = sourcePath;

    // 画像・ZIP から作った PDF のとき、元の画像・ZIP(登録後にごみ箱へ移すもの、決定ログ Y-5)。空なら SourcePath
    public IReadOnlyList<string> OriginalSources { get; init; } = [];

    // 登録後にごみ箱へ移すファイル
    public IReadOnlyList<string> Originals => OriginalSources.Count > 0 ? OriginalSources : [SourcePath];

    // 決定ログ DD-5: 保留フォルダにある PDF(一覧の上の「保留」にまとめて表示する)
    public bool IsHeld { get; init; }

    // 保留・削除予定へ移す前の場所(保留の PDF のとき。記録がなければ null)
    public string? HeldOriginalPath { get; init; }

    // 一覧のまとまり(保留 / 取り込み待ち)
    public string GroupName => IsHeld ? "保留" : "取り込み待ち";

    public override string DisplayName => Path.GetFileName(SourcePath);

    public override string OriginalFileName => DisplayName;

    public override IReadOnlyList<string> TitleCandidates => Prepared?.TitleCandidates ?? [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsReady), nameof(IsRegistrable), nameof(IsDuplicate))]
    public partial ImportItemStatus Status { get; set; } = ImportItemStatus.Preparing;

    // 登録済み・一覧の中と同じ内容(サムネイルを灰色にして「重複」と出す、2026-11-10 の利用者の要望)
    public bool IsDuplicate => Status == ImportItemStatus.Duplicate;

    // 決定ログ R-4(案B): 一覧に残したまま、取り込まない印を付ける
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsRegistrable))]
    public partial bool IsExcluded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial ImageSource? Preview { get; set; }

    public PreparedImport? Prepared { get; private set; }

    public bool IsReady => Status == ImportItemStatus.Ready;

    // 登録の対象になるもの(準備ができていて、取り込まない印がない)
    public bool IsRegistrable => IsReady && !IsExcluded;

    public string StatusText => Status switch
    {
        ImportItemStatus.Ready when IsExcluded => "登録しません",
        ImportItemStatus.Preparing => "準備中…",
        ImportItemStatus.Ready => Message is null ? "準備完了" : $"準備完了(⚠ {Message})",
        ImportItemStatus.Duplicate => $"⚠ {Message}(登録しません)",
        _ => $"✕ {Message}",
    };

    // 保留の PDF は、元の場所も出す
    public string? HeldText => IsHeld && HeldOriginalPath is { } original ? $"元の場所:{Path.GetDirectoryName(original)}" : null;

    public void ApplyPrepared(PreparedImport prepared)
    {
        Prepared = prepared;
        Message = prepared.Message;
        // 決定ログ DF-3: タイトルの初めの値はファイル名(PDF の文字は、表紙のデザインなどで意味のない文字列になることがあるため候補にだけ出す)
        Title = Path.GetFileNameWithoutExtension(SourcePath);
        Preview = prepared.PreviewImagePath is { } path ? LoadImage(path) : null;
        Status = prepared.Status switch
        {
            PreparationStatus.Ready => ImportItemStatus.Ready,
            PreparationStatus.Duplicate => ImportItemStatus.Duplicate,
            _ => ImportItemStatus.Failed,
        };
    }

    public void MarkDuplicateInList()
    {
        Message = "同じ内容のファイルが一覧にあります";
        Status = ImportItemStatus.Duplicate;
    }

    // ファイルをロックしないよう、読み込み終わったら閉じる
    private static ImageSource LoadImage(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }
}

// 決定ログ DH-4: 1 ページ目が似ている登録済みの PDF が見つかったときに選ぶこと
public enum DuplicateChoice
{
    Continue,
    Delete,
    Hold,
}
