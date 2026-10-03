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
}

public sealed class WpfUiDialogs(Window? owner) : IUserDialogs
{
    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Owner = owner,
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
}
