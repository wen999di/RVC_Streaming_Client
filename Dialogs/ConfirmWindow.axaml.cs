using Avalonia.Controls;
using Avalonia.Input;
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
        DialogTitleTextBlock.Text = title;
        MessageTextBlock.Text = message;
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private void Window_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close(false);
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
