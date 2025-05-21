using System;
using System.Windows;
using System.Windows.Controls; // Specifically using System.Windows.Controls for CheckBox
using System.Threading.Tasks;
using System.Windows.Input; // For MouseWheelEventArgs

namespace RepoToTxtGui;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private MainWindowViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;
    }

    private async void CheckBox_Checked(object sender, RoutedEventArgs e)
    {
        await HandleTreeNodeCheckChange(sender);
    }

    private async void CheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        await HandleTreeNodeCheckChange(sender);
    }

    private async Task HandleTreeNodeCheckChange(object sender)
    {
        if (DataContext is MainWindowViewModel vm &&
            sender is CheckBox { DataContext: TreeNodeViewModel nodeVm } && // Modern pattern matching
            vm.IsUiEnabled)
        {
            // The IsChecked property of TreeNodeViewModel is bound TwoWay.
            // Its setter handles propagation and INotifyPropertyChanged.
            // We just need to ensure that after this UI-driven change is processed,
            // the output is regenerated.
            await Application.Current.Dispatcher.BeginInvoke(async () =>
            {
                if (vm.IsUiEnabled) // Re-check, as state might change during dispatcher processing
                {
                    await vm.RegenerateOutputAsync();
                }
            }, System.Windows.Threading.DispatcherPriority.Background); // Use Background priority
        }
    }
    private void TreeViewBorder_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Find the ScrollViewer in the visual tree
        if (!e.Handled && FindName("TreeViewScrollViewer") is ScrollViewer scrollViewer)
        {
            // Manually scroll the TreeViewScrollViewer
            // Adjust delta for natural scrolling direction
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - (e.Delta * 0.5)); // Adjust multiplier for scroll speed
            e.Handled = true;
        }
    }
}