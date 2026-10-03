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

    public override string DisplayName => Path.GetFileName(SourcePath);

    public override string OriginalFileName => DisplayName;

    public override IReadOnlyList<string> TitleCandidates => Prepared?.TitleCandidates ?? [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsReady))]
    public partial ImportItemStatus Status { get; set; } = ImportItemStatus.Preparing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial ImageSource? Preview { get; set; }

    public PreparedImport? Prepared { get; private set; }

    public bool IsReady => Status == ImportItemStatus.Ready;

    public string StatusText => Status switch
    {
        ImportItemStatus.Preparing => "準備中…",
        ImportItemStatus.Ready => Message is null ? "準備完了" : $"準備完了(⚠ {Message})",
        ImportItemStatus.Duplicate => $"⚠ {Message}(登録しません)",
        _ => $"✕ {Message}",
    };

    public void ApplyPrepared(PreparedImport prepared)
    {
        Prepared = prepared;
        Message = prepared.Message;
        Title = prepared.TitleCandidates.FirstOrDefault() ?? "";
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
