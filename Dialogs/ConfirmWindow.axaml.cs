using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ClientAvalonia.Dialogs;

public partial class ConfirmWindow : Window
{
    public ConfirmWindow()
    {
        InitializeComponent();
    }

    public ConfirmWindow(string title, string message)
        : this()
    {
        Title = title;
        MessageTextBlock.Text = message;
    }

    private void Confirm_OnClick(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}