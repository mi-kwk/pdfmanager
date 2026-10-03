using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PdfManager.App.Services;
using PdfManager.Core.Catalog;
using PdfManager.Core.Database;
using PdfManager.Core.Documents;
using PdfManager.Core.Import;
using PdfManager.Core.Storage;
using Wpf.Ui.Controls;

namespace PdfManager.App.ViewModels;

// SC-03 取り込み(docs/04「取り込み画面の流れ」)。入力欄は DocumentEditor(詳細エリアと共通)
public partial class ImportViewModel : ObservableObject, IDisposable
{
    private const int MaxReportedProblems = 5;

    private readonly DataFolder _dataFolder;
    private readonly ImportService _service;
    private readonly IUserDialogs _dialogs;
    private readonly string _previewFolder = Path.Combine(Path.GetTempPath(), "PdfManager", "import-" + Guid.NewGuid().ToString("N"));
    private LastImportInput? _lastInput;
    private IReadOnlyList<FieldDefinition> _fields = [];
    private List<ImportItem> _selection = [];
    private bool _preparing;

    public ImportViewModel(DataFolder dataFolder, IUserDialogs dialogs, IEnumerable<string> paths)
    {
        _dataFolder = dataFolder;
        _service = new ImportService(dataFolder);
        _dialogs = dialogs;
        Editor = new DocumentEditor(dialogs, showRatingAndMemo: false) { OtherDocuments = () => Items.Where(i => i.IsReady) };
        Directory.CreateDirectory(_previewFolder);
        _lastInput = LastImportInput.Load(dataFolder);
        Items.CollectionChanged += (_, _) => NotifyCounts();
        LoadCatalog();
        AddFiles(paths);
    }

    public DocumentEditor Editor { get; }

    public ObservableCollection<ImportItem> Items { get; } = [];

    public int RegisteredCount { get; private set; }

    public int ReadyCount => Items.Count(i => i.IsReady);

    public string RegisterText => $"登録({ReadyCount} 件)";

    public string ListHeader => $"取り込み待ち({Items.Count} 件)";

    private List<ImportItem> Editable => _selection.Where(i => i.IsReady).ToList();

    public bool HasEditableSelection => Editable.Count > 0;

    public string SelectionText => _selection.Count switch
    {
        0 => "左の一覧から PDF を選んでください(Ctrl・Shift で複数選択)",
        1 => _selection[0].DisplayName,
        _ => $"{_selection.Count} 件を選択中(入力した値は選択中の全件に入ります)",
    };

    [ObservableProperty]
    public partial ImportItem? PrimarySelection { get; set; }

    [ObservableProperty]
    public partial bool DeleteOriginals { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand))]
    public partial bool IsRegistering { get; set; }

    [ObservableProperty]
    public partial bool IsResultOpen { get; set; }

    [ObservableProperty]
    public partial string ResultTitle { get; set; } = "";

    [ObservableProperty]
    public partial string ResultMessage { get; set; } = "";

    [ObservableProperty]
    public partial InfoBarSeverity ResultSeverity { get; set; }

    [ObservableProperty]
    public partial ImageSource? PreviewImage { get; set; }

    [ObservableProperty]
    public partial string PreviewMessage { get; set; } = "";

    // ==== ファイルの追加と準備 ====

    [RelayCommand]
    private void SelectFilesToAdd()
    {
        var dialog = new OpenFileDialog { Title = "取り込む PDF を選択", Filter = "PDF ファイル (*.pdf)|*.pdf", Multiselect = true };
        if (dialog.ShowDialog() == true)
        {
            AddFiles(dialog.FileNames);
        }
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        var files = ImportService.CollectPdfFiles(paths);
        if (files.Count == 0)
        {
            ShowResult(InfoBarSeverity.Warning, "追加できるファイルがありません", "PDF ファイルが見つかりませんでした。");
            return;
        }
        foreach (var path in files.Where(f => Items.All(i => !string.Equals(i.SourcePath, f, StringComparison.OrdinalIgnoreCase))))
        {
            Items.Add(new ImportItem(path));
        }
        PrimarySelection ??= Items.FirstOrDefault();
        _ = PrepareQueuedAsync();
    }

    private async Task PrepareQueuedAsync()
    {
        if (_preparing)
        {
            return;
        }
        _preparing = true;
        try
        {
            while (Items.FirstOrDefault(i => i.Status == ImportItemStatus.Preparing) is { } item)
            {
                PreparedImport prepared;
                try
                {
                    prepared = await Task.Run(() => _service.PrepareAsync(item.SourcePath, _previewFolder));
                }
                catch (Exception ex)
                {
                    prepared = new PreparedImport(item.SourcePath, PreparationStatus.Failed, ex.Message, "", 0, 0, [], [], null);
                }
                if (!Items.Contains(item))
                {
                    continue;
                }

                item.ApplyPrepared(prepared);
                if (item.IsReady && Items.Any(o => o != item && o.Status != ImportItemStatus.Failed && o.Prepared?.FileHash == prepared.FileHash))
                {
                    item.MarkDuplicateInList();
                }
                if (item.IsReady)
                {
                    item.Prefix = _lastInput?.Prefix;
                }
                NotifyCounts();
                if (_selection.Contains(item))
                {
                    LoadSelection();
                }
            }
        }
        finally
        {
            _preparing = false;
        }
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        foreach (var item in _selection.ToList())
        {
            Items.Remove(item);
        }
        PrimarySelection = Items.FirstOrDefault();
    }

    // ==== 選択 ====

    public void SetSelection(IEnumerable<ImportItem> selected)
    {
        _selection = selected.ToList();
        LoadSelection();
    }

    private void LoadSelection()
    {
        var first = _selection.FirstOrDefault();
        PreviewImage = first?.Preview;
        PreviewMessage = first is null ? "" : first.Status switch
        {
            ImportItemStatus.Preparing => "準備中…",
            _ when first.Preview is null => first.Message ?? "プレビューはありません",
            _ => _selection.Count > 1 ? $"{first.DisplayName}(ほか {_selection.Count - 1} 件)" : "",
        };
        Editor.SetTargets(Editable);
        OnPropertyChanged(nameof(HasEditableSelection));
        OnPropertyChanged(nameof(SelectionText));
    }

    [RelayCommand]
    private void ApplyLastInput()
    {
        if (_lastInput is null)
        {
            ShowResult(InfoBarSeverity.Informational, "前回の入力がありません", "このライブラリで一度登録すると使えるようになります。");
            return;
        }
        foreach (var item in Editable)
        {
            item.FieldInputs.Clear();
            foreach (var (fieldId, text) in _lastInput.FieldInputs.Where(f => _fields.Any(d => d.Id == f.Key)))
            {
                item.FieldInputs[fieldId] = text;
            }
            item.TagKeys.Clear();
            foreach (var name in _lastInput.TagNames)
            {
                item.TagKeys.Add(Editor.EnsureTagKnown(name, []));
            }
            item.Prefix = _lastInput.Prefix;
        }
        Editor.Reload();
    }

    // ==== 登録 ====

    private bool CanRegister() => !IsRegistering;

    [RelayCommand(CanExecute = nameof(CanRegister))]
    private async Task RegisterAsync()
    {
        var targets = Items.Where(i => i.IsReady).ToList();
        if (targets.Count == 0)
        {
            ShowResult(InfoBarSeverity.Warning, "登録できる PDF がありません", "準備完了の PDF がありません。");
            return;
        }

        var parsedValues = new Dictionary<EditableDocument, Dictionary<long, FieldValue>>();
        var fieldErrors = Editor.ParseFieldValues(targets, parsedValues);
        if (fieldErrors.Count > 0)
        {
            await _dialogs.ShowErrorAsync("次の項目の入力を直してから登録してください。\n\n" + string.Join("\n", fieldErrors.Take(MaxReportedProblems)));
            return;
        }

        var sameNames = Editor.CountSameFileNames(targets);
        if (sameNames > 0 && !await _dialogs.ConfirmAsync("同じファイル名があります",
                $"同じファイル名になる PDF が {sameNames} 件あります。このまま登録しますか?\n(USB へ出力するときは「(2)」などを付けて区別します)", "登録する"))
        {
            return;
        }
        if (DeleteOriginals && targets.Any(t => !RecycleBin.IsAvailableFor(t.SourcePath))
            && !await _dialogs.ConfirmAsync("元のファイルが完全に削除されます",
                "USB メモリなど、ごみ箱が使えない場所にある元のファイルがあります。登録後、それらは完全に削除され、元に戻せません。続けますか?", "登録して削除する"))
        {
            return;
        }

        IsRegistering = true;
        var registered = 0;
        var problems = new List<string>();
        ImportItem? lastWithInput = null;
        try
        {
            Dictionary<string, long> tagIds;
            using (var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath))
            {
                tagIds = Editor.ResolveTagIds(new CatalogRepository(connection), targets.SelectMany(t => t.TagKeys));
            }
            foreach (var item in targets)
            {
                try
                {
                    var (source, sourceId) = Editor.FileNameSourceFor(item, tagIds);
                    var input = new ImportInput(DocumentEditor.TitleFor(item), Editor.FileNameFor(item), source, sourceId,
                        item.UseTitleAsFileName ? "" : item.Suffix, parsedValues[item], item.TagKeys.Select(k => tagIds[k]).ToList());
                    var result = await Task.Run(() => _service.RegisterAsync(item.Prepared!, input));
                    if (result.WasDuplicate)
                    {
                        item.Message = result.Warning;
                        item.Status = ImportItemStatus.Duplicate;
                        continue;
                    }
                    registered++;
                    // 何も入力していない PDF で「前回」を上書きしないよう、入力があったものだけを覚える
                    if (item.FieldInputs.Count > 0 || item.TagKeys.Count > 0 || item.Prefix is not null)
                    {
                        lastWithInput = item;
                    }
                    Items.Remove(item);
                    if (result.Warning is not null)
                    {
                        problems.Add($"・{item.DisplayName}:{result.Warning}");
                    }
                    if (DeleteOriginals)
                    {
                        DeleteOriginal(item.SourcePath, problems);
                    }
                }
                catch (Exception ex)
                {
                    item.Message = $"登録できませんでした: {ex.Message}";
                    problems.Add($"・{item.DisplayName}:{ex.Message}");
                }
            }
        }
        finally
        {
            IsRegistering = false;
            RegisteredCount += registered;
            if (lastWithInput is not null)
            {
                SaveLastInput(lastWithInput);
            }
            LoadCatalog();
            PrimarySelection = Items.FirstOrDefault();
            if (PrimarySelection is null)
            {
                SetSelection([]);
            }
        }

        var message = $"{registered} 件を登録しました。続けて PDF を追加できます。";
        if (problems.Count > 0)
        {
            message += "\n" + string.Join("\n", problems.Take(MaxReportedProblems)) + (problems.Count > MaxReportedProblems ? $"\nほか {problems.Count - MaxReportedProblems} 件" : "");
        }
        ShowResult(problems.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success, "登録しました", message);
    }

    private static void DeleteOriginal(string path, List<string> problems)
    {
        try
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
        catch (Exception ex)
        {
            problems.Add($"・{Path.GetFileName(path)}:元のファイルを削除できませんでした({ex.Message})");
        }
    }

    private void SaveLastInput(ImportItem item)
    {
        _lastInput = new LastImportInput(
            new Dictionary<long, string>(item.FieldInputs),
            item.TagKeys.Select(Editor.TagDisplayName).ToList(),
            item.Prefix);
        try
        {
            _lastInput.Save(_dataFolder);
        }
        catch (IOException)
        {
            // 保存できなくても登録自体には影響しない。次回「前回と同じ」が古い内容になるだけ
        }
    }

    // ==== 共通 ====

    private void LoadCatalog()
    {
        using var connection = PdfManagerDatabase.Open(_dataFolder.DatabasePath);
        var catalog = new CatalogRepository(connection);
        _fields = catalog.GetFields();
        Editor.SetCatalog(_fields, catalog.GetTags(), new DocumentRepository(connection).GetFileNames());
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(ReadyCount));
        OnPropertyChanged(nameof(RegisterText));
        OnPropertyChanged(nameof(ListHeader));
    }

    private void ShowResult(InfoBarSeverity severity, string title, string message)
    {
        ResultSeverity = severity;
        ResultTitle = title;
        ResultMessage = message;
        IsResultOpen = true;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_previewFolder, recursive: true);
        }
        catch (IOException)
        {
            // 一時フォルダなので、消せなくても Windows の一時ファイル整理で消える
        }
        GC.SuppressFinalize(this);
    }
}
