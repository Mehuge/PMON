using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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

    // Button runs its Command after Click; hiding the flyout synchronously would detach the row first.
    private void OnProcessPicked(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() => ProcessPicker.Flyout?.Hide());

    private void OnEditRules(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        SettingsButton.Flyout?.Hide();

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
