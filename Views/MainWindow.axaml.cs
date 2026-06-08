using Avalonia.Controls;
using jxr2uhdr.ViewModels;

namespace jxr2uhdr.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
    }
}
