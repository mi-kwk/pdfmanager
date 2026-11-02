using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfManager.App.Services;
using PdfManager.Core.Catalog;
using PdfManager.Core.Database;
using PdfManager.Core.Documents;
using PdfManager.Core.Pdf;
using PdfManager.Core.Storage;

namespace PdfManager.App.ViewModels;

// SC-21 タグの一括付与(決定ログ DM-1〜DM-5)。選んだ PDF にまとめてタグを付ける・外す。
// タグの欄は取り込み画面と同じ。チェック = 全件に付ける、外す = 全件から外す、一部(薄い表示)= そのまま
public sealed partial class TagBulkViewModel : ObservableObject
{
    private readonly DataFolder _dataFolder;
    private readonly StorageAccess _storage;
    private readonly IUserDialogs _dialogs;
    private IReadOnlyList<FieldDefinition> _fields = [];
    private List<DetailItem> _items = [];
    private Dictionary<DetailItem, HashSet<string>> _originals = [];
    // 付ける(true)・外す(false)と決めたタグ。選ぶ PDF を変えても引き継ぐ
    private Dictionary<string, bool> _intents = [];
    private bool _reloadQueued;

    public TagBulkViewModel(DataFolder dataFolder, StorageAccess storage, IUserDialogs dialogs, IReadOnlyCollection<string>? initialChecked, int tagCollapseRank)
    {
        _dataFolder = dataFolder;
        _storage = storage;
        _dialogs = dialogs;
        Editor = new DocumentEditor(dialogs, showRatingAndMemo: false) { CollapseUpToRank = tagCollapseRank };
        // 決定ログ DB-2: タグの欄から紐づけ・名前を直したら、すぐ保存して読み直す
        Editor.CatalogWriter = action =>
        {
            using (var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath))
            {
                action(new CatalogRepository(connection));
            }
            LoadCatalog();
            return true;
        };
        Editor.Edited += RecomputeIntents;
        LoadCatalog();
        Selector = new TitleBatchViewModel(dataFolder, storage, includeAll: true, initialChecked);
        Selector.CheckedChanged += QueueReload;
        Reload();
    }

    public TitleBatchViewModel Selector { get; }

    public DocumentEditor Editor { get; }

    [ObservableProperty]
    public partial string ChangesText { get; set; } = "";

    [ObservableProperty]
    public partial string? ResultText { get; set; }

    // 一度でも保存したか(メイン画面を読み直すため)
    public bool Saved { get; private set; }

    public bool HasIntents => _intents.Count > 0;

    // ==== 右の大きな表示 ====

    [ObservableProperty]
    public partial string PreviewTitle { get; set; } = "";

    [ObservableProperty]
    public partial ImageSource? Preview { get; set; }

    public async Task ShowPreviewAsync(TitleRow row)
    {
        PreviewTitle = row.Title;
        var pdf = _storage.Storage.GetDataFolder(row.StorageId)?.GetPdfPath(row.Id);
        try
        {
            if (pdf is null || !File.Exists(pdf))
            {
                throw new FileNotFoundException();
            }
            var (_, jpeg) = await PdfPageRenderer.RenderPageToJpegAsync(pdf, 0, 1000, 1000);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(jpeg);
            image.EndInit();
            image.Freeze();
            Preview = image;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Preview = row.Thumbnail;
        }
    }

    // ==== 対象の入れ替え ====

    // チェックは 1 件ずつ届くので、まとめて 1 回だけ読み直す(「すべて選ぶ」で何度も読み直さないように)
    private void QueueReload()
    {
        if (_reloadQueued)
        {
            return;
        }
        _reloadQueued = true;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _reloadQueued = false;
            Reload();
        });
    }

    // チェックしている PDF をタグの欄の対象にし、決めてある「付ける・外す」を当てはめる
    private void Reload()
    {
        var ids = Selector.CheckedRows.Select(r => r.Id).ToList();
        using (var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath))
        {
            _items = new DocumentRepository(connection).GetDetails(ids).Select(d => new DetailItem(d, _fields, Editor.TagKeyOf)).ToList();
        }
        _originals = _items.ToDictionary(i => i, i => i.TagKeys.ToHashSet());
        foreach (var item in _items)
        {
            foreach (var (key, add) in _intents)
            {
                if (add)
                {
                    item.TagKeys.Add(key);
                }
                else
                {
                    item.TagKeys.Remove(key);
                }
            }
        }
        Editor.SetTargets(_items);
        UpdateChangesText();
    }

    // タグの欄で押したら、「全件に付いている」「全件から外れている」タグを、付ける・外すとして覚える
    private void RecomputeIntents()
    {
        if (_items.Count == 0)
        {
            return;
        }
        var keys = _items.SelectMany(i => i.TagKeys).Concat(_originals.Values.SelectMany(k => k)).Concat(_intents.Keys).ToHashSet();
        var intents = new Dictionary<string, bool>();
        foreach (var key in keys)
        {
            var allNow = _items.All(i => i.TagKeys.Contains(key));
            var noneNow = _items.All(i => !i.TagKeys.Contains(key));
            var allBefore = _items.All(i => _originals[i].Contains(key));
            var noneBefore = _items.All(i => !_originals[i].Contains(key));
            if (allNow && !allBefore)
            {
                intents[key] = true;
            }
            else if (noneNow && !noneBefore)
            {
                intents[key] = false;
            }
            else if (_intents.TryGetValue(key, out var previous) && (previous ? allNow : noneNow))
            {
                intents[key] = previous;
            }
        }
        _intents = intents;
        UpdateChangesText();
    }

    private void UpdateChangesText()
    {
        var add = _intents.Where(i => i.Value).Select(i => $"「{Editor.TagDisplayName(i.Key)}」").ToList();
        var remove = _intents.Where(i => !i.Value).Select(i => $"「{Editor.TagDisplayName(i.Key)}」").ToList();
        var parts = new List<string>();
        if (add.Count > 0)
        {
            parts.Add($"付ける:{string.Join("、", add)}");
        }
        if (remove.Count > 0)
        {
            parts.Add($"外す:{string.Join("、", remove)}");
        }
        ChangesText = parts.Count == 0 ? "下のタグの欄で、付けるタグにチェック、外すタグはチェックを外してください。" : string.Join(" / ", parts);
        OnPropertyChanged(nameof(HasIntents));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private bool CanApply() => _items.Count > 0 && _intents.Count > 0;

    // 決定ログ DM-4: 「〇件に『△△』を付けます/『□□』を外します」と確かめてから、まとめて保存する
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        var changed = _items.Where(i => !i.TagKeys.SetEquals(_originals[i])).ToList();
        if (changed.Count == 0)
        {
            await _dialogs.ShowErrorAsync("選んでいる PDF には、もう同じようにタグが付いています(変わるものがありません)。", "タグの一括付与");
            return;
        }
        if (!await _dialogs.ConfirmAsync("タグの一括付与", $"{changed.Count} 件の PDF の、タグを変えます。\n\n{ChangesText}", "実行"))
        {
            return;
        }
        if (!await DetailSaver.SaveAsync(_dataFolder, _storage, _dialogs, Editor, changed))
        {
            return;
        }
        Saved = true;
        _intents = [];
        LoadCatalog();
        Reload();
        ResultText = $"{changed.Count} 件の PDF のタグを変えました。";
    }

    private void LoadCatalog()
    {
        using var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath);
        var catalog = new CatalogRepository(connection);
        _fields = catalog.GetFields().Where(f => f.Type != FieldType.Select).ToList();
        Editor.SetCatalog(_fields, catalog.GetGroups(), catalog.GetTags(), new DocumentRepository(connection).GetFileNames());
    }
}
