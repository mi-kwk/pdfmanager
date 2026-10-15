using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfManager.Core.Images;

namespace PdfManager.App.ViewModels;

// 画像から作った PDF。登録後にごみ箱へ移すのは OriginalSources(元の画像・ZIP、決定ログ Y-5)
public sealed record CreatedPdf(string Path, IReadOnlyList<string> OriginalSources);

// 縮小の選択肢(決定ログ Y-3)
public sealed record ImageSizeChoice(string Label, int? MaxLongSide)
{
    // 選択欄の読み上げに使う
    public override string ToString() => Label;
}

// PDF の 1 ページになる画像
public sealed partial class ImagePdfPage(ImageFrame frame, int frameCount) : ObservableObject
{
    public const int ThumbnailSize = 160;

    public ImageFrame Frame { get; } = frame;

    public string DisplayName => frameCount > 1 ? $"{Frame.DisplayName}({Frame.FrameIndex + 1}/{frameCount} ページ)" : Frame.DisplayName;

    // 1 枚ずつ PDF にするときの名前
    public string BaseName => Path.GetFileNameWithoutExtension(Frame.DisplayName) + (frameCount > 1 ? $"_{Frame.FrameIndex + 1}" : "");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Angle))]
    public partial int QuarterTurns { get; set; }

    public double Angle => QuarterTurns * 90;

    [ObservableProperty]
    public partial ImageSource? Thumbnail { get; set; }

    [ObservableProperty]
    public partial string? Problem { get; set; }
}

// できる PDF 1 つ分。直接選んだ画像はまとめて 1 つ、ZIP は ZIP ごとに 1 つ(決定ログ Y-1)
public sealed partial class ImagePdfGroup : ObservableObject
{
    public ImagePdfGroup(string? zipPath, IReadOnlyList<string> imagePaths)
    {
        ZipPath = zipPath;
        ImagePaths = imagePaths;
        Pages.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Header));
            OnPropertyChanged(nameof(Name));
        };
    }

    public string? ZipPath { get; }

    public IReadOnlyList<string> ImagePaths { get; }

    public ObservableCollection<ImagePdfPage> Pages { get; } = [];

    // 決定ログ Y-5: ZIP の名前、または 1 枚目の画像の名前
    public string Name => ZipPath is not null
        ? Path.GetFileNameWithoutExtension(ZipPath)
        : Path.GetFileNameWithoutExtension(Pages.FirstOrDefault()?.Frame.Path ?? ImagePaths.FirstOrDefault() ?? "");

    public string Kind => ZipPath is not null ? "ZIP" : "選んだ画像";

    public string Header => Problem ?? $"{Pages.Count} 枚";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    public partial string? Problem { get; set; }
}

// SC-05 画像から PDF 作成(F-26)
public sealed partial class ImagePdfViewModel : ObservableObject
{
    private readonly string _workFolder;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<CreatedPdf> _created = [];

    // workFolder: 取り出した画像と作った PDF を置く一時フォルダ(取り込み画面を閉じると消える)
    public ImagePdfViewModel(IReadOnlyList<string> images, IReadOnlyList<string> zips, string workFolder)
    {
        _workFolder = workFolder;
        if (images.Count > 0)
        {
            Groups.Add(new ImagePdfGroup(null, images));
        }
        foreach (var zip in zips)
        {
            Groups.Add(new ImagePdfGroup(zip, []));
        }
        SelectedGroup = Groups.FirstOrDefault();
        SizeChoices = ImagePdfBuilder.MaxLongSideChoices
            .Select(v => new ImageSizeChoice(v is { } px ? $"長辺 {px:N0} ピクセルまで縮小" : "縮小しない(そのまま)", v))
            .ToList();
        SelectedSize = SizeChoices.First(c => c.MaxLongSide == ImagePdfBuilder.DefaultMaxLongSide);
    }

    public ObservableCollection<ImagePdfGroup> Groups { get; } = [];

    public bool HasManyGroups => Groups.Count > 1;

    public IReadOnlyList<ImageSizeChoice> SizeChoices { get; }

    public IReadOnlyList<CreatedPdf> Created => _created;

    [ObservableProperty]
    public partial ImagePdfGroup? SelectedGroup { get; set; }

    [ObservableProperty]
    public partial ImageSizeChoice SelectedSize { get; set; }

    // 決定ログ Y-1: 1 枚ずつ別の PDF にする
    [ObservableProperty]
    public partial bool OnePdfPerImage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    public partial bool IsCreating { get; set; }

    public bool IsBusy => IsLoading || IsCreating;

    [ObservableProperty]
    public partial string ProgressText { get; set; } = "";

    [ObservableProperty]
    public partial double ProgressValue { get; set; }

    [ObservableProperty]
    public partial double ProgressMaximum { get; set; } = 1;

    public int PdfCount => OnePdfPerImage ? Groups.Sum(g => g.Pages.Count) : Groups.Count(g => g.Pages.Count > 0);

    public string CreateText => $"PDF を作成({PdfCount} 件)";

    partial void OnOnePdfPerImageChanged(bool value) => NotifyCounts();

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(PdfCount));
        OnPropertyChanged(nameof(CreateText));
    }

    // ==== 読み込み ====

    public async Task LoadAsync()
    {
        IsLoading = true;
        ProgressText = "画像を読み込んでいます…";
        var token = _cancellation.Token;
        try
        {
            foreach (var group in Groups)
            {
                try
                {
                    var files = group.ZipPath is { } zip
                        ? await Task.Run(() => ImageSources.ExtractZipImages(zip, NewWorkFolder("zip"), token), token)
                        : group.ImagePaths.Select(p => new ImageFrame(p, 0, Path.GetFileName(p))).ToList();
                    foreach (var file in files)
                    {
                        var count = await FrameCountAsync(file.Path);
                        for (var i = 0; i < count; i++)
                        {
                            group.Pages.Add(new ImagePdfPage(file with { FrameIndex = i }, count));
                        }
                    }
                    if (group.Pages.Count == 0)
                    {
                        group.Problem = "画像がありません";
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    group.Problem = $"読み込めません:{ex.Message}";
                }
                NotifyCounts();
            }
        }
        finally
        {
            IsLoading = false;
            ProgressText = "";
        }
        await LoadThumbnailsAsync(token);
    }

    // 複数ページの TIFF は 1 ページずつ並べる。読めない画像は 1 枚として並べ、サムネイルの代わりに理由を表示する
    private static async Task<int> FrameCountAsync(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }
        try
        {
            return Math.Max(1, await Task.Run(() => ImagePdfBuilder.GetFrameCountAsync(path)));
        }
        catch (Exception)
        {
            return 1;
        }
    }

    private async Task LoadThumbnailsAsync(CancellationToken token)
    {
        foreach (var page in Groups.SelectMany(g => g.Pages).ToList())
        {
            if (token.IsCancellationRequested)
            {
                return;
            }
            try
            {
                var bytes = await Task.Run(() => ImagePdfBuilder.CreateThumbnailAsync(page.Frame.Path, page.Frame.FrameIndex, ImagePdfPage.ThumbnailSize), token);
                page.Thumbnail = ToImage(bytes);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                page.Problem = $"読み込めません:{ex.Message}";
            }
        }
    }

    private static BitmapImage ToImage(byte[] bytes)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(bytes);
        image.EndInit();
        image.Freeze();
        return image;
    }

    // ==== 並び替え・回転・除外(決定ログ Y-2) ====

    public void MoveUp(IReadOnlyList<ImagePdfPage> selected) => Move(selected, -1);

    public void MoveDown(IReadOnlyList<ImagePdfPage> selected) => Move(selected, 1);

    private void Move(IReadOnlyList<ImagePdfPage> selected, int step)
    {
        if (SelectedGroup is not { } group || selected.Count == 0)
        {
            return;
        }
        var indexes = selected.Select(p => group.Pages.IndexOf(p)).Where(i => i >= 0).Order().ToList();
        if (indexes.Count == 0 || (step < 0 ? indexes[0] == 0 : indexes[^1] == group.Pages.Count - 1))
        {
            return;
        }
        foreach (var index in step < 0 ? indexes : Enumerable.Reverse(indexes))
        {
            group.Pages.Move(index, index + step);
        }
    }

    public static void Rotate(IReadOnlyList<ImagePdfPage> selected, int quarterTurns)
    {
        foreach (var page in selected)
        {
            page.QuarterTurns = (page.QuarterTurns + quarterTurns + 4) % 4;
        }
    }

    public void Exclude(IReadOnlyList<ImagePdfPage> selected)
    {
        foreach (var page in selected)
        {
            SelectedGroup?.Pages.Remove(page);
        }
        NotifyCounts();
    }

    // ==== 作成 ====

    // 作った PDF は Created に入る。うまく作れなかったものの説明を返す
    public async Task<IReadOnlyList<string>> CreateAsync()
    {
        var size = SelectedSize.MaxLongSide;
        var jobs = new List<(string Name, IReadOnlyList<ImagePdfPage> Pages, IReadOnlyList<string> Originals)>();
        foreach (var group in Groups.Where(g => g.Pages.Count > 0))
        {
            if (OnePdfPerImage)
            {
                foreach (var page in group.Pages)
                {
                    jobs.Add((page.BaseName, [page], group.ZipPath is { } zip ? [zip] : [page.Frame.Path]));
                }
            }
            else
            {
                jobs.Add((group.Name, group.Pages.ToList(),
                    group.ZipPath is { } zip ? [zip] : group.Pages.Select(p => p.Frame.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
            }
        }

        var problems = new List<string>();
        IsCreating = true;
        ProgressMaximum = Math.Max(1, jobs.Sum(j => j.Pages.Count));
        ProgressValue = 0;
        var done = 0;
        try
        {
            foreach (var (name, pages, originals) in jobs)
            {
                ProgressText = $"「{name}」を作成しています…";
                var offset = done;
                var progress = new Progress<int>(n => ProgressValue = offset + n);
                var path = Path.Combine(NewWorkFolder("pdf"), name + ".pdf");
                var imagePages = pages.Select(p => new ImagePage(p.Frame.Path, p.Frame.FrameIndex, p.QuarterTurns)).ToList();
                try
                {
                    await Task.Run(() => ImagePdfBuilder.BuildAsync(imagePages, size, path, progress, _cancellation.Token));
                    _created.Add(new CreatedPdf(path, originals));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    problems.Add($"・{name}:{ex.Message}");
                }
                done += pages.Count;
            }
        }
        finally
        {
            IsCreating = false;
            ProgressText = "";
        }
        return problems;
    }

    public void Cancel() => _cancellation.Cancel();

    private string NewWorkFolder(string kind) => Directory.CreateDirectory(Path.Combine(_workFolder, $"{kind}-{Guid.NewGuid():N}")).FullName;
}
