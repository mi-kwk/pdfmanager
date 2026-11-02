using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfManager.App.Services;
using PdfManager.Core.Database;
using PdfManager.Core.Documents;
using PdfManager.Core.Pdf;
using PdfManager.Core.Storage;
using PdfManager.Core.Text;

namespace PdfManager.App.ViewModels;

// タイトルの付け直し(編集)の 1 行
public sealed partial class TitleEditRow(TitleRow row, Action changed) : ObservableObject
{
    public string Id => row.Id;

    public string CurrentTitle => row.Title;

    public string? StorageId => row.StorageId;

    public ImageSource? Thumbnail => row.Thumbnail;

    // 空なら変えない
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged), nameof(EffectiveTitle))]
    public partial string NewTitle { get; set; } = "";

    [ObservableProperty]
    public partial bool IsConflict { get; set; }

    public string EffectiveTitle => NameNormalizer.Clean(NewTitle) is { Length: > 0 } title ? title : CurrentTitle;

    public bool IsChanged => NameNormalizer.Clean(NewTitle) is { Length: > 0 } title && title != CurrentTitle;

    partial void OnNewTitleChanged(string value) => changed();
}

// SC-19 タイトルの付け直し(編集、決定ログ DI-3〜DI-8)。保存するまでは下書きで、重なりが残っている間は保存しない
public sealed partial class TitleBatchEditViewModel : ObservableObject
{
    private const int MaxListedChanges = 20;

    private readonly DataFolder _dataFolder;
    private readonly StorageAccess _storage;
    private readonly IUserDialogs _dialogs;
    private bool _suspendCheck;

    public TitleBatchEditViewModel(DataFolder dataFolder, StorageAccess storage, IUserDialogs dialogs, IReadOnlyList<TitleRow> rows)
    {
        _dataFolder = dataFolder;
        _storage = storage;
        _dialogs = dialogs;
        foreach (var row in rows)
        {
            Rows.Add(new TitleEditRow(row, Recheck));
        }
        var first = rows.FirstOrDefault()?.Title ?? "";
        NumberStem = (TitleNumbers.SplitTrailingNumber(first)?.Stem ?? first).TrimEnd();
        Selected = Rows.FirstOrDefault();
        Recheck();
    }

    public ObservableCollection<TitleEditRow> Rows { get; } = [];

    // 番号を振るときの名前と、始めの番号(決定ログ DI-6)
    [ObservableProperty]
    public partial string NumberStem { get; set; } = "";

    [ObservableProperty]
    public partial string StartNumber { get; set; } = "1";

    [ObservableProperty]
    public partial int ChangeCount { get; set; }

    [ObservableProperty]
    public partial int ConflictCount { get; set; }

    public string CountText => ConflictCount > 0
        ? $"変更 {ChangeCount} 件・重なり {ConflictCount} 件(重なりをなくすと保存できます)"
        : $"変更 {ChangeCount} 件";

    public bool Saved { get; private set; }

    public bool HasChanges => ChangeCount > 0;

    // ==== 右の大きな表示 ====

    [ObservableProperty]
    public partial TitleEditRow? Selected { get; set; }

    [ObservableProperty]
    public partial ImageSource? Preview { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    public partial int PageIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    public partial int PageCount { get; set; }

    public string PageText => PageCount == 0 ? "" : $"{PageIndex + 1} / {PageCount} ページ";

    partial void OnSelectedChanged(TitleEditRow? value)
    {
        PageIndex = 0;
        _ = ShowPreviewAsync();
    }

    [RelayCommand]
    private Task PreviousPageAsync()
    {
        if (PageIndex <= 0)
        {
            return Task.CompletedTask;
        }
        PageIndex--;
        return ShowPreviewAsync();
    }

    [RelayCommand]
    private Task NextPageAsync()
    {
        if (PageIndex + 1 >= PageCount)
        {
            return Task.CompletedTask;
        }
        PageIndex++;
        return ShowPreviewAsync();
    }

    // PDF を読めないとき(つながっていないサブなど)はサムネイルを出す
    private async Task ShowPreviewAsync()
    {
        if (Selected is not { } row)
        {
            Preview = null;
            PageCount = 0;
            return;
        }
        var pdf = _storage.Storage.GetDataFolder(row.StorageId)?.GetPdfPath(row.Id);
        try
        {
            if (pdf is null || !File.Exists(pdf))
            {
                throw new FileNotFoundException();
            }
            var (count, jpeg) = await PdfPageRenderer.RenderPageToJpegAsync(pdf, PageIndex, 1200, 1200);
            if (Selected == row)
            {
                PageCount = count;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(jpeg);
                image.EndInit();
                image.Freeze();
                Preview = image;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Preview = row.Thumbnail;
            PageCount = 0;
        }
    }

    // ==== 並べ替えと番号(決定ログ DI-6) ====

    public void Move(IReadOnlyList<TitleEditRow> rows, int offset)
    {
        var ordered = rows.OrderBy(Rows.IndexOf).ToList();
        if (offset > 0)
        {
            ordered.Reverse();
        }
        foreach (var row in ordered)
        {
            var index = Rows.IndexOf(row);
            var target = index + offset;
            if (target < 0 || target >= Rows.Count || rows.Contains(Rows[target]))
            {
                continue;
            }
            Rows.Move(index, target);
        }
    }

    public void MoveTo(TitleEditRow row, int index)
    {
        var from = Rows.IndexOf(row);
        if (from >= 0 && index >= 0 && index < Rows.Count && from != index)
        {
            Rows.Move(from, index);
        }
    }

    // 選んだ行(なければ全部)に、上から順に「名前 + 番号」を入れる。数字の書き方はシリーズの一番大きい番号に合わせる
    public async Task NumberAsync(IReadOnlyList<TitleEditRow> selected)
    {
        var stem = NumberStem.TrimEnd();
        if (stem.Length == 0)
        {
            await _dialogs.ShowErrorAsync("番号の前に付ける名前を入れてください(例:とんこつらーめん)。", "番号を振る");
            return;
        }
        if (!int.TryParse(NameNormalizer.Normalize(StartNumber), out var start) || start < 0)
        {
            await _dialogs.ShowErrorAsync("始めの番号は 0 以上の数で入れてください。", "番号を振る");
            return;
        }
        var targets = (selected.Count > 1 ? selected : Rows).OrderBy(Rows.IndexOf).ToList();
        var (_, style) = TitleNumbers.MaxNumber(TitleNumbers.SeriesKey(stem), Rows.Select(r => r.CurrentTitle));
        _suspendCheck = true;
        for (var i = 0; i < targets.Count; i++)
        {
            targets[i].NewTitle = stem + TitleNumbers.Format(start + i, style, stem);
        }
        _suspendCheck = false;
        Recheck();
    }

    // 決定ログ DI-5: 下書きの中と、ライブラリのほかの PDF のタイトル・ファイル名を合わせて比べる
    private void Recheck()
    {
        if (_suspendCheck)
        {
            return;
        }
        var changes = Rows.Where(r => r.IsChanged).ToDictionary(r => r.Id, r => r.EffectiveTitle);
        HashSet<string> conflicts;
        using (var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath))
        {
            conflicts = TitleBatch.FindConflicts(connection, changes);
        }
        foreach (var row in Rows)
        {
            row.IsConflict = conflicts.Contains(row.Id);
        }
        ChangeCount = changes.Count;
        ConflictCount = conflicts.Count;
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(HasChanges));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private bool CanSave() => ChangeCount > 0 && ConflictCount == 0;

    // 決定ログ DI-7: 「変更前 → 変更後」を確かめてから、まとめて保存する
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var changed = Rows.Where(r => r.IsChanged).ToList();
        var lines = changed.Take(MaxListedChanges).Select(r => $"・{r.CurrentTitle} → {r.EffectiveTitle}");
        var more = changed.Count > MaxListedChanges ? $"\nほか {changed.Count - MaxListedChanges} 件" : "";
        if (!await _dialogs.ConfirmAsync("タイトルの付け直し",
                $"{changed.Count} 件のタイトルを変えます。ファイル名を「タイトルと同じ」にしている PDF は、ファイル名も変わります。\n\n{string.Join("\n", lines)}{more}", "保存する"))
        {
            return;
        }
        var ids = changed.Select(r => r.Id).ToList();
        // 決定ログ S-12: つながっていないサブの文書は編集できない
        if (changed.FirstOrDefault(r => !_storage.Storage.IsAvailable(r.StorageId)) is { } unavailable)
        {
            await _dialogs.ShowErrorAsync(_storage.Storage.UnavailableMessage(unavailable.StorageId), "保存できません");
            return;
        }
        var token = await _storage.RunAsync(s => s.BeginChange(ids));
        using (var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath))
        {
            TitleBatch.Apply(connection, changed.Select(r => new TitleChange(r.Id, r.EffectiveTitle)).ToList(), DateTimeOffset.UtcNow);
        }
        var problems = await _storage.RunAsync(s => s.CompleteChange(token));
        if (problems.Count > 0)
        {
            await _dialogs.ShowErrorAsync(string.Join("\n\n", problems.Take(5)), "サブライブラリ");
        }
        Saved = true;
        SaveCompleted?.Invoke();
    }

    public event Action? SaveCompleted;
}
