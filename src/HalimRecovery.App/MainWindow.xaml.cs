using System.Windows;
using System.Windows.Controls;
using HalimRecovery.App.ViewModels;

namespace HalimRecovery.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => _vm.GridSelection = ResultsGrid.SelectedItems.Cast<FileItemVm>().ToList();
}
