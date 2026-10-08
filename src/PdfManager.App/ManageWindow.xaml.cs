using PdfManager.App.Services;
using PdfManager.App.ViewModels;
using PdfManager.Core.Storage;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PdfManager.App;

public partial class ManageWindow : FluentWindow
{
    public ManageWindow(DataFolder dataFolder)
    {
        InitializeComponent();
        WindowPlacement.KeepOnScreen(this);
        DataContext = new ManageViewModel(dataFolder, new WpfUiDialogs(this));
        SystemThemeWatcher.Watch(this);
    }
}
