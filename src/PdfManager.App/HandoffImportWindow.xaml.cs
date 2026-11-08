using System.ComponentModel;
using System.Windows;
using PdfManager.App.Services;
using PdfManager.App.ViewModels;
using PdfManager.Core.Export;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PdfManager.App;

public sealed record HandoffNewRow(string Title, string Detail, string SizeText);

// 中身が同じで情報が違う PDF の 1 行(決定ログ EB-10)。選択肢の並びは HandoffChoice と同じ
public sealed class HandoffDifferentRow(HandoffExisting existing) : INotifyPropertyChanged
{
    private int _choiceIndex;

    public event PropertyChangedEventHandler? PropertyChanged;

    public HandoffExisting Existing { get; } = existing;

    public string Title => Existing.LocalTitle;

    public string Detail => string.Join("、", Existing.Differences)
        + (Existing.Writable ? "" : "(保存先のサブライブラリがつながっていないため、直せません)");

    public bool Writable => Existing.Writable;

    public IReadOnlyList<string> Choices { get; } = ["タグは足す・ほかは今のまま", "相手に合わせる", "そのまま"];

    public int ChoiceIndex
    {
        get => Writable ? _choiceIndex : (int)HandoffChoice.Keep;
        set
        {
            _choiceIndex = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChoiceIndex)));
        }
    }

    public HandoffChoice Choice => (HandoffChoice)ChoiceIndex;
}

// 受け渡しを取り込む(F-52、決定ログ EB-9〜EB-11)。見積もりを見せ、中身が同じ PDF の扱いを選んでもらう
public partial class HandoffImportWindow : FluentWindow
{
    private readonly HandoffImportPlan _plan;
    private readonly IReadOnlyList<HandoffDifferentRow> _different;

    public HandoffImportWindow(HandoffPackage package, HandoffImportPlan plan)
    {
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this);
        SystemThemeWatcher.Watch(this);
        _plan = plan;
        var manifest = package.Manifest;
        SourceText.Text = $"「{manifest.LibraryName}」から {manifest.Pc} で {manifest.ExportedAt.ToLocalTime():yyyy/MM/dd HH:mm} に書き出した受け渡し({manifest.Documents.Count:N0} 件)";
        SourcePath.Text = package.Source;
        OtherLibraryNote.Visibility = plan.SameLibrary ? Visibility.Collapsed : Visibility.Visible;

        var lines = new List<string>
        {
            $"新しく登録する PDF:{plan.NewDocuments.Count:N0} 件(合計 {ArchiveViewModel.FormatSize(plan.NewBytes)})",
            $"中身が同じ PDF:情報が同じ {plan.Same:N0} 件(何もしません)・情報が違う {plan.Different.Count:N0} 件(「中身が同じで情報が違う PDF」で選べます)",
        };
        if (plan.MissingFiles.Count > 0)
        {
            lines.Add($"受け渡しに PDF がなかったもの:{plan.MissingFiles.Count:N0} 件(取り込みません)" + string.Join("、", plan.MissingFiles.Take(5)));
        }
        SummaryText.Text = string.Join("\n", lines);
        var definitions = new List<string>();
        if (plan.NewGroups.Count > 0) definitions.Add("グループ:" + string.Join("、", plan.NewGroups));
        if (plan.NewTags.Count > 0) definitions.Add("タグ:" + string.Join("、", plan.NewTags.Take(30)) + (plan.NewTags.Count > 30 ? $" ほか {plan.NewTags.Count - 30} 個" : ""));
        if (plan.NewFields.Count > 0) definitions.Add("補助項目:" + string.Join("、", plan.NewFields));
        DefinitionsText.Text = definitions.Count == 0 ? "新しく作るタグなどはありません。" : "この PC に新しく作るもの … " + string.Join(" / ", definitions);

        NewList.ItemsSource = plan.NewDocuments.Select(d => new HandoffNewRow(d.Title,
            $"登録 {d.CreatedAt.ToLocalTime():yyyy/MM/dd}" + (d.Tags.Count > 0 ? " ・ " + string.Join("、", d.Tags) : ""), ArchiveViewModel.FormatSize(d.FileSize))).ToList();
        _different = plan.Different.Select(e => new HandoffDifferentRow(e)).ToList();
        DifferentList.ItemsSource = _different;
        NewTab.Header = $"新しく登録する PDF({plan.NewDocuments.Count:N0})";
        DifferentTab.Header = $"中身が同じで情報が違う PDF({plan.Different.Count:N0})";
        if (plan.NewDocuments.Count == 0 && plan.Different.Count > 0)
        {
            DifferentTab.IsSelected = true;
        }
        ImportButton.IsEnabled = plan.NewDocuments.Count > 0 || _different.Any(r => r.Writable);
    }

    public IReadOnlyDictionary<string, HandoffChoice> Choices { get; private set; } = new Dictionary<string, HandoffChoice>();

    private void SetAll(HandoffChoice choice)
    {
        foreach (var row in _different.Where(r => r.Writable))
        {
            row.ChoiceIndex = (int)choice;
        }
    }

    private void OnAllAddTagsClick(object sender, RoutedEventArgs e) => SetAll(HandoffChoice.AddTags);

    private void OnAllOverwriteClick(object sender, RoutedEventArgs e) => SetAll(HandoffChoice.Overwrite);

    private void OnAllKeepClick(object sender, RoutedEventArgs e) => SetAll(HandoffChoice.Keep);

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        if (!_plan.HasEnoughSpace)
        {
            await new WpfUiDialogs(this).ShowErrorAsync(
                $"ライブラリのあるドライブの空き容量が足りません。\n登録する PDF:{ArchiveViewModel.FormatSize(_plan.NewBytes)}\n空き容量:{ArchiveViewModel.FormatSize(_plan.FreeBytes ?? 0)}",
                "受け渡しを取り込む");
            return;
        }
        Choices = _different.ToDictionary(r => r.Existing.Document.Id, r => r.Choice);
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
