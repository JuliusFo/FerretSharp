using CommunityToolkit.Mvvm.ComponentModel;

namespace FerretSharp.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Title { get; set; } = "FerretSharp";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Not connected";
}
