using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfManager.App.Services;
using PdfManager.Core.Database;
using PdfManager.Core.Documents;
using PdfManager.Core.Import;
using PdfManager.Core.Pdf;
using PdfManager.Core.Storage;
using Wpf.Ui.Controls;

namespace PdfManager.App.ViewModels;

// 削除予定の PDF 1 件(決定ログ DD-8)。初めはチェックが付いている
public sealed partial class DeleteReviewItem(SetAsideFile file) : ObservableObject
{
    public SetAsideFile File { get; } = file;

    public string FileName => File.FileName;

    public string OriginalText => File.OriginalPath is { } original ? $"元の場所:{Path.GetDirectoryName(original)}" : "元の場所:記録なし";

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = true;

    [ObservableProperty]
    public partial ImageSource? Thumbnail { get; set; }

    // 登録済みの PDF と同じ内容か(調べ終わるまでは空)
    [ObservableProperty]
    public partial string Mark { get; set; } = "";

    // 決定ログ DH-5: 1 ページ目が似ている登録済みの PDF(重複チェックをしたときだけ)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMatch))]
    public partial DuplicateMatch? Match { get; set; }

    [ObservableProperty]
    public partial string SimilarText { get; set; } = "";

    public bool HasMatch => Match is not null;
}

// SC-15 削除予定の確認(決定ログ DD-7・DD-8)。チェックしたものを PC のごみ箱へ送るか、保留へ戻す
public sealed partial class DeleteReviewViewModel : ObservableObject
{
    private const uint ThumbnailSize = 160;
    private const uint PreviewSize = 1200;

    private readonly DataFolder _dataFolder;
    private readonly SetAsideStore _store;
    private readonly IUserDialogs _dialogs;
    private readonly bool _duplicateLoose;
    private int _loadVersion;

    public DeleteReviewViewModel(DataFolder dataFolder, IUserDialogs dialogs, bool duplicateLoose = false)
    {
        _duplicateLoose = duplicateLoose;
        _dataFolder = dataFolder;
        _store = new SetAsideStore(dataFolder);
        _dialogs = dialogs;
    }

    public ObservableCollection<DeleteReviewItem> Items { get; } = [];

    public int CheckedCount => Items.Count(i => i.IsChecked);

    public string CountText => Items.Count == 0 ? "削除予定の PDF はありません" : $"{Items.Count} 件中 {CheckedCount} 件を選んでいます";

    // 何か消した・保留へ戻したか(メイン画面の件数を直すため)
    public bool Changed { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    public partial DeleteReviewItem? Selected { get; set; }

    [ObservableProperty]
    public partial ImageSource? Preview { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    public partial int PageIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    public partial int PageCount { get; set; }

    public string PageText => Selected is null || PageCount == 0 ? "" : $"{PageIndex + 1} / {PageCount} ページ";

    [ObservableProperty]
    public partial bool IsResultOpen { get; set; }

    [ObservableProperty]
    public partial string ResultMessage { get; set; } = "";

    [ObservableProperty]
    public partial InfoBarSeverity ResultSeverity { get; set; }

    public void Load()
    {
        var version = ++_loadVersion;
        foreach (var item in Items)
        {
            item.PropertyChanged -= OnItemChanged;
        }
        Items.Clear();
        foreach (var file in _store.List(SetAsideKind.Delete))
        {
            var item = new DeleteReviewItem(file);
            item.PropertyChanged += OnItemChanged;
            Items.Add(item);
        }
        Selected = Items.FirstOrDefault();
        NotifyCounts();
        _ = LoadThumbnailsAsync(version);
    }

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeleteReviewItem.IsChecked))
        {
            NotifyCounts();
        }
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(CountText));
        DeleteCommand.NotifyCanExecuteChanged();
        HoldCommand.NotifyCanExecuteChanged();
        CheckDuplicatesCommand.NotifyCanExecuteChanged();
    }

    // サムネイルと「登録済みと同じ」の印は、一覧を出してから 1 件ずつ用意する
    private async Task LoadThumbnailsAsync(int version)
    {
        HashSet<string> registered;
        using (var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath))
        {
            registered = new DocumentRepository(connection).GetFileHashes();
        }
        foreach (var item in Items.ToList())
        {
            if (version != _loadVersion)
            {
                return;
            }
            var path = item.File.Path;
            try
            {
                var (_, jpeg) = await PdfPageRenderer.RenderPageToJpegAsync(path, 0, ThumbnailSize, ThumbnailSize);
                item.Thumbnail = ToImage(jpeg);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 壊れた PDF などはサムネイルなしで出す
            }
            try
            {
                var hash = await Task.Run(() =>
                {
                    using var stream = System.IO.File.OpenRead(path);
                    return Convert.ToHexStringLower(SHA256.HashData(stream));
                });
                item.Mark = registered.Contains(hash) ? "登録済みと同じ内容" : "";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                item.Mark = "読み込めません";
            }
        }
    }

    partial void OnSelectedChanged(DeleteReviewItem? value)
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

    private async Task ShowPreviewAsync()
    {
        if (Selected is not { } item)
        {
            Preview = null;
            PageCount = 0;
            return;
        }
        try
        {
            var (count, jpeg) = await PdfPageRenderer.RenderPageToJpegAsync(item.File.Path, PageIndex, PreviewSize, PreviewSize);
            if (Selected == item)
            {
                PageCount = count;
                Preview = ToImage(jpeg);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Preview = null;
            PageCount = 0;
        }
    }

    [RelayCommand]
    private void CheckAll()
    {
        foreach (var item in Items)
        {
            item.IsChecked = true;
        }
    }

    [RelayCommand]
    private void UncheckAll()
    {
        foreach (var item in Items)
        {
            item.IsChecked = false;
        }
    }

    private bool HasChecked() => CheckedCount > 0;

    // 決定ログ DD-8: チェックしたものを PC のごみ箱へ送る(ごみ箱が使えない場所なら、完全に消えることも確かめる)
    [RelayCommand(CanExecute = nameof(HasChecked))]
    private async Task DeleteAsync()
    {
        var targets = Items.Where(i => i.IsChecked).ToList();
        var permanent = !_store.IsRecycleBinAvailable(SetAsideKind.Delete);
        var message = permanent
            ? $"チェックした {targets.Count} 件を削除しますか?\n\nライブラリが USB メモリなど、ごみ箱が使えない場所にあるため、完全に削除され、元に戻せません。"
            : $"チェックした {targets.Count} 件を削除しますか?\n\nPC のごみ箱へ移します(ごみ箱から元に戻せます)。";
        if (!await _dialogs.ConfirmAsync("削除", message, permanent ? "完全に削除する" : "ごみ箱へ移す"))
        {
            return;
        }
        ReleasePreview();
        var problems = await Task.Run(() => _store.Delete(targets.Select(t => t.File.Path)));
        Changed = true;
        Load();
        ShowResult(problems, $"{targets.Count - problems.Count} 件を{(permanent ? "削除" : "ごみ箱へ移")}しました。");
    }

    // 決定ログ DD-8: チェックしたものを保留フォルダへ移す(取り込み画面の「保留」から登録できる)
    [RelayCommand(CanExecute = nameof(HasChecked))]
    private async Task HoldAsync()
    {
        var targets = Items.Where(i => i.IsChecked).ToList();
        ReleasePreview();
        var problems = await Task.Run(() =>
        {
            var failed = new List<string>();
            foreach (var target in targets)
            {
                try
                {
                    _store.Move(target.File.Path, SetAsideKind.Hold);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed.Add($"{target.FileName}:{ex.Message}");
                }
            }
            return failed;
        });
        Changed = true;
        Load();
        ShowResult(problems, $"{targets.Count - problems.Count} 件を保留へ移しました。取り込み画面の「保留」から登録できます。");
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckDuplicatesCommand))]
    public partial bool IsCheckingDuplicates { get; set; }

    private bool CanCheckDuplicates() => !IsCheckingDuplicates && HasChecked();

    // 決定ログ DH-5: チェックした PDF を、登録済みのすべての PDF と 1 ページ目の見た目で比べる(タグの条件なし)
    [RelayCommand(CanExecute = nameof(CanCheckDuplicates))]
    private async Task CheckDuplicatesAsync()
    {
        var targets = Items.Where(i => i.IsChecked).ToList();
        IsCheckingDuplicates = true;
        try
        {
            ResultSeverity = InfoBarSeverity.Informational;
            ResultMessage = "登録済みの PDF と比べています…";
            IsResultOpen = true;
            await DuplicateLookup.PrepareAsync(_dataFolder, new Progress<(int Done, int Total)>(p =>
            {
                if (p.Total > 20)
                {
                    ResultMessage = $"はじめに、登録済みの PDF の 1 ページ目を調べています({p.Done:N0} / {p.Total:N0} 件)…";
                }
            }));
            var found = 0;
            for (var i = 0; i < targets.Count; i++)
            {
                var item = targets[i];
                ResultMessage = $"登録済みの PDF と比べています({i + 1} / {targets.Count} 件)…";
                IReadOnlyList<DuplicateMatch> matches;
                try
                {
                    matches = await DuplicateLookup.FindAsync(_dataFolder, item.File.Path, null, _duplicateLoose);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    item.Match = null;
                    item.SimilarText = "1 ページ目を読み込めません";
                    continue;
                }
                item.Match = matches.FirstOrDefault();
                item.SimilarText = matches.Count switch
                {
                    0 => "1 ページ目が同じ登録済みの PDF はありません",
                    1 => $"1 ページ目が「{matches[0].Title}」と同じ({matches[0].SimilarityText})",
                    _ => $"1 ページ目が「{matches[0].Title}」ほか {matches.Count - 1} 件と同じ",
                };
                found += matches.Count > 0 ? 1 : 0;
            }
            ShowResult([], $"チェックした {targets.Count} 件のうち、{found} 件は 1 ページ目が同じ登録済みの PDF があります。カードを選ぶと、右に並べて表示します。");
        }
        finally
        {
            IsCheckingDuplicates = false;
        }
    }

    // 表示中のプレビューはファイルを開いていないが、念のため選択を外してから動かす
    private void ReleasePreview()
    {
        Selected = null;
        Preview = null;
    }

    private void ShowResult(IReadOnlyList<string> problems, string message)
    {
        ResultSeverity = problems.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        ResultMessage = problems.Count > 0 ? message + "\nできなかったもの:\n" + string.Join("\n", problems.Take(5)) : message;
        IsResultOpen = true;
    }

    private static BitmapImage ToImage(byte[] jpeg)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(jpeg);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
