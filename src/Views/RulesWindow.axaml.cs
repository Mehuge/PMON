using Avalonia.Controls;
using Avalonia.Interactivity;
using PMON.ViewModels;

namespace PMON.Views;

public partial class RulesWindow : Window
{
    public RulesWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        (DataContext as RulesViewModel)?.Activate();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        (DataContext as RulesViewModel)?.Deactivate();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
