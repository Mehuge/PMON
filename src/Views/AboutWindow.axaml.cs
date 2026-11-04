using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PMON.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?.Split('+')[0] ?? "1.0.0";
        VersionText.Text = $"Version {version}";
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
