using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfManager.App.Services;
using PdfManager.Core.Catalog;
using PdfManager.Core.Documents;
using PdfManager.Core.Import;
using PdfManager.Core.Text;

namespace PdfManager.App.ViewModels;

public sealed partial class StarItem(int value, Action<int> onClick) : ObservableObject
{
    public int Value { get; } = value;

    [ObservableProperty]
    public partial bool IsFilled { get; set; }

    [RelayCommand]
    private void Click() => onClick(Value);
}

// 取り込み画面(SC-03)と詳細エリア(G-5、J-1〜J-7)で共通の入力欄。入力した値は対象の全件に入る(F-5〜F-7、J-2)
public partial class DocumentEditor : ObservableObject
{
    private readonly IUserDialogs _dialogs;
    // 保存・登録前の新しいタグ。キーは正規化した名前、値は表示名と付ける属性(決定ログ D-4)
    private readonly Dictionary<string, (string Name, IReadOnlyList<long> AttributeOptionIds)> _pendingTags = [];
    private IReadOnlyList<FieldDefinition> _fields = [];
    private Dictionary<string, TagSummary> _tags = [];
    private HashSet<string> _existingFileNames = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<EditableDocument> _targets = [];
    private bool _loading;

    public DocumentEditor(IUserDialogs dialogs, bool showRatingAndMemo)
    {
        _dialogs = dialogs;
        ShowRatingAndMemo = showRatingAndMemo;
        Stars = Enumerable.Range(1, 5).Select(i => new StarItem(i, OnStarClicked)).ToList();
        SelectedAttributeFilter = AttributeFilterOption.All;
    }

    // 利用者が入力欄を変えたとき(詳細エリアの「保存していない変更」の判定に使う)
    public event Action? Edited;

    // 同じファイル名の警告で、対象以外に比べる文書(取り込み画面では一覧の他の PDF)
    public Func<IEnumerable<EditableDocument>> OtherDocuments { get; set; } = () => [];

    public bool ShowRatingAndMemo { get; }

    public IReadOnlyList<EditableDocument> Targets => _targets;

    public bool HasTargets => _targets.Count > 0;

    public bool IsFileNamePartsEnabled => EditorUseTitle != true;

    public ObservableCollection<FieldEditor> FieldEditors { get; } = [];

    public IReadOnlyList<StarItem> Stars { get; }

    [ObservableProperty]
    public partial string EditorTitle { get; set; } = "";

    [ObservableProperty]
    public partial bool IsTitleMixed { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<string> TitleCandidates { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFileNamePartsEnabled))]
    public partial bool? EditorUseTitle { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<PrefixOption> PrefixOptions { get; set; } = [];

    [ObservableProperty]
    public partial PrefixOption? EditorPrefix { get; set; }

    [ObservableProperty]
    public partial string EditorSuffix { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SuffixPlaceholder))]
    public partial bool IsSuffixMixed { get; set; }

    public string SuffixPlaceholder => IsSuffixMixed ? "(複数の値)" : "自由入力(例:-MPC-WE1)";

    [ObservableProperty]
    public partial string FileNamePreview { get; set; } = "";

    [ObservableProperty]
    public partial string? FileNameWarning { get; set; }

    [ObservableProperty]
    public partial bool CanRebuildFileName { get; set; }

    [ObservableProperty]
    public partial string TagSearch { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<AttributeFilterOption> AttributeFilters { get; set; } = [];

    [ObservableProperty]
    public partial AttributeFilterOption SelectedAttributeFilter { get; set; }

    [ObservableProperty]
    public partial bool ShowAllTags { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<TagChoice> TagChoices { get; set; } = [];

    [ObservableProperty]
    public partial string TagFilterHint { get; set; } = "";

    [ObservableProperty]
    public partial bool IsRatingMixed { get; set; }

    [ObservableProperty]
    public partial string EditorMemo { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoPlaceholder))]
    public partial bool IsMemoMixed { get; set; }

    public string MemoPlaceholder => IsMemoMixed ? "(複数の値)入力すると、選択中の全件が同じメモになります" : "メモ(検索の対象になります)";

    // ==== 対象と項目・タグの定義 ====

    public void SetCatalog(IReadOnlyList<FieldDefinition> fields, IReadOnlyList<TagSummary> tags, HashSet<string> existingFileNames)
    {
        _tags = tags.ToDictionary(t => NameNormalizer.Normalize(t.Name));
        foreach (var key in _pendingTags.Keys.Where(_tags.ContainsKey).ToList())
        {
            _pendingTags.Remove(key);
        }
        _existingFileNames = existingFileNames;

        if (!fields.Select(f => (f.Id, f.Type)).SequenceEqual(_fields.Select(f => (f.Id, f.Type))))
        {
            FieldEditors.Clear();
            foreach (var field in fields)
            {
                FieldEditors.Add(new FieldEditor(field, OnFieldEdited));
            }
        }
        _fields = fields;
        AttributeFilters = [AttributeFilterOption.All, .. fields
            .Where(f => f.Type == FieldType.Select)
            .SelectMany(f => f.Options.Select(o => new AttributeFilterOption(o.Id, $"{f.Name}:{o.Value}")))];
        SelectedAttributeFilter = AttributeFilters.FirstOrDefault(a => a.OptionId == SelectedAttributeFilter?.OptionId) ?? AttributeFilterOption.All;
    }

    public void SetTargets(IReadOnlyList<EditableDocument> targets)
    {
        _targets = targets;
        Reload();
    }

    // 対象の文書の値を入力欄に読み込み直す
    public void Reload()
    {
        _loading = true;
        try
        {
            (EditorTitle, IsTitleMixed) = Common(i => i.Title);
            TitleCandidates = _targets.Count == 1 ? _targets[0].TitleCandidates : [];
            var useTitle = _targets.Select(i => i.UseTitleAsFileName).Distinct().ToList();
            EditorUseTitle = useTitle.Count switch { 0 => false, 1 => useTitle[0], _ => null };
            (EditorSuffix, IsSuffixMixed) = Common(i => i.Suffix);
            (EditorMemo, IsMemoMixed) = Common(i => i.Memo);
            var ratings = _targets.Select(i => i.Rating).Distinct().ToList();
            IsRatingMixed = ratings.Count > 1;
            ShowStars(ratings.Count == 1 ? ratings[0] : 0);
            RebuildPrefixOptions();
            foreach (var editor in FieldEditors)
            {
                var (text, mixed) = Common(i => i.FieldInputs.GetValueOrDefault(editor.Field.Id, ""));
                editor.Load(text, mixed);
            }
            RefreshTagChoices();
        }
        finally
        {
            _loading = false;
        }
        UpdateFileNamePreview();
        OnPropertyChanged(nameof(HasTargets));
    }

    private (string Value, bool IsMixed) Common(Func<EditableDocument, string> get)
    {
        var values = _targets.Select(get).Distinct().ToList();
        return values.Count == 1 ? (values[0], false) : ("", values.Count > 1);
    }

    private void ApplyEdit(Action<EditableDocument> apply)
    {
        foreach (var item in _targets)
        {
            apply(item);
        }
        UpdateFileNamePreview();
        Edited?.Invoke();
    }

    // ==== タイトル・ファイル名・評価・メモ ====

    partial void OnEditorTitleChanged(string value)
    {
        if (_loading)
        {
            return;
        }
        IsTitleMixed = false;
        ApplyEdit(i =>
        {
            i.Title = value;
            i.FileNameChanged |= i.UseTitleAsFileName;
        });
    }

    partial void OnEditorUseTitleChanged(bool? value)
    {
        if (!_loading && value is { } useTitle)
        {
            ApplyEdit(i =>
            {
                i.UseTitleAsFileName = useTitle;
                i.FileNameChanged = true;
            });
        }
    }

    partial void OnEditorPrefixChanged(PrefixOption? value)
    {
        if (!_loading && value is not null)
        {
            ApplyEdit(i =>
            {
                i.Prefix = value.Choice;
                i.FileNameChanged = true;
            });
        }
    }

    partial void OnEditorSuffixChanged(string value)
    {
        if (_loading)
        {
            return;
        }
        IsSuffixMixed = false;
        ApplyEdit(i =>
        {
            i.Suffix = value;
            i.FileNameChanged = true;
        });
    }

    partial void OnEditorMemoChanged(string value)
    {
        if (_loading)
        {
            return;
        }
        IsMemoMixed = false;
        ApplyEdit(i => i.Memo = value);
    }

    // 決定ログ J-5: 同じ星をもう一度押すと未評価に戻る
    private void OnStarClicked(int value)
    {
        var current = _targets.Select(i => i.Rating).Distinct().ToList();
        var rating = !IsRatingMixed && current is [var only] && only == value ? 0 : value;
        IsRatingMixed = false;
        ShowStars(rating);
        ApplyEdit(i => i.Rating = rating);
    }

    private void ShowStars(int rating)
    {
        foreach (var star in Stars)
        {
            star.IsFilled = star.Value <= rating;
        }
    }

    // 決定ログ C-8: タグ名の変更などで変わった先頭の部分を、ファイル名に反映する
    [RelayCommand]
    private void RebuildFileName() => ApplyEdit(i => i.FileNameChanged = true);

    private void OnFieldEdited(FieldEditor editor)
    {
        if (_loading)
        {
            return;
        }
        foreach (var item in _targets)
        {
            if (editor.Text.Trim().Length == 0)
            {
                item.FieldInputs.Remove(editor.Field.Id);
            }
            else
            {
                item.FieldInputs[editor.Field.Id] = editor.Text;
            }
        }
        if (editor.IsSelect)
        {
            RefreshTagChoices();
        }
        UpdateFileNamePreview();
        Edited?.Invoke();
    }

    // ==== タグ ====

    partial void OnTagSearchChanged(string value) => RefreshTagChoices();

    partial void OnSelectedAttributeFilterChanged(AttributeFilterOption value) => RefreshTagChoices();

    partial void OnShowAllTagsChanged(bool value) => RefreshTagChoices();

    [RelayCommand]
    private async Task AddTagFromSearchAsync()
    {
        var name = NameNormalizer.Clean(TagSearch);
        if (name.Length == 0 || _targets.Count == 0)
        {
            return;
        }
        if (name.Length > CatalogRepository.MaxNameLength)
        {
            await _dialogs.ShowErrorAsync($"タグ名は {CatalogRepository.MaxNameLength} 文字以内で入力してください。");
            return;
        }
        var key = EnsureTagKnown(name, SelectedAttributeOptionIds());
        ApplyEdit(i => i.TagKeys.Add(key));
        TagSearch = "";
        RebuildPrefixOptions();
        RefreshTagChoices();
    }

    public string EnsureTagKnown(string name, IReadOnlyList<long> attributeOptionIdsForNewTag)
    {
        var key = NameNormalizer.Normalize(name);
        if (!_tags.ContainsKey(key) && !_pendingTags.ContainsKey(key))
        {
            _pendingTags[key] = (NameNormalizer.Clean(name), attributeOptionIdsForNewTag);
        }
        return key;
    }

    private void OnTagToggled(TagChoice choice)
    {
        if (_loading)
        {
            return;
        }
        var add = choice.IsChecked != false;
        foreach (var item in _targets)
        {
            if (add)
            {
                item.TagKeys.Add(choice.Key);
            }
            else
            {
                item.TagKeys.Remove(choice.Key);
            }
        }
        RebuildPrefixOptions();
        UpdateFileNamePreview();
        Edited?.Invoke();
    }

    // 決定ログ D-1: 選択肢型の項目で選んでいる値(対象の全件で同じもの)
    private List<long> SelectedAttributeOptionIds() => FieldEditors
        .Where(e => e.IsSelect && !e.IsMixed && e.Text.Length > 0)
        .SelectMany(e => e.Field.Options.Where(o => o.Value == e.Text).Select(o => o.Id))
        .ToList();

    private void RefreshTagChoices()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            if (_targets.Count == 0)
            {
                TagChoices = [];
                TagFilterHint = "";
                return;
            }

            var attributeIds = SelectedAttributeOptionIds();
            var search = NameNormalizer.Normalize(TagSearch);
            // 検索欄や属性のプルダウンで探しているときは、項目の値による絞り込みとは別に探す(決定ログ G-4)
            var byItemValue = !ShowAllTags && attributeIds.Count > 0 && search.Length == 0 && SelectedAttributeFilter.OptionId is null;
            var candidates = _tags.Values
                .Select(t => (Key: NameNormalizer.Normalize(t.Name), t.Name, t.AttributeText, OptionIds: t.Attributes.Select(a => a.OptionId).ToList(), IsNew: false))
                .Concat(_pendingTags.Select(p => (p.Key, p.Value.Name, AttributeText: AttributeText(p.Value.AttributeOptionIds), OptionIds: p.Value.AttributeOptionIds.ToList(), IsNew: true)));

            TagChoices = candidates
                .Where(c =>
                    _targets.Any(i => i.TagKeys.Contains(c.Key))
                    || ((!byItemValue || c.OptionIds.Any(attributeIds.Contains))
                        && (SelectedAttributeFilter.OptionId is not { } filter || c.OptionIds.Contains(filter))
                        && (search.Length == 0 || c.Key.Contains(search, StringComparison.Ordinal)
                            || NameNormalizer.Normalize(c.AttributeText).Contains(search, StringComparison.Ordinal))))
                .Select(c =>
                {
                    var count = _targets.Count(i => i.TagKeys.Contains(c.Key));
                    bool? state = count == 0 ? false : count == _targets.Count ? true : null;
                    return new TagChoice(c.Key, c.Name, c.AttributeText, c.IsNew, state, OnTagToggled);
                })
                .OrderByDescending(c => c.IsChecked != false)
                .ThenBy(c => c.Key, StringComparer.Ordinal)
                .ToList();

            TagFilterHint = byItemValue
                ? $"項目で選んだ「{AttributeText(attributeIds)}」を属性に持つタグを表示しています。"
                : TagChoices.Count == 0 ? "タグがありません。上の欄に入力して Enter で追加できます。" : "";
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private string AttributeText(IEnumerable<long> optionIds) => string.Join("、", _fields
        .SelectMany(f => f.Options)
        .Where(o => optionIds.Contains(o.Id))
        .Select(o => o.Value));

    // ==== ファイル名 ====

    private void RebuildPrefixOptions()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            var options = new List<PrefixOption> { PrefixOption.None };
            options.AddRange(_targets.SelectMany(i => i.TagKeys).Distinct()
                .Select(key => new PrefixOption(new FileNamePrefixChoice(FileNamePrefixChoice.TagKind, key), $"タグ:{TagDisplayName(key)}")));
            options.AddRange(_fields.Select(f => new PrefixOption(
                new FileNamePrefixChoice(FileNamePrefixChoice.FieldKind, f.Id.ToString(CultureInfo.InvariantCulture)), $"項目:{f.Name}")));

            var current = _targets.Select(i => i.Prefix).Distinct().ToList();
            if (current is [{ } only] && options.All(o => o.Choice != only))
            {
                options.Add(new PrefixOption(only, $"{PrefixLabel(only)}(付いていません)"));
            }
            PrefixOptions = options;
            EditorPrefix = current.Count == 1 ? options.First(o => o.Choice == current[0]) : null;
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private string PrefixLabel(FileNamePrefixChoice choice) => choice.Kind == FileNamePrefixChoice.TagKind
        ? $"タグ:{TagDisplayName(choice.Key)}"
        : $"項目:{FieldByKey(choice.Key)?.Name ?? "(削除された項目)"}";

    private FieldDefinition? FieldByKey(string key) => _fields.FirstOrDefault(f => f.Id.ToString(CultureInfo.InvariantCulture) == key);

    public string TagDisplayName(string key) =>
        _tags.TryGetValue(key, out var tag) ? tag.Name : _pendingTags.TryGetValue(key, out var pending) ? pending.Name : key;

    public string? TagKeyOf(long tagId) => _tags.Values.FirstOrDefault(t => t.Id == tagId) is { } tag ? NameNormalizer.Normalize(tag.Name) : null;

    private string? PrefixValue(EditableDocument item)
    {
        if (item.Prefix is not { } prefix)
        {
            return null;
        }
        if (prefix.Kind == FileNamePrefixChoice.TagKind)
        {
            return item.TagKeys.Contains(prefix.Key) ? TagDisplayName(prefix.Key) : null;
        }
        if (FieldByKey(prefix.Key) is not { } field || !item.FieldInputs.TryGetValue(field.Id, out var raw))
        {
            return null;
        }
        try
        {
            return FieldValues.Parse(field.Type, raw) is { } value ? FieldValues.Format(field.Type, value) : null;
        }
        catch (CatalogException)
        {
            return raw;
        }
    }

    // 入力から作ったファイル名(登録済みの文書でも、今の入力で作った場合の名前)
    public BuiltFileName ComputeFileName(EditableDocument item) =>
        FileNameBuilder.Build(item.Title, item.UseTitleAsFileName, PrefixValue(item), item.Suffix, item.OriginalFileName);

    // 保存・登録するファイル名(決定ログ J-3、C-8)
    public string FileNameFor(EditableDocument item) =>
        item.StoredFileName is { } stored && !item.FileNameChanged ? stored : ComputeFileName(item).FileName;

    public void UpdateFileNamePreview()
    {
        if (_targets.Count == 0)
        {
            FileNamePreview = "";
            FileNameWarning = null;
            CanRebuildFileName = false;
            return;
        }
        var names = _targets.Select(FileNameFor).ToList();
        FileNamePreview = _targets.Count == 1
            ? $"→ {names[0]}"
            : $"→ {names[0]} など({_targets.Count} 件それぞれに作られます)";

        var warnings = new List<string>();
        if (_targets.Any(i => (i.StoredFileName is null || i.FileNameChanged) && ComputeFileName(i).ReplacedInvalidCharacters))
        {
            warnings.Add("ファイル名に使えない文字を「_」に置き換えました。");
        }
        if (CountSameFileNames(_targets) > 0)
        {
            warnings.Add("同じファイル名の PDF があります(保存・登録はできます)。");
        }
        CanRebuildFileName = _targets.Any(i => i.StoredFileName is not null && !i.FileNameChanged
            && !string.Equals(i.StoredFileName, ComputeFileName(i).FileName, StringComparison.Ordinal));
        if (CanRebuildFileName)
        {
            warnings.Add("タグ名などが変わっています。「タグから作り直す」でファイル名に反映できます。");
        }
        FileNameWarning = warnings.Count == 0 ? null : string.Join(" ", warnings);
    }

    // 決定ログ C-7: ライブラリの他の文書や、一緒に保存・登録するものと同じファイル名の件数
    public int CountSameFileNames(IEnumerable<EditableDocument> items)
    {
        var list = items.ToList();
        var own = list.Concat(OtherDocuments()).Distinct().ToList();
        var names = own.Select(FileNameFor).ToList();
        // 自分自身の今の名前は、ライブラリの「他の文書」から除く
        var others = new HashSet<string>(_existingFileNames, StringComparer.OrdinalIgnoreCase);
        foreach (var stored in own.Select(i => i.StoredFileName).OfType<string>())
        {
            others.Remove(stored);
        }
        return list.Select(FileNameFor).Count(n => others.Contains(n) || names.Count(o => string.Equals(o, n, StringComparison.OrdinalIgnoreCase)) > 1);
    }

    // ==== 保存・登録の準備 ====

    public static string TitleFor(EditableDocument item) =>
        NameNormalizer.Clean(item.Title).Length == 0 ? FileNameBuilder.UntitledTitle : NameNormalizer.Clean(item.Title);

    // 項目の値の形式チェック。問題があればメッセージの一覧を返す
    public List<string> ParseFieldValues(IEnumerable<EditableDocument> items, Dictionary<EditableDocument, Dictionary<long, FieldValue>> parsed)
    {
        var errors = new List<string>();
        foreach (var item in items)
        {
            var values = new Dictionary<long, FieldValue>();
            foreach (var (fieldId, raw) in item.FieldInputs)
            {
                if (_fields.FirstOrDefault(f => f.Id == fieldId) is not { } field)
                {
                    continue;
                }
                try
                {
                    if (FieldValues.Parse(field.Type, raw) is { } value)
                    {
                        values[fieldId] = value;
                    }
                }
                catch (CatalogException ex)
                {
                    errors.Add($"・{item.DisplayName}:{field.Name} {ex.Message}");
                }
            }
            parsed[item] = values;
        }
        return errors;
    }

    // 新しいタグはここで登録する(その時点で選んでいた属性付き、決定ログ D-4)
    public Dictionary<string, long> ResolveTagIds(CatalogRepository catalog, IEnumerable<string> keys) =>
        keys.Distinct().ToDictionary(key => key, key => _tags.TryGetValue(key, out var tag)
            ? tag.Id
            : catalog.GetOrCreateTag(_pendingTags[key].Name, _pendingTags[key].AttributeOptionIds));

    public (FileNameSource Source, long? SourceId) FileNameSourceFor(EditableDocument item, Dictionary<string, long> tagIds) => item.UseTitleAsFileName
        ? (FileNameSource.Title, null)
        : item.Prefix switch
        {
            { Kind: FileNamePrefixChoice.TagKind } p when item.TagKeys.Contains(p.Key) => (FileNameSource.Tag, tagIds[p.Key]),
            { Kind: FileNamePrefixChoice.FieldKind } p when PrefixValue(item) is not null => (FileNameSource.Field, long.Parse(p.Key, CultureInfo.InvariantCulture)),
            _ => (FileNameSource.None, null),
        };
}
