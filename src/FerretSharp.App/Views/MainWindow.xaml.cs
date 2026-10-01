using FerretSharp.App.Services;
using FerretSharp.App.ViewModels;
using Wpf.Ui.Controls;

namespace FerretSharp.App.Views;

public partial class MainWindow : FluentWindow
{
    public MainWindow(MainWindowViewModel viewModel, ThemeService themeService)
    {
        DataContext = viewModel;
        InitializeComponent();
        themeService.Attach(this, DockingManager);
    }
}
