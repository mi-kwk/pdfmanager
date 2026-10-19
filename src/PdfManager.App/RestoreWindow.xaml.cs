using System.IO;
using System.Windows;
using Microsoft.Win32;
using PdfManager.App.Services;
using PdfManager.Core.Backup;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PdfManager.App;

// 一覧の 1 行(バックアップ 1 回分)
public sealed record RestoreGenerationRow(BackupGeneration Generation)
{
    public string Title => Generation.CreatedAt.LocalDateTime.ToString("yyyy/MM/dd HH:mm");

    public string Detail => $"{Generation.DocumentCount:N0} 件の文書" + (Generation.MissingPdfCount > 0 ? $"(PDF のないもの {Generation.MissingPdfCount:N0} 件)" : "");
}

// 決定ログ BB-7: バックアップから戻す。どのライブラリ・いつの状態を、どこへ戻すかを選ぶ
public partial class RestoreWindow : FluentWindow
{
    public RestoreWindow(IReadOnlyList<BackupInfo> backups)
    {
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this);
        SystemThemeWatcher.Watch(this);
        LibraryChoice.ItemsSource = backups;
        LibraryChoice.SelectedIndex = 0;
    }

    public BackupInfo? SelectedBackup { get; private set; }

    public BackupGeneration? SelectedGeneration { get; private set; }

    public string? Destination { get; private set; }

    private void OnLibraryChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var rows = LibraryChoice.SelectedItem is BackupInfo backup
            ? backup.Generations.OrderByDescending(g => g.Name, StringComparer.Ordinal).Select(g => new RestoreGenerationRow(g)).ToList()
            : [];
        GenerationList.ItemsSource = rows;
        GenerationList.SelectedIndex = rows.Count > 0 ? 0 : -1;
    }

    private void OnChooseDestinationClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "戻す先のフォルダを選択(空のフォルダ)" };
        if (dialog.ShowDialog(this) == true)
        {
            DestinationBox.Text = dialog.FolderName;
        }
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        var dialogs = new WpfUiDialogs(this);
        if (LibraryChoice.SelectedItem is not BackupInfo backup || GenerationList.SelectedItem is not RestoreGenerationRow row)
        {
            await dialogs.ShowErrorAsync("戻すライブラリと、いつの状態に戻すかを選んでください。", "バックアップから戻す");
            return;
        }
        var destination = DestinationBox.Text.Trim();
        if (destination.Length == 0 || !Directory.Exists(destination))
        {
            await dialogs.ShowErrorAsync("戻す先のフォルダを選んでください(あるフォルダを選びます)。", "バックアップから戻す");
            return;
        }
        SelectedBackup = backup;
        SelectedGeneration = row.Generation;
        Destination = destination;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
