using System.Windows;
using Wpf.Ui.Controls;

namespace PdfManager.App.Services;

public enum PendingChangesChoice
{
    Save,
    Discard,
    Cancel,
}

public interface IUserDialogs
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText);

    Task ShowErrorAsync(string message, string title = "入力を確認してください");

    // 決定ログ J-1: 保存していない変更があるときの確認
    Task<PendingChangesChoice> AskPendingChangesAsync();

    // 3 つから選ぶ確認。1 = primary、2 = secondary、0 = close(閉じたときも 0)
    Task<int> AskChoiceAsync(string title, string message, string primary, string secondary, string close);

    // 1 行の文字の入力。キャンセルなら null
    string? AskText(string title, string label, string initialText);

    // 決定ログ DB-2: タグを選ぶ(紐づけ先など)。キャンセルなら null
    IReadOnlyList<string>? ChooseTags(string title, string message, IReadOnlyList<TagPick> choices, IReadOnlyCollection<string> selectedKeys);
}

// タグを選ぶ画面の 1 件(Key は登録・タグ設定の画面でのタグのキー)
public sealed record TagPick(string Key, string Name, string Detail);

public sealed class WpfUiDialogs(Window? owner) : IUserDialogs
{
    // 決定ログ HD-1: 確認画面は親の画面の中央に出す。指定しないと主ディスプレイの中央に出て、
    // 親の画面を別のディスプレイに置いているときに離れた場所に出てしまう
    private WindowStartupLocation StartupLocation => owner is { IsLoaded: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Owner = owner,
            WindowStartupLocation = StartupLocation,
            Title = title,
            Content = message,
            PrimaryButtonText = confirmText,
            CloseButtonText = "キャンセル",
        };
        return await box.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }

    public async Task ShowErrorAsync(string message, string title = "入力を確認してください")
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Owner = owner,
            WindowStartupLocation = StartupLocation,
            Title = title,
            Content = message,
            CloseButtonText = "OK",
        };
        await box.ShowDialogAsync();
    }

    public async Task<PendingChangesChoice> AskPendingChangesAsync()
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Owner = owner,
            WindowStartupLocation = StartupLocation,
            Title = "保存していない変更があります",
            Content = "詳細で編集した内容を保存しますか?",
            PrimaryButtonText = "保存する",
            SecondaryButtonText = "保存しない",
            CloseButtonText = "キャンセル",
        };
        return await box.ShowDialogAsync() switch
        {
            Wpf.Ui.Controls.MessageBoxResult.Primary => PendingChangesChoice.Save,
            Wpf.Ui.Controls.MessageBoxResult.Secondary => PendingChangesChoice.Discard,
            _ => PendingChangesChoice.Cancel,
        };
    }

    public async Task<int> AskChoiceAsync(string title, string message, string primary, string secondary, string close)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Owner = owner,
            WindowStartupLocation = StartupLocation,
            Title = title,
            Content = message,
            PrimaryButtonText = primary,
            SecondaryButtonText = secondary,
            CloseButtonText = close,
        };
        return await box.ShowDialogAsync() switch
        {
            Wpf.Ui.Controls.MessageBoxResult.Primary => 1,
            Wpf.Ui.Controls.MessageBoxResult.Secondary => 2,
            _ => 0,
        };
    }

    public string? AskText(string title, string label, string initialText)
    {
        var window = new TextInputWindow(title, label, initialText) { Owner = owner };
        window.ShowDialog();
        return window.Result;
    }

    public IReadOnlyList<string>? ChooseTags(string title, string message, IReadOnlyList<TagPick> choices, IReadOnlyCollection<string> selectedKeys)
    {
        var window = new TagPickWindow(title, message, choices, selectedKeys) { Owner = owner };
        window.ShowDialog();
        return window.Result;
    }
}
