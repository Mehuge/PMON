using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PMON.Services;
using PMON.ViewModels;
using PMON.Views;

namespace PMON;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel(AppSettings.Load());
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.Exit += (_, _) => viewModel.Dispose();
            viewModel.Start();

            if (desktop.Args is { Length: > 0 } args && File.Exists(args[0]))
                _ = viewModel.LoadRecordingAsync(args[0]);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
