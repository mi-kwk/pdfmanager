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
using PdfManager.Core.Images;
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
    private readonly SetAsideStore _setAside;
    private readonly bool _duplicateLoose;
    private readonly IUserDialogs _dialogs;
    private readonly string _previewFolder = Path.Combine(Path.GetTempPath(), "PdfManager", "import-" + Guid.NewGuid().ToString("N"));
    private LastImportInput? _lastInput;
    private IReadOnlyList<FieldDefinition> _fields = [];
    private List<ImportItem> _selection = [];
    private bool _preparing;

    private readonly DocumentDetail? _newVersionOf;

    // newVersionOf を渡すと、その文書の新しい版として 1 件だけ登録する(決定ログ N-1①、N-2)。
    // showRegisterAll は「すべて登録」ボタンを表示するか(設定画面で切り替える、決定ログ R-5)。tagCollapseRank はタグを選んだら畳む欄の順位(決定ログ AA-1)
    // useTextTitleCandidates はタイトルの候補に PDF の文字から作ったものも出すか(決定ログ DF-3)
    public ImportViewModel(DataFolder dataFolder, IUserDialogs dialogs, IEnumerable<string> paths, DocumentDetail? newVersionOf = null, bool showRegisterAll = false,
        int tagCollapseRank = PdfManager.Core.Settings.AppSettings.DefaultTagCollapseRank, bool useTextTitleCandidates = true, bool duplicateLoose = false)
    {
        _duplicateLoose = duplicateLoose;
        _newVersionOf = newVersionOf;
        _dataFolder = dataFolder;
        _service = new ImportService(dataFolder);
        _setAside = new SetAsideStore(dataFolder);
        _dialogs = dialogs;
        ShowRegisterAll = showRegisterAll;
        // 決定ログ R-10・T-6: 新しい版は前の版と同じ名前で続けるため、番号を付けない
        Editor = new DocumentEditor(dialogs, showRatingAndMemo: false)
        {
            NumberDuplicateNames = newVersionOf is null,
            CollapseUpToRank = tagCollapseRank,
            UseTitleSuggestions = true,
            UseTextTitleCandidates = useTextTitleCandidates,
        };
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
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DocumentEditor.HasFirstRankTag))
            {
                CheckDuplicateCommand.NotifyCanExecuteChanged();
            }
        };
        // 名前を変えたタグは、選んでいない PDF の入力でも付け替える
        Editor.TagKeyChanged += (oldKey, newKey) =>
        {
            foreach (var item in Items.Where(i => i.TagKeys.Remove(oldKey)))
            {
                item.TagKeys.Add(newKey);
            }
        };
        Directory.CreateDirectory(_previewFolder);
        _lastInput = LastImportInput.Load(dataFolder);
        Items.CollectionChanged += (_, e) =>
        {
            foreach (var item in e.NewItems?.OfType<ImportItem>() ?? [])
            {
                item.PropertyChanged += OnItemChanged;
            }
            NotifyCounts();
        };
        LoadCatalog();
        _initialPaths = paths.ToList();
    }

    private readonly List<string> _initialPaths;

    // 画面が表示されてから、渡されたファイルを一覧に入れる(画像・ZIP があれば「画像から PDF 作成」の画面を開くため)。
    // 画像から PDF を作る画面をキャンセルして何も入らなかったときは false
    public bool AddInitialFiles()
    {
        LoadHeld();
        // 決定ログ DD-6: ファイルを選ばずに開いたとき(メイン画面の「保留」)は、保留を開いた状態で始める
        if (_initialPaths.Count == 0)
        {
            IsHeldExpanded = true;
            PrimarySelection ??= Items.FirstOrDefault();
            _ = PrepareQueuedAsync();
            return true;
        }
        return AddFiles(_initialPaths);
    }

    // 画像・ZIP から PDF を作る画面を開く(画像, ZIP, 作業フォルダ)。キャンセルしたときは空を返す(F-26、決定ログ Y-1)
    public Func<IReadOnlyList<string>, IReadOnlyList<string>, string, IReadOnlyList<CreatedPdf>>? CreatePdfsFromImages { get; set; }

    // 決定ログ ED-3: PDF を登録・保留・削除予定へ移す前に、ビューアのタブを閉じてもらう(元の場所のパスを渡す)
    public Action<IReadOnlyCollection<string>>? ReleaseFiles { get; set; }

    // 「取り込まない」を切り替えたとき(決定ログ R-4)
    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImportItem.IsExcluded))
        {
            NotifyCounts();
            if (sender is ImportItem item && _selection.Contains(item))
            {
                LoadSelection();
            }
        }
    }

    public DocumentEditor Editor { get; }

    public ObservableCollection<ImportItem> Items { get; } = [];

    public int RegisteredCount { get; private set; }

    public string? LastRegisteredId { get; private set; }

    public bool IsNewVersionMode => _newVersionOf is not null;

    public string? NewVersionText => _newVersionOf is null ? null : $"「{_newVersionOf.Title}」の新しい版として登録します。前の版のタイトル・ファイル名の作り方・項目・タグ・メモを引き継いでいます(変更できます)。";

    public bool ShowRegisterAll { get; }

    // 登録の対象になる件数(「取り込まない」の印が付いたものと、保留のものは除く)
    public int ReadyCount => Items.Count(i => i.IsRegistrable && !i.IsHeld);

    public int SelectedReadyCount => _selection.Count(i => i.IsRegistrable);

    public string RegisterAllText => $"すべて登録({ReadyCount} 件)";

    public string RegisterSelectedText => $"選択中を登録({SelectedReadyCount} 件)";

    public string ListHeader => $"取り込み待ち({Items.Count(i => !i.IsHeld)} 件)";

    // 決定ログ DD-5: 一覧の上の「保留」(初めは閉じている)
    public int HeldCount => Items.Count(i => i.IsHeld);

    public string HeldHeader => $"保留({HeldCount} 件)";

    [ObservableProperty]
    public partial bool IsHeldExpanded { get; set; }

    // 決定ログ DD-4: 登録済み、または一覧のほかの PDF と同じ内容のもの
    public int DuplicateCount => Items.Count(i => i.Status == ImportItemStatus.Duplicate);

    public string DuplicateText => $"同じ内容のもの({DuplicateCount} 件)を削除予定へ";

    public bool HasSelection => _selection.Count > 0;

    // 登録や保留・削除予定へ送って次の PDF に移ったとき、右の入力欄を一番上へ戻す(決定ログ DC-1)
    public event Action? EditorScrollResetRequested;

    private List<ImportItem> Editable => _selection.Where(i => i.IsRegistrable).ToList();

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
    // 決定ログ Z-2: 初めからオン(G-1 の「毎回『残す』に戻す」を改めた)
    public partial bool DeleteOriginals { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterSelectedCommand), nameof(RegisterAllCommand))]
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
        var dialog = new OpenFileDialog { Title = "取り込むファイルを選択", Filter = FileDialogFilter, Multiselect = true };
        if (dialog.ShowDialog() == true)
        {
            AddFiles(dialog.FileNames);
        }
    }

    // 決定ログ Y-1: 画像・ZIP も選べる
    public static string FileDialogFilter =>
        "PDF・画像・ZIP|*.pdf;*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff;*.zip|PDF ファイル (*.pdf)|*.pdf|" + ImageSources.DialogFilter;

    // 画像から PDF を作る画面をキャンセルして何も入らなかったときは false
    public bool AddFiles(IEnumerable<string> paths)
    {
        var collected = ImportService.CollectFiles(paths);
        var files = collected.PdfFiles.Select(f => new CreatedPdf(f, [])).ToList();
        if (collected.HasImageSources && CreatePdfsFromImages is { } create)
        {
            files.AddRange(create(collected.ImageFiles, collected.ZipFiles, _previewFolder));
            if (files.Count == 0)
            {
                return false;
            }
        }
        if (files.Count == 0)
        {
            ShowResult(InfoBarSeverity.Warning, "追加できるファイルがありません", "PDF・画像・ZIP ファイルが見つかりませんでした。");
            return true;
        }
        if (IsNewVersionMode && (Items.Count > 0 || files.Count > 1))
        {
            ShowResult(InfoBarSeverity.Informational, "新しい版は 1 件ずつ登録します", "最初の 1 件だけを使います。");
            files = Items.Count > 0 ? [] : files.Take(1).ToList();
        }
        foreach (var file in files.Where(f => Items.All(i => !string.Equals(i.SourcePath, f.Path, StringComparison.OrdinalIgnoreCase))))
        {
            // 決定ログ Z-1: ファイル名は初めから「タイトルと同じにする」
            Items.Add(new ImportItem(file.Path) { UseTitleAsFileName = true, OriginalSources = file.OriginalSources });
        }
        // 保留は初めは閉じておくので、取り込み待ちの先頭を選ぶ
        PrimarySelection ??= Items.FirstOrDefault(i => !i.IsHeld) ?? Items.FirstOrDefault();
        _ = PrepareQueuedAsync();
        return true;
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
            // 保留の PDF は後回し(初めは閉じているため)
            while (Items.Where(i => i.Status == ImportItemStatus.Preparing).OrderBy(i => i.IsHeld).FirstOrDefault() is { } item)
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
                    if (_newVersionOf is not null)
                    {
                        CopyFromPreviousVersion(item, new DetailItem(_newVersionOf, _fields, Editor.TagKeyOf));
                    }
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
        ReleaseFiles?.Invoke(_selection.Select(i => i.SourcePath).ToList());
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
        NotifyCounts();
    }

    private static void CopyFromPreviousVersion(ImportItem item, DetailItem previous)
    {
        item.Title = previous.Title;
        item.UseTitleAsFileName = previous.UseTitleAsFileName;
        item.Prefix = previous.Prefix;
        item.Suffix = previous.Suffix;
        item.Memo = previous.Memo;
        foreach (var (fieldId, text) in previous.FieldInputs)
        {
            item.FieldInputs[fieldId] = text;
        }
        foreach (var key in previous.TagKeys)
        {
            item.TagKeys.Add(key);
        }
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
                item.TagKeys.Add(Editor.EnsureTagKnown(name));
            }
            item.Prefix = _lastInput.Prefix;
        }
        Editor.Reload();
    }

    // ==== 登録 ====

    private bool CanRegisterSelected() => !IsRegistering && SelectedReadyCount > 0;

    private bool CanRegisterAll() => !IsRegistering && ReadyCount > 0;

    // 決定ログ R-5: 選んでいる PDF だけを登録し、次の PDF を選ぶ(1 件ずつ入力して登録できるように)
    [RelayCommand(CanExecute = nameof(CanRegisterSelected))]
    private Task RegisterSelectedAsync() => RegisterAsync(Items.Where(i => _selection.Contains(i) && i.IsRegistrable).ToList());

    [RelayCommand(CanExecute = nameof(CanRegisterAll))]
    private Task RegisterAllAsync() => RegisterAsync(Items.Where(i => i.IsRegistrable && !i.IsHeld).ToList());

    private async Task RegisterAsync(List<ImportItem> targets)
    {
        if (targets.Count == 0)
        {
            ShowResult(InfoBarSeverity.Warning, "登録できる PDF がありません", "準備完了の PDF がありません。");
            return;
        }
        // 登録したものは一覧から消えるので、その位置にある次の PDF を選ぶ
        var nextIndex = Items.IndexOf(targets[0]);
        var wasHeld = targets[0].IsHeld;

        var parsedValues = new Dictionary<EditableDocument, Dictionary<long, FieldValue>>();
        var fieldErrors = Editor.ParseFieldValues(targets, parsedValues);
        if (fieldErrors.Count > 0)
        {
            await _dialogs.ShowErrorAsync("次の項目の入力を直してから登録してください。\n\n" + string.Join("\n", fieldErrors.Take(MaxReportedProblems)));
            return;
        }

        // 決定ログ R-10・T-4・T-7: 同じファイル名があれば確認せずに「_2」などを付ける(一緒に登録するものどうしも)
        var names = Editor.AssignNames(targets.Cast<EditableDocument>().ToList());
        if (DeleteOriginals && targets.Where(t => !t.IsHeld).SelectMany(t => t.Originals).Any(p => !RecycleBin.IsAvailableFor(p))
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
                    var input = new ImportInput(names[item].Title, names[item].FileName, source, sourceId,
                        item.UseTitleAsFileName ? "" : item.Suffix, parsedValues[item], item.TagKeys.Select(k => tagIds[k]).ToList())
                    {
                        Memo = item.Memo,
                        NewVersionOf = _newVersionOf?.Id,
                    };
                    var result = await Task.Run(() => _service.RegisterAsync(item.Prepared!, input));
                    if (result.WasDuplicate)
                    {
                        item.Message = result.Warning;
                        item.Status = ImportItemStatus.Duplicate;
                        continue;
                    }
                    registered++;
                    LastRegisteredId = result.DocumentId;
                    // 何も入力していない PDF で「前回」を上書きしないよう、入力があったものだけを覚える
                    if (item.FieldInputs.Count > 0 || item.TagKeys.Count > 0 || item.Prefix is not null)
                    {
                        lastWithInput = item;
                    }
                    ReleaseFiles?.Invoke([item.SourcePath]);
                    Items.Remove(item);
                    if (result.Warning is not null)
                    {
                        problems.Add($"・{item.DisplayName}:{result.Warning}");
                    }
                    if (item.IsHeld)
                    {
                        // 決定ログ DD-5: 保留の PDF は、登録したら保留フォルダから消す
                        DeleteOriginal(item.SourcePath, problems);
                        _setAside.Forget(item.SourcePath);
                    }
                    else if (DeleteOriginals)
                    {
                        // 画像・ZIP から作った PDF は、元の画像・ZIP をごみ箱へ(決定ログ Y-5)。
                        // 同じ ZIP から作ってまだ登録していない PDF が残っているときは、全部登録するまで残す
                        foreach (var original in item.Originals.Where(o => !Items.Any(i => i.Originals.Contains(o, StringComparer.OrdinalIgnoreCase))))
                        {
                            DeleteOriginal(original, problems);
                        }
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
            SelectNext(nextIndex, wasHeld);
        }

        var message = $"{registered} 件を登録しました。続けて PDF を追加できます。";
        if (problems.Count > 0)
        {
            message += "\n" + string.Join("\n", problems.Take(MaxReportedProblems)) + (problems.Count > MaxReportedProblems ? $"\nほか {problems.Count - MaxReportedProblems} 件" : "");
        }
        ShowResult(problems.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success, "登録しました", message);
    }

    // 登録・移動したあとに次の PDF を選ぶ。同じまとまり(保留 / 取り込み待ち)の、その位置にある次のものを先に
    private void SelectNext(int nextIndex, bool wasHeld)
    {
        PrimarySelection = Items.Skip(nextIndex).FirstOrDefault(i => i.IsRegistrable && i.IsHeld == wasHeld)
            ?? Items.FirstOrDefault(i => i.IsRegistrable && i.IsHeld == wasHeld)
            ?? Items.FirstOrDefault(i => i.IsRegistrable && !i.IsHeld)
            ?? Items.FirstOrDefault(i => !i.IsHeld)
            ?? Items.FirstOrDefault();
        if (PrimarySelection is null)
        {
            SetSelection([]);
        }
        EditorScrollResetRequested?.Invoke();
    }

    partial void OnPrimarySelectionChanged(ImportItem? value)
    {
        // 閉じている保留の PDF を選んだときは開く(選んだものが見えるように)
        if (value?.IsHeld == true)
        {
            IsHeldExpanded = true;
        }
    }

    // ==== 1 ページ目で重複チェック(決定ログ DH-4) ====

    // 似ている登録済みの PDF を並べて見せ、どうするかを選んでもらう画面を開く
    public Func<ImportItem, IReadOnlyList<Services.DuplicateMatch>, DuplicateChoice>? ShowDuplicates { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckDuplicateCommand))]
    public partial bool IsCheckingDuplicate { get; set; }

    // 1 件だけ選んでいて、第 1 グループのタグを付けているとき
    private bool CanCheckDuplicate() => !IsCheckingDuplicate && !IsRegistering && _selection.Count == 1
        && _selection[0].Status is ImportItemStatus.Ready or ImportItemStatus.Duplicate && Editor.HasFirstRankTag;

    [RelayCommand(CanExecute = nameof(CanCheckDuplicate))]
    private async Task CheckDuplicateAsync()
    {
        var item = _selection[0];
        var tagIds = Editor.FirstRankTagIds();
        if (tagIds.Count == 0)
        {
            ShowResult(InfoBarSeverity.Informational, "1 ページ目で重複チェック", "付けている第 1 グループのタグは新しく作ったもので、まだ登録済みの PDF がありません。");
            return;
        }
        IsCheckingDuplicate = true;
        try
        {
            ShowResult(InfoBarSeverity.Informational, "1 ページ目で重複チェック", "登録済みの PDF と比べています…");
            await Services.DuplicateLookup.PrepareAsync(_dataFolder, new Progress<(int Done, int Total)>(p =>
                ResultMessage = p.Total > 20 ? $"はじめに、登録済みの PDF の 1 ページ目を調べています({p.Done:N0} / {p.Total:N0} 件)…" : ResultMessage));
            var matches = await Services.DuplicateLookup.FindAsync(_dataFolder, item.SourcePath, tagIds, _duplicateLoose);
            if (matches.Count == 0)
            {
                ShowResult(InfoBarSeverity.Success, "1 ページ目で重複チェック", "同じ第 1 グループのタグの登録済み PDF に、1 ページ目が同じものはありませんでした。");
                return;
            }
            IsResultOpen = false;
            switch (ShowDuplicates?.Invoke(item, matches) ?? DuplicateChoice.Continue)
            {
                case DuplicateChoice.Delete:
                    await SetAsideAsync([item], SetAsideKind.Delete);
                    break;
                case DuplicateChoice.Hold when !item.IsHeld:
                    await SetAsideAsync([item], SetAsideKind.Hold);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowResult(InfoBarSeverity.Warning, "1 ページ目で重複チェック", $"調べられませんでした: {ex.Message}");
        }
        finally
        {
            IsCheckingDuplicate = false;
        }
    }

    // ==== 保留・削除予定(決定ログ DD-1〜DD-6) ====

    // 保留フォルダの PDF を一覧の上に入れる(新しい版の登録のときは出さない)
    private void LoadHeld()
    {
        if (IsNewVersionMode)
        {
            return;
        }
        var index = 0;
        foreach (var file in _setAside.List(SetAsideKind.Hold))
        {
            if (Items.All(i => !string.Equals(i.SourcePath, file.Path, StringComparison.OrdinalIgnoreCase)))
            {
                Items.Insert(index++, new ImportItem(file.Path) { IsHeld = true, HeldOriginalPath = file.OriginalPath, UseTitleAsFileName = true });
            }
        }
    }

    private bool CanSetAside() => !IsRegistering && !IsNewVersionMode && _selection.Count > 0;

    [RelayCommand(CanExecute = nameof(CanSetAside))]
    private Task SendToHoldAsync() => SetAsideAsync(_selection.Where(i => !i.IsHeld).ToList(), SetAsideKind.Hold);

    [RelayCommand(CanExecute = nameof(CanSetAside))]
    private Task SendToDeleteAsync() => SetAsideAsync(_selection.ToList(), SetAsideKind.Delete);

    // 決定ログ DD-4: 同じ内容のもの(登録済み、または一覧のほかの PDF と同じ)をまとめて削除予定へ
    [RelayCommand]
    private async Task SendDuplicatesToDeleteAsync()
    {
        var duplicates = Items.Where(i => i.Status == ImportItemStatus.Duplicate).ToList();
        if (duplicates.Count > 0 && await _dialogs.ConfirmAsync("削除予定へ移す",
                $"登録済みの PDF、または一覧のほかの PDF と同じ内容のもの {duplicates.Count} 件を、削除予定へ移しますか?\n\n元のフォルダからは移動します。メイン画面の「削除予定」で確認して消すまでは、ライブラリの「{SetAsideStore.DeleteFolderName}」フォルダに残ります。",
                "削除予定へ移す"))
        {
            await SetAsideAsync(duplicates, SetAsideKind.Delete);
        }
    }

    // 選んだ PDF を保留・削除予定のフォルダへ移す(元のフォルダからは消える、決定ログ DD-2)
    private async Task SetAsideAsync(List<ImportItem> targets, SetAsideKind kind)
    {
        if (targets.Count == 0)
        {
            return;
        }
        var nextIndex = Items.IndexOf(targets[0]);
        var wasHeld = targets[0].IsHeld;
        var moved = 0;
        var problems = new List<string>();
        var insertAt = Items.Count(i => i.IsHeld);
        // 決定ログ ED-3: 移す前に、ビューアで開いていれば閉じる(開いたままだと移せないことがある)
        ReleaseFiles?.Invoke(targets.Select(t => t.SourcePath).ToList());
        foreach (var item in targets)
        {
            // 画像・ZIP から作った PDF は作業用のファイルなので、元の画像・ZIP は動かせない
            if (item.OriginalSources.Count > 0)
            {
                problems.Add($"・{item.DisplayName}:画像・ZIP から作った PDF は移せません(「一覧から外す」を使ってください)");
                continue;
            }
            if (item.Status == ImportItemStatus.Preparing)
            {
                problems.Add($"・{item.DisplayName}:準備中のため移せません。準備が終わってからもう一度押してください");
                continue;
            }
            string path;
            try
            {
                path = await Task.Run(() => _setAside.Move(item.SourcePath, kind));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"・{item.DisplayName}:移せませんでした({ex.Message})");
                continue;
            }
            moved++;
            var index = Items.IndexOf(item);
            Items.Remove(item);
            if (index < insertAt)
            {
                insertAt--;
            }
            if (kind == SetAsideKind.Hold)
            {
                // 準備した結果は場所を変えてそのまま使う(入力はやり直し)
                var held = new ImportItem(path) { IsHeld = true, HeldOriginalPath = item.SourcePath, UseTitleAsFileName = true };
                if (item.Prepared is { } prepared)
                {
                    held.ApplyPrepared(prepared with { SourcePath = path });
                    if (item.Status == ImportItemStatus.Duplicate)
                    {
                        held.Message = item.Message;
                        held.Status = ImportItemStatus.Duplicate;
                    }
                }
                Items.Insert(insertAt++, held);
            }
        }
        SelectNext(Math.Min(nextIndex, Items.Count), wasHeld);
        _ = PrepareQueuedAsync();
        var place =kind == SetAsideKind.Hold ? "保留" : "削除予定";
        var message = kind == SetAsideKind.Hold
            ? $"{moved} 件を保留へ移しました。一覧の上の「保留」から、あとで登録・削除予定へ移せます。"
            : $"{moved} 件を削除予定へ移しました。メイン画面の「削除予定」で確認してから消せます。";
        if (problems.Count > 0)
        {
            message += "\n" + string.Join("\n", problems.Take(MaxReportedProblems)) + (problems.Count > MaxReportedProblems ? $"\nほか {problems.Count - MaxReportedProblems} 件" : "");
        }
        ShowResult(problems.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success, $"{place}へ移しました", message);
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
        _fields = catalog.GetFields().Where(f => f.Type != FieldType.Select).ToList();
        var documents = new DocumentRepository(connection);
        Editor.SetCatalog(_fields, catalog.GetGroups(), catalog.GetTags(), documents.GetFileNames());
        // 決定ログ DF-1: タイトルの候補に使う登録済みのタイトル(登録するたびに読み直す)
        Editor.SetTitleEntries(documents.GetTitleEntries());
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(HeldCount));
        OnPropertyChanged(nameof(HeldHeader));
        OnPropertyChanged(nameof(DuplicateCount));
        OnPropertyChanged(nameof(DuplicateText));
        OnPropertyChanged(nameof(HasSelection));
        SendToHoldCommand.NotifyCanExecuteChanged();
        SendToDeleteCommand.NotifyCanExecuteChanged();
        CheckDuplicateCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ReadyCount));
        OnPropertyChanged(nameof(SelectedReadyCount));
        OnPropertyChanged(nameof(RegisterAllText));
        OnPropertyChanged(nameof(RegisterSelectedText));
        OnPropertyChanged(nameof(ListHeader));
        RegisterSelectedCommand.NotifyCanExecuteChanged();
        RegisterAllCommand.NotifyCanExecuteChanged();
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
