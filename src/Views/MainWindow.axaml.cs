using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PMON.ViewModels;

namespace PMON.Views;

public partial class MainWindow : Window
{
    private RulesWindow? _rulesWindow;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnOpenRecording(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        var folder = Directory.Exists(viewModel.RecordingFolder)
            ? await StorageProvider.TryGetFolderFromPathAsync(viewModel.RecordingFolder)
            : null;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open recording",
            AllowMultiple = false,
            SuggestedStartLocation = folder,
            FileTypeFilter =
            [
                new FilePickerFileType("PMON recordings") { Patterns = ["*.csv"] },
                FilePickerFileTypes.All,
            ],
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            return;

        await viewModel.LoadRecordingAsync(path);
    }

    private void OnProcessPicked(object? sender, SelectionChangedEventArgs e) => ProcessPicker.Flyout?.Hide();

    private void OnEditRules(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        if (_rulesWindow is not null)
        {
            _rulesWindow.Activate();
            return;
        }

        _rulesWindow = new RulesWindow { DataContext = viewModel.Rules };
        _rulesWindow.Closed += (_, _) => _rulesWindow = null;
        _rulesWindow.Show(this);
    }

    private async void OnAbout(object? sender, RoutedEventArgs e) =>
        await new AboutWindow().ShowDialog(this);
}
