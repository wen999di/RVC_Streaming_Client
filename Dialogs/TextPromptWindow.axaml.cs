using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ClientAvalonia.Dialogs;

public partial class TextPromptWindow : Window
{
    public TextPromptWindow()
    {
        InitializeComponent();
    }

    public TextPromptWindow(string title, string prompt, string initialValue = "", string placeholderText = "")
        : this()
    {
        Title = title;
        PromptTextBlock.Text = prompt;
        InputTextBox.Text = initialValue;
        InputTextBox.PlaceholderText = placeholderText;
    }

    private void Confirm_OnClick(object? sender, RoutedEventArgs e)
    {
        Close((InputTextBox.Text ?? string.Empty).Trim());
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}