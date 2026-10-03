using CommunityToolkit.Mvvm.ComponentModel;
using PdfManager.Core.Import;

namespace PdfManager.App.ViewModels;

// 取り込み画面と詳細エリアで共通の、1 文書分の入力内容。入力欄の内容は選択を変えても残るよう、ここに持つ
public abstract class EditableDocument : ObservableObject
{
    public abstract string DisplayName { get; }

    public abstract string OriginalFileName { get; }

    public virtual IReadOnlyList<string> TitleCandidates => [];

    public string Title { get; set; } = "";

    public bool UseTitleAsFileName { get; set; }

    public FileNamePrefixChoice? Prefix { get; set; }

    public string Suffix { get; set; } = "";

    // 項目 ID → 入力された文字(形式のチェックは保存・登録時)
    public Dictionary<long, string> FieldInputs { get; } = [];

    // 正規化したタグ名(登録前の新しいタグも同じように扱うため名前で持つ)
    public HashSet<string> TagKeys { get; } = [];

    public int Rating { get; set; }

    public string Memo { get; set; } = "";

    // 登録済みのファイル名。null(取り込み)なら常に入力から作る。
    // 登録済みの文書は、ファイル名の部分を直接変えたときか「作り直す」を押したときだけ作り直す(決定ログ J-3、C-8)
    public string? StoredFileName { get; set; }

    public bool FileNameChanged { get; set; }
}
