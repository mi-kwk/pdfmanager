using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfManager.App.Services;
using PdfManager.Core.Catalog;
using PdfManager.Core.Documents;
using PdfManager.Core.Import;
using PdfManager.Core.Search;
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
    // 保存・登録前の新しいタグ。キーは正規化した名前(決定ログ Q-12)
    private readonly Dictionary<string, PendingTag> _pendingTags = [];
    private IReadOnlyList<FieldDefinition> _fields = [];
    private Dictionary<long, TagGroup> _groups = [];
    private Dictionary<string, TagSummary> _tags = [];
    private Dictionary<long, string> _tagKeysById = [];
    private HashSet<string> _existingFileNames = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<EditableDocument> _targets = [];
    private bool _loading;

    // showTagSections が false なら、付いているタグだけを表示し、「タグ設定」で別画面を開く(詳細エリア、決定ログ R-11 ④)
    public DocumentEditor(IUserDialogs dialogs, bool showRatingAndMemo, bool showTagSections = true)
    {
        _dialogs = dialogs;
        ShowRatingAndMemo = showRatingAndMemo;
        ShowTagSections = showTagSections;
        Stars = Enumerable.Range(1, 5).Select(i => new StarItem(i, OnStarClicked)).ToList();
    }

    private sealed record PendingTag(string Name, long? GroupId, IReadOnlyList<string> ParentKeys);

    // タグ 1 つ分の情報(登録済みと、まだ登録していない新しいタグをまとめて扱う)。Id・UsageCount・Aliases は登録済みのタグだけ
    private sealed record TagInfo(string Key, string Name, long? GroupId, IReadOnlyList<string> ParentKeys, bool IsNew,
        long? Id = null, long UsageCount = 0, IReadOnlyList<string>? Aliases = null)
    {
        public IReadOnlyList<string> AliasNames => Aliases ?? [];

        // 検索欄で比べる形(ひらがな・カタカナの違いも吸収する、決定ログ DE-3)
        public string Folded { get; } = DocumentSearchIndex.Normalize(Name);

        public IReadOnlyList<string> FoldedAliases { get; } = (Aliases ?? []).Select(DocumentSearchIndex.Normalize).ToList();

        public bool HasAliasKey(string key) => AliasNames.Any(a => NameNormalizer.Normalize(a) == key);
    }

    // 決定ログ DB-1・DB-2: タグの紐づけ・名前をこの画面から直す(すぐライブラリに保存する)。
    // 画面ごとに、データベースへの書き込みと、そのあとの読み込み直し(SetCatalog)を行う処理を渡す。null なら直せない
    public Func<Action<CatalogRepository>, bool>? CatalogWriter { get; set; }

    // タグの名前を変えてキーが変わったとき(古いキー, 新しいキー)。対象以外の PDF の入力も直すために画面へ知らせる
    public event Action<string, string>? TagKeyChanged;

    // 利用者が入力欄を変えたとき(詳細エリアの「保存していない変更」の判定に使う)
    public event Action? Edited;

    // 詳細エリアの「タグ設定」が押されたとき
    public event Action? TagSettingRequested;

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

    public bool ShowTagSections { get; }

    // 決定ログ R-11 ②: タググループごとの欄(順位 1・独立 → 順位 2〜5 → 自由タグ)
    public ObservableCollection<TagSection> TagSections { get; } = [];

    // 決定ログ AA-1: タグを選ぶと畳む欄の順位の上限(0 は畳まない。設定画面で PC ごとに決める)
    public int CollapseUpToRank
    {
        get;
        set
        {
            field = value;
            UpdateCollapsible();
            ResetCollapsed();
        }
    } = Core.Settings.AppSettings.DefaultTagCollapseRank;

    // 決定ログ R-11 ④: 付いているタグ(詳細エリアの表示用)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAssignedTags))]
    public partial IReadOnlyList<AssignedTagLine> AssignedTags { get; set; } = [];

    public bool HasAssignedTags => AssignedTags.Count > 0;

    [ObservableProperty]
    public partial bool IsRatingMixed { get; set; }

    [ObservableProperty]
    public partial string EditorMemo { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoPlaceholder))]
    public partial bool IsMemoMixed { get; set; }

    public string MemoPlaceholder => IsMemoMixed ? "(複数の値)入力すると、選択中の全件が同じメモになります" : "メモ(検索の対象になります)";

    // ==== 対象と項目・タグの定義 ====

    // fields は補助項目(年月・日付・数値・文字、決定ログ Q-6)
    public void SetCatalog(IReadOnlyList<FieldDefinition> fields, IReadOnlyList<TagGroup> groups, IReadOnlyList<TagSummary> tags, HashSet<string> existingFileNames)
    {
        _tags = tags.ToDictionary(t => NameNormalizer.Normalize(t.Name));
        _tagKeysById = tags.ToDictionary(t => t.Id, t => NameNormalizer.Normalize(t.Name));
        foreach (var key in _pendingTags.Keys.Where(_tags.ContainsKey).ToList())
        {
            _pendingTags.Remove(key);
        }
        _existingFileNames = existingFileNames;

        fields = fields.Where(f => f.Type != FieldType.Select).ToList();
        if (!fields.Select(f => (f.Id, f.Type)).SequenceEqual(_fields.Select(f => (f.Id, f.Type))))
        {
            FieldEditors.Clear();
            foreach (var field in fields)
            {
                FieldEditors.Add(new FieldEditor(field, OnFieldEdited));
            }
        }
        _fields = fields;

        // 順位 1 と独立を先に、順位 2〜5 をその後に(同じ順位の中は管理画面での並び順)、最後に自由タグ
        var ordered = groups.OrderBy(g => g.DisplayRank).ToList();
        if (!ordered.Select(g => (g.Id, g.Name, g.Rank)).SequenceEqual(TagSections.Where(s => s.Group is not null).Select(s => (s.Group!.Id, s.Group.Name, s.Group.Rank))))
        {
            TagSections.Clear();
            foreach (var group in ordered)
            {
                TagSections.Add(new TagSection(group, () => RefreshTagChoices(), OnSectionEnterAsync));
            }
            TagSections.Add(new TagSection(null, () => RefreshTagChoices(), OnSectionEnterAsync));
        }
        _groups = groups.ToDictionary(g => g.Id);
        UpdateCollapsible();
        RefreshTagChoices();
    }

    // 設定の順位以下で、下に次の順位の欄がある欄だけを畳めるようにする(最後の順位・独立・自由タグは畳まない、AA-1)
    private void UpdateCollapsible()
    {
        foreach (var section in TagSections)
        {
            section.CanCollapse = section.Group is { Rank: >= 1 } group && group.Rank <= CollapseUpToRank
                && TagSections.Any(s => s.Group?.Rank == group.Rank + 1);
            if (!section.CanCollapse)
            {
                section.IsCollapsed = false;
            }
        }
    }

    // 決定ログ AA-5: タグが付いている欄は畳んだ状態で始める
    private void ResetCollapsed()
    {
        foreach (var section in TagSections)
        {
            section.IsCollapsed = section.CanCollapse && section.Summary.Length > 0;
        }
    }

    // 決定ログ AA-1・AA-3・AA-4: タグを付けたら、その欄を畳んで次の順位の欄の検索欄へ
    private void CollapseAfterSelect(TagSection section)
    {
        if (!section.CanCollapse || section.Group is not { } group)
        {
            return;
        }
        section.IsCollapsed = true;
        if (TagSections.FirstOrDefault(s => s.Group?.Rank == group.Rank + 1) is { } next)
        {
            // 次の欄が畳まれていれば開く(すぐ選べるように)
            next.IsCollapsed = false;
            next.RequestFocus();
        }
    }

    public void SetTargets(IReadOnlyList<EditableDocument> targets)
    {
        // 対象が変わったら、タグの欄の検索の文字を消す(前の PDF の検索で候補が絞られたままにならないように)
        if (!targets.SequenceEqual(_targets))
        {
            foreach (var section in TagSections)
            {
                section.ResetSearch();
            }
        }
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
            ResetCollapsed();
        }
        finally
        {
            _loading = false;
        }
        UpdateFileNamePreview();
        OnPropertyChanged(nameof(HasTargets));
        UpdateTitleDuplicate();
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
        // 決定ログ DF-1: 入力するたびに、入力に合う登録済みのタイトルを候補に出す
        if (UseTitleSuggestions && !_adoptingTitle)
        {
            ShowTitleSuggestions(all: false);
        }
        UpdateTitleDuplicate();
    }

    // 決定ログ DH-4: 付けている第 1 グループ(順位 1)のタグのうち、登録済みのもの(新しく作ったタグにはまだ PDF がない)
    public IReadOnlyList<long> FirstRankTagIds() => CheckedKeysOfRank(1)
        .Select(k => _tags.TryGetValue(k, out var tag) ? tag.Id : (long?)null)
        .OfType<long>()
        .ToList();

    public bool HasFirstRankTag => _targets.Count > 0 && CheckedKeysOfRank(1).Count > 0;

    // ==== 重複タイトル(決定ログ DG-1〜DG-4、取り込み画面だけ) ====

    // 「『〇〇』は登録済みです。次の番号は『〇〇』です」。重なっていなければ null
    [ObservableProperty]
    public partial string? TitleDuplicateText { get; set; }

    private string? _nextTitle;

    private void UpdateTitleDuplicate()
    {
        _nextTitle = null;
        TitleDuplicateText = null;
        if (!UseTitleSuggestions || _targets.Count != 1 || NameNormalizer.Clean(EditorTitle) is not { Length: > 0 } title)
        {
            return;
        }
        // 決定ログ DG-2: ひらがな・カタカナ、全角・半角の違いは同じとみなす
        var key = NameNormalizer.Fold(title);
        if (_titleEntries.FirstOrDefault(e => NameNormalizer.Fold(e.Title) == key) is not { } existing)
        {
            return;
        }
        _nextTitle = TitleNumbers.Next(title, _titleEntries.Select(e => e.Title));
        TitleDuplicateText = $"「{existing.Title}」は登録済みです。次の番号は「{_nextTitle}」です。";
    }

    // 決定ログ DG-1: [この番号にする]
    [RelayCommand]
    private void UseNextTitle()
    {
        if (_nextTitle is not { } next)
        {
            return;
        }
        _adoptingTitle = true;
        try
        {
            EditorTitle = next;
        }
        finally
        {
            _adoptingTitle = false;
        }
        IsTitleSuggestionsOpen = false;
    }

    // ==== タイトルの候補(決定ログ DF-1〜DF-3、取り込み画面だけ) ====

    private IReadOnlyList<TitleEntry> _titleEntries = [];
    private bool _adoptingTitle;
    private const int MaxRegisteredTitleSuggestions = 10;

    // 取り込み画面だけ、登録済みのタイトルを候補に出す
    public bool UseTitleSuggestions { get; init; }

    // 決定ログ DF-3: PDF の文字から作った候補も出すか(設定画面で PC ごとに決める)
    public bool UseTextTitleCandidates { get; set; } = true;

    [ObservableProperty]
    public partial IReadOnlyList<TitleSuggestion> TitleSuggestions { get; set; } = [];

    [ObservableProperty]
    public partial bool IsTitleSuggestionsOpen { get; set; }

    public void SetTitleEntries(IReadOnlyList<TitleEntry> entries)
    {
        _titleEntries = entries;
        UpdateTitleDuplicate();
    }

    // all が true なら入力に関係なくすべて(▼ボタン)、false なら今の入力に合うもの
    public void ShowTitleSuggestions(bool all)
    {
        TitleSuggestions = _targets.Count == 0 ? [] : BuildTitleSuggestions(all ? "" : EditorTitle);
        IsTitleSuggestionsOpen = TitleSuggestions.Count > 0;
    }

    // 決定ログ DF-2: 登録済みのタイトルを選んだら、末尾の番号を「一番大きい番号 + 1」にする(新しい版のときは番号を変えない)
    public void AdoptTitleSuggestion(TitleSuggestion suggestion)
    {
        _adoptingTitle = true;
        try
        {
            EditorTitle = suggestion.IsRegistered && NumberDuplicateNames
                ? TitleNumbers.NextNumbered(suggestion.Text, _titleEntries.Select(e => e.Title))
                : suggestion.Text;
        }
        finally
        {
            _adoptingTitle = false;
        }
        IsTitleSuggestionsOpen = false;
    }

    private List<TitleSuggestion> BuildTitleSuggestions(string input)
    {
        var search = DocumentSearchIndex.Normalize(input.Trim());
        var firstRank = CheckedKeysOfRank(1);
        string FirstRankNames(TitleEntry entry) => string.Join("、", entry.TagIds
            .Select(id => _tagKeysById.GetValueOrDefault(id))
            .OfType<string>()
            .Where(k => _tags.TryGetValue(k, out var tag) && RankOf(tag.GroupId) == 1)
            .Select(TagDisplayName));

        // 第 1 グループのタグが同じものを先に、その中は新しい順。入力がないときは同じタグのものだけ
        var registered = _titleEntries
            .Select((entry, index) => (Entry: entry, Index: index,
                SameTag: entry.TagIds.Any(id => _tagKeysById.TryGetValue(id, out var key) && firstRank.Contains(key))))
            .Where(x => search.Length == 0 ? x.SameTag : DocumentSearchIndex.Normalize(x.Entry.Title).Contains(search, StringComparison.Ordinal))
            .OrderByDescending(x => x.SameTag)
            .ThenBy(x => x.Index)
            .DistinctBy(x => NameNormalizer.Normalize(x.Entry.Title))
            .Take(MaxRegisteredTitleSuggestions)
            .Select(x => new TitleSuggestion(x.Entry.Title, FirstRankNames(x.Entry) is { Length: > 0 } names ? $"登録済み ・ {names}" : "登録済み", true))
            .ToList();

        // この PDF から作った候補(ファイル名、PDF の文字)
        var local = new List<TitleSuggestion>();
        if (_targets.Count == 1)
        {
            var fileName = Path.GetFileNameWithoutExtension(_targets[0].OriginalFileName);
            local.Add(new TitleSuggestion(fileName, "ファイル名", false));
            if (UseTextTitleCandidates)
            {
                local.AddRange(_targets[0].TitleCandidates.Where(c => c != fileName).Select(c => new TitleSuggestion(c, "PDF の文字", false)));
            }
        }
        var taken = registered.Select(r => NameNormalizer.Normalize(r.Text)).ToHashSet();
        return
        [
            .. registered,
            .. local.Where(l => (search.Length == 0 || DocumentSearchIndex.Normalize(l.Text).Contains(search, StringComparison.Ordinal))
                                && l.Text.Length > 0 && taken.Add(NameNormalizer.Normalize(l.Text))),
        ];
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
        // 決定ログ DL-1・DL-2: 評価だけをすぐ保存する画面では、「保存していない変更」にしない
        if (RatingSaver is { } save)
        {
            foreach (var item in _targets)
            {
                item.Rating = rating;
            }
            _ = save(rating);
            return;
        }
        ApplyEdit(i => i.Rating = rating);
    }

    // 決定ログ DL-1〜DL-3: 星を押したら評価だけをすぐ保存する(詳細エリア)。null なら「保存」で保存する
    public Func<int, Task>? RatingSaver { get; set; }

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
        UpdateFileNamePreview();
        Edited?.Invoke();
    }

    // ==== タグ(決定ログ R-11 ②、Q-3・Q-8・Q-9・Q-12) ====

    [RelayCommand]
    private void OpenTagSetting() => TagSettingRequested?.Invoke();

    // 「前回と同じ」などで名前から付けるタグ。知らない名前なら自由タグとして新しく作る
    public string EnsureTagKnown(string name)
    {
        var key = NameNormalizer.Normalize(name);
        if (!_tags.ContainsKey(key) && !_pendingTags.ContainsKey(key))
        {
            _pendingTags[key] = new PendingTag(NameNormalizer.Clean(name), null, []);
        }
        return key;
    }

    private IEnumerable<TagInfo> AllTags() => _tags.Values
        .Select(t => new TagInfo(NameNormalizer.Normalize(t.Name), t.Name, t.GroupId,
            t.ParentIds.Select(id => _tagKeysById.GetValueOrDefault(id)).OfType<string>().ToList(), false, t.Id, t.UsageCount, t.AliasNames))
        .Concat(_pendingTags.Select(p => new TagInfo(p.Key, p.Value.Name, p.Value.GroupId, p.Value.ParentKeys, true)));

    private int? RankOf(long? groupId) => groupId is { } id && _groups.TryGetValue(id, out var group) ? group.Rank : null;

    // 対象のどれかに付いている、指定した順位のタグ(紐づくタグを表示するための親)
    private HashSet<string> CheckedKeysOfRank(int rank) => AllTags()
        .Where(t => RankOf(t.GroupId) == rank && _targets.Any(i => i.TagKeys.Contains(t.Key)))
        .Select(t => t.Key)
        .ToHashSet();

    private async Task OnSectionEnterAsync(TagSection section)
    {
        var name = NameNormalizer.Clean(section.Search);
        if (name.Length == 0 || _targets.Count == 0)
        {
            return;
        }
        if (name.Length > CatalogRepository.MaxNameLength)
        {
            await _dialogs.ShowErrorAsync($"タグ名は {CatalogRepository.MaxNameLength} 文字以内で入力してください。");
            return;
        }
        var key = NameNormalizer.Normalize(name);
        var groupId = section.Group?.Id;
        var all = AllTags().ToList();
        // 決定ログ DE-2: 別名を入れたときは、その別名を持つ正式なタグを付ける
        var existing = all.FirstOrDefault(t => t.Key == key) ?? all.FirstOrDefault(t => t.HasAliasKey(key));
        if (existing is null)
        {
            // 決定ログ DE-3: 同じ欄に、ひらがな・カタカナだけが違うタグ(別名を含む)があれば、そちらを付けるか確かめる
            var folded = DocumentSearchIndex.Normalize(name);
            if (all.FirstOrDefault(t => t.GroupId == groupId && (t.Folded == folded || t.FoldedAliases.Contains(folded))) is { } similar)
            {
                var choice = await _dialogs.AskChoiceAsync("似た名前のタグがあります",
                    $"「{similar.Name}」があります(ひらがな・カタカナの違いだけです)。「{similar.Name}」を付けますか?\n\n別のタグとして「{name}」を新しく作ることもできます。",
                    $"「{similar.Name}」を付ける", "新しく作る", "キャンセル");
                if (choice == 0)
                {
                    return;
                }
                existing = choice == 1 ? similar : null;
            }
        }
        if (existing is not null)
        {
            // 決定ログ Q-9: グループが違っても同じ名前のタグは作れない
            if (existing.GroupId != groupId)
            {
                var what = existing.Key == key ? "" : $"(「{name}」は「{existing.Name}」の別名です)";
                await _dialogs.ShowErrorAsync($"「{existing.Name}」は「{GroupLabel(existing.GroupId)}」にあるタグです{what}。同じ名前のタグは 1 つしか作れません。");
                return;
            }
            key = existing.Key;
            // 決定ログ DB-1: 上で選んでいるタグに紐づいていなければ、紐づけを直すか確かめる
            await ConfirmLinkAsync(existing);
        }
        else
        {
            // 順位 2 以降なら、上の順位で選んでいるタグに紐づけて作る
            var parents = section.Group is { Rank: >= 2 } group ? CheckedKeysOfRank(group.Rank - 1).ToList() : [];
            _pendingTags[key] = new PendingTag(name, groupId, parents);
        }
        ApplyEdit(i => i.TagKeys.Add(key));
        section.Search = "";
        RebuildPrefixOptions();
        RefreshTagChoices();
        CollapseAfterSelect(section);
    }

    private string GroupLabel(long? groupId) => groupId is { } id && _groups.TryGetValue(id, out var group) ? group.Name : "自由タグ";

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
        // 押した欄はそのまま(押した直後に並びが変わらないように)、紐づくタグの欄などを更新する
        var section = TagSections.FirstOrDefault(s => s.Choices.Contains(choice));
        RefreshTagChoices(keep: section);
        RebuildPrefixOptions();
        UpdateFileNamePreview();
        Edited?.Invoke();
        if (add && section is not null)
        {
            CollapseAfterSelect(section);
        }
        // 決定ログ DB-1: 検索で見つけた、上で選んでいるタグに紐づいていないタグを付けたとき
        if (add && AllTags().FirstOrDefault(t => t.Key == choice.Key) is { } tag)
        {
            _ = ConfirmLinkAsync(tag);
        }
    }

    // 決定ログ DB-1: 順位 2 以降のタグを付けるとき、上の順位で選んでいるタグに紐づいていなければ、紐づけを直すか確かめる
    private async Task ConfirmLinkAsync(TagInfo tag)
    {
        if (RankOf(tag.GroupId) is not { } rank || rank < 2 || (CatalogWriter is null && !tag.IsNew))
        {
            return;
        }
        var selected = CheckedKeysOfRank(rank - 1);
        if (selected.Count == 0 || tag.ParentKeys.Any(selected.Contains))
        {
            return;
        }
        var selectedText = string.Join("、", selected.Select(k => $"「{TagDisplayName(k)}」"));
        var current = tag.ParentKeys.Count == 0
            ? "どのタグにも紐づいていません"
            : $"{string.Join("、", tag.ParentKeys.Select(k => $"「{TagDisplayName(k)}」"))}に紐づいています";
        var shared = tag.UsageCount > 0 ? $"\n\n紐づけの変更は、このタグが付いているほかの PDF({tag.UsageCount:N0} 件)にも反映されます(すぐ保存します)。" : "";
        var choice = await _dialogs.AskChoiceAsync("紐づけの確認",
            $"「{tag.Name}」は{current}。今選んでいる{selectedText}には紐づいていません。\n\n"
            + $"・紐づけを追加:{selectedText}にも紐づけます\n"
            + $"・付け替える:今の紐づけを外して、{selectedText}だけに紐づけます\n"
            + "・そのまま付ける:紐づけは変えずに、このタグを付けます" + shared,
            "紐づけを追加", "付け替える", "そのまま付ける");
        if (choice != 0)
        {
            await SetParentsAsync(tag, choice == 1 ? tag.ParentKeys.Union(selected).ToList() : selected.ToList());
        }
    }

    // 紐づけ先を置き換える。新しいタグなら登録まで覚えておき、登録済みのタグならすぐ保存する
    private async Task<bool> SetParentsAsync(TagInfo tag, IReadOnlyList<string> parentKeys)
    {
        if (tag.IsNew)
        {
            if (_pendingTags.TryGetValue(tag.Key, out var pending))
            {
                _pendingTags[tag.Key] = pending with { ParentKeys = parentKeys };
            }
            RefreshTagChoices();
            return true;
        }
        // 紐づけ先がまだ登録していない新しいタグなら、先に作る
        return await EditCatalogAsync(catalog =>
        {
            var ids = ResolveTagIds(catalog, parentKeys);
            catalog.SetTagParents(tag.Id!.Value, parentKeys.Select(k => ids[k]));
        });
    }

    private async Task<bool> EditCatalogAsync(Action<CatalogRepository> action)
    {
        if (CatalogWriter is null)
        {
            return false;
        }
        try
        {
            return CatalogWriter(action);
        }
        catch (CatalogException ex)
        {
            await _dialogs.ShowErrorAsync(ex.Message);
            return false;
        }
    }

    // 決定ログ DB-2: タグの欄の右クリック「紐づけを変更」
    private async Task EditLinksAsync(TagChoice choice)
    {
        if (AllTags().FirstOrDefault(t => t.Key == choice.Key) is not { } tag || RankOf(tag.GroupId) is not { } rank || rank < 2)
        {
            return;
        }
        var picks = AllTags()
            .Where(t => RankOf(t.GroupId) == rank - 1)
            .OrderBy(t => t.Key, StringComparer.Ordinal)
            .Select(t => new TagPick(t.Key, t.Name, GroupLabel(t.GroupId) + (t.IsNew ? "(新規)" : "")))
            .ToList();
        var shared = tag.UsageCount > 0 ? $"このタグが付いているほかの PDF({tag.UsageCount:N0} 件)にも反映されます。" : "";
        var selected = _dialogs.ChooseTags($"「{tag.Name}」の紐づけ",
            $"「{tag.Name}」を紐づける「{TagRanks.Label(rank - 1)}」のタグを選んでください(複数可)。OK を押すとすぐ保存します。{shared}",
            picks, tag.ParentKeys.ToHashSet());
        if (selected is not null && !selected.ToHashSet().SetEquals(tag.ParentKeys))
        {
            await SetParentsAsync(tag, selected);
        }
    }

    // 決定ログ DB-2: タグの欄の右クリック「名前を変更」
    private async Task RenameTagAsync(TagChoice choice)
    {
        if (AllTags().FirstOrDefault(t => t.Key == choice.Key) is not { } tag)
        {
            return;
        }
        var input = _dialogs.AskText("タグの名前の変更", $"「{tag.Name}」の新しい名前", tag.Name);
        if (input is null || NameNormalizer.Clean(input) is not { Length: > 0 } name || name == tag.Name)
        {
            return;
        }
        var newKey = NameNormalizer.Normalize(name);
        if (tag.IsNew)
        {
            if (newKey != tag.Key && AllTags().Any(t => t.Key == newKey || t.HasAliasKey(newKey)))
            {
                await _dialogs.ShowErrorAsync($"「{name}」というタグ(または別名)がすでにあります。");
                return;
            }
            var pending = _pendingTags[tag.Key];
            _pendingTags.Remove(tag.Key);
            _pendingTags[newKey] = pending with { Name = name };
        }
        else
        {
            if (tag.UsageCount > 0 && !await _dialogs.ConfirmAsync("タグの名前の変更",
                    $"「{tag.Name}」は {tag.UsageCount:N0} 件の PDF に付いています。名前を変えると、それらの PDF のタグの名前も「{name}」に変わります(すぐ保存します)。変えますか?", "変える"))
            {
                return;
            }
            if (!await EditCatalogAsync(catalog => catalog.RenameTag(tag.Id!.Value, name)))
            {
                return;
            }
        }
        if (newKey != tag.Key)
        {
            RenameKey(tag.Key, newKey);
        }
        RebuildPrefixOptions();
        RefreshTagChoices();
        UpdateFileNamePreview();
    }

    // 名前が変わったタグのキーを、入力中の PDF の中で付け替える
    private void RenameKey(string oldKey, string newKey)
    {
        foreach (var item in _targets)
        {
            if (item.TagKeys.Remove(oldKey))
            {
                item.TagKeys.Add(newKey);
            }
        }
        foreach (var (key, pending) in _pendingTags.ToList())
        {
            if (pending.ParentKeys.Contains(oldKey))
            {
                _pendingTags[key] = pending with { ParentKeys = pending.ParentKeys.Select(k => k == oldKey ? newKey : k).ToList() };
            }
        }
        TagKeyChanged?.Invoke(oldKey, newKey);
    }

    private void RefreshTagChoices(TagSection? keep = null)
    {
        OnPropertyChanged(nameof(HasFirstRankTag));
        var wasLoading = _loading;
        _loading = true;
        try
        {
            var all = AllTags().ToList();
            var counts = all.ToDictionary(t => t.Key, t => _targets.Count(i => i.TagKeys.Contains(t.Key)));
            foreach (var section in TagSections.Where(s => s != keep))
            {
                RefreshSection(section, all, counts);
            }
            var lines = TagSections
                .Select(s => (Section: s, Names: all
                    .Where(t => t.GroupId == s.Group?.Id && counts[t.Key] > 0)
                    .OrderBy(t => t.Key, StringComparer.Ordinal)
                    .Select(t => counts[t.Key] == _targets.Count ? t.Name : $"{t.Name}(一部)")
                    .ToList()))
                .ToList();
            foreach (var (section, names) in lines)
            {
                section.Summary = string.Join("、", names);
            }
            AssignedTags = lines
                .Where(x => x.Names.Count > 0)
                .Select(x => new AssignedTagLine(x.Section.Title, string.Join("、", x.Names)))
                .ToList();
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void RefreshSection(TagSection section, List<TagInfo> all, Dictionary<string, int> counts)
    {
        if (_targets.Count == 0)
        {
            section.Choices = [];
            section.Hint = "";
            return;
        }
        var members = all.Where(t => t.GroupId == section.Group?.Id).ToList();
        // ひらがな・カタカナの違いと別名も吸収して探す(決定ログ DE-2・DE-3)
        var search = DocumentSearchIndex.Normalize(section.Search);
        bool NameMatches(TagInfo t) => t.Folded.Contains(search, StringComparison.Ordinal);
        bool AliasMatches(TagInfo t) => t.FoldedAliases.Any(a => a.Contains(search, StringComparison.Ordinal));
        IEnumerable<TagInfo> visible;
        if (search.Length > 0)
        {
            // 検索したときは、上の順位を選んでいなくても見つかる(決定ログ R-11 ②)
            visible = members.Where(t => counts[t.Key] > 0 || NameMatches(t) || AliasMatches(t));
            section.Hint = members.Any(t => NameMatches(t) || AliasMatches(t)) ? "" : $"見つかりません。Enter で「{NameNormalizer.Clean(section.Search)}」を新しく作って付けます。";
        }
        else if (section.Group is { Rank: >= 2 } group)
        {
            var parents = CheckedKeysOfRank(group.Rank - 1);
            visible = members.Where(t => counts[t.Key] > 0 || t.ParentKeys.Any(parents.Contains));
            section.Hint = parents.Count == 0
                ? $"「{TagRanks.Label(group.Rank - 1)}」のタグを選ぶと、紐づくタグが表示されます。検索欄で探して付けることもできます。"
                : visible.Any() ? "" : "選んだタグに紐づくタグはありません。検索欄で探して付けられます。";
        }
        else
        {
            visible = members;
            section.Hint = members.Count == 0 ? "タグがありません。検索欄に入力して Enter で追加できます。" : "";
        }
        section.Choices = visible
            .Select(t =>
            {
                var count = counts[t.Key];
                bool? state = count == 0 ? false : count == _targets.Count ? true : null;
                var detail = section.IsDependent ? string.Join("、", t.ParentKeys.Select(TagDisplayName)) : "";
                // 別名で見つかったときは、どの別名かを出す
                if (search.Length > 0 && !NameMatches(t) && t.AliasNames.FirstOrDefault(a => DocumentSearchIndex.Normalize(a).Contains(search, StringComparison.Ordinal)) is { } alias)
                {
                    detail = detail.Length == 0 ? $"別名「{alias}」" : $"{detail} ・ 別名「{alias}」";
                }
                var canEdit = t.IsNew || CatalogWriter is not null;
                return new TagChoice(t.Key, t.Name, detail, t.IsNew, state, OnTagToggled,
                    canEdit && section.IsDependent ? EditLinksAsync : null, canEdit ? RenameTagAsync : null);
            })
            .OrderByDescending(c => c.IsChecked != false)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .ToList();
    }

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
                new FileNamePrefixChoice(FileNamePrefixChoice.FieldKind, f.Id.ToString(CultureInfo.InvariantCulture)), $"補助項目:{f.Name}")));

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
        : $"補助項目:{FieldByKey(choice.Key)?.Name ?? "(削除された補助項目)"}";

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
        var assigned = AssignNames(_targets);
        var first = assigned[_targets[0]];
        FileNamePreview = _targets.Count == 1
            ? $"→ {first.FileName}"
            : $"→ {first.FileName} など({_targets.Count} 件それぞれに作られます)";

        var warnings = new List<string>();
        if (_targets.Any(i => (i.StoredFileName is null || i.FileNameChanged) && ComputeFileName(i).ReplacedInvalidCharacters))
        {
            warnings.Add("ファイル名に使えない文字を「_」に置き換えました。");
        }
        // 決定ログ R-10・T-4: 番号が付くときは、付く前から知らせる
        if (_targets.Any(i => assigned[i].Number > 0))
        {
            warnings.Add(_targets.Count == 1
                ? $"同じファイル名がすでにあるため、タイトルとファイル名の末尾に「_{first.Number}」を付けて{(_targets[0].StoredFileName is null ? "登録" : "保存")}します。"
                : "同じファイル名がすでにあるものは、タイトルとファイル名の末尾に「_2」などの番号を付けます。");
        }
        CanRebuildFileName = _targets.Any(i => i.StoredFileName is not null && !i.FileNameChanged
            && !string.Equals(i.StoredFileName, ComputeFileName(i).FileName, StringComparison.Ordinal));
        if (CanRebuildFileName)
        {
            warnings.Add("タグ名などが変わっています。「タグから作り直す」でファイル名に反映できます。");
        }
        FileNameWarning = warnings.Count == 0 ? null : string.Join(" ", warnings);
    }

    // 決定ログ R-10・T-6: 「新しい版を追加」では番号を付けない(版は前の版と同じ名前で続ける)
    public bool NumberDuplicateNames { get; set; } = true;

    // 決定ログ R-10・T-1〜T-5・T-7: 保存・登録するタイトルとファイル名。ライブラリの他の文書や、一緒に保存・登録するものと
    // ファイル名が同じなら「_2」などを付ける(Number が 0 なら付けない)。登録済みでファイル名を変えていない文書には付けない
    public Dictionary<EditableDocument, (string Title, string FileName, int Number)> AssignNames(IReadOnlyList<EditableDocument> items)
    {
        var own = items.Select(i => i.StoredFileName).OfType<string>().Select(UniqueNames.Key).ToHashSet();
        var taken = _existingFileNames.Select(UniqueNames.Key).Where(k => !own.Contains(k)).ToHashSet();
        var result = new Dictionary<EditableDocument, (string, string, int)>();
        foreach (var item in items)
        {
            var title = TitleFor(item);
            var fileName = FileNameFor(item);
            var keepAsIs = !NumberDuplicateNames || (item.StoredFileName is not null && !item.FileNameChanged);
            if (keepAsIs)
            {
                taken.Add(UniqueNames.Key(fileName));
                result[item] = (title, fileName, 0);
            }
            else
            {
                result[item] = UniqueNames.Assign(title, fileName, taken);
            }
        }
        return result;
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

    // 新しいタグはここで登録する(グループと、作ったときに上で選んでいたタグへの紐づけ付き、決定ログ Q-12)。
    // 紐づけ先も新しいタグのことがあるため、順位の小さい順に作る
    public Dictionary<string, long> ResolveTagIds(CatalogRepository catalog, IEnumerable<string> keys)
    {
        var ids = new Dictionary<string, long>();
        foreach (var key in keys.Distinct().OrderBy(k => _pendingTags.TryGetValue(k, out var p) ? RankOf(p.GroupId) ?? 0 : -1))
        {
            if (_tags.TryGetValue(key, out var tag))
            {
                ids[key] = tag.Id;
                continue;
            }
            var pending = _pendingTags[key];
            var parentIds = pending.ParentKeys
                .Select(p => _tags.TryGetValue(p, out var parent) ? parent.Id : ids.GetValueOrDefault(p))
                .Where(id => id != 0)
                .ToList();
            ids[key] = catalog.GetOrCreateTag(pending.Name, pending.GroupId, parentIds);
        }
        return ids;
    }

    public (FileNameSource Source, long? SourceId) FileNameSourceFor(EditableDocument item, Dictionary<string, long> tagIds) => item.UseTitleAsFileName
        ? (FileNameSource.Title, null)
        : item.Prefix switch
        {
            { Kind: FileNamePrefixChoice.TagKind } p when item.TagKeys.Contains(p.Key) => (FileNameSource.Tag, tagIds[p.Key]),
            { Kind: FileNamePrefixChoice.FieldKind } p when PrefixValue(item) is not null => (FileNameSource.Field, long.Parse(p.Key, CultureInfo.InvariantCulture)),
            _ => (FileNameSource.None, null),
        };
}
