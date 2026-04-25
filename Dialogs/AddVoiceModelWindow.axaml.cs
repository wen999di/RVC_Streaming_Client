using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace ClientAvalonia.Dialogs;

public record AddVoiceModelResult(string Name, string PthDisplay, string IndexDisplay);

public partial class AddVoiceModelWindow : Window
{
    private string _pendingPth = string.Empty;
    private string _pendingIndex = string.Empty;

    public AddVoiceModelWindow()
    {
        InitializeComponent();

        PthDropBorder.AddHandler(DragDrop.DragOverEvent, PthBorder_DragOver);
        PthDropBorder.AddHandler(DragDrop.DropEvent, PthBorder_Drop);
        PthDropBorder.AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetHighlight(PthDropBorder, false));

        IndexDropBorder.AddHandler(DragDrop.DragOverEvent, IndexBorder_DragOver);
        IndexDropBorder.AddHandler(DragDrop.DropEvent, IndexBorder_Drop);
        IndexDropBorder.AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetHighlight(IndexDropBorder, false));
    }

    // ── drag over ──────────────────────────────────────────────────────────

    private void PthBorder_DragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.Text))
        {
            e.DragEffects = DragDropEffects.Copy;
            SetHighlight(PthDropBorder, true);
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void IndexBorder_DragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.Text))
        {
            e.DragEffects = DragDropEffects.Copy;
            SetHighlight(IndexDropBorder, true);
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    // ── drop ───────────────────────────────────────────────────────────────

    private void PthBorder_Drop(object? sender, DragEventArgs e)
    {
        SetHighlight(PthDropBorder, false);
        var text = e.DataTransfer.TryGetText();
        if (string.IsNullOrWhiteSpace(text)) return;

        var name = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                       .Select(t => t.Trim()).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        if (string.IsNullOrWhiteSpace(name)) return;

        var fileName = System.IO.Path.GetFileName(name);
        if (!fileName.EndsWith(".pth", StringComparison.OrdinalIgnoreCase))
            return;

        _pendingPth = name;
        PthFileText.Text = fileName;

        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
            NameTextBox.Text = System.IO.Path.GetFileNameWithoutExtension(fileName);
    }

    private void IndexBorder_Drop(object? sender, DragEventArgs e)
    {
        SetHighlight(IndexDropBorder, false);
        var text = e.DataTransfer.TryGetText();
        if (string.IsNullOrWhiteSpace(text)) return;

        var name = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                       .Select(t => t.Trim()).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        if (string.IsNullOrWhiteSpace(name)) return;

        var fileName = System.IO.Path.GetFileName(name);
        if (!fileName.EndsWith(".index", StringComparison.OrdinalIgnoreCase))
            return;

        _pendingIndex = name;
        IndexFileText.Text = fileName;
    }

    // ── buttons ────────────────────────────────────────────────────────────

    private void Confirm_OnClick(object? sender, RoutedEventArgs e)
    {
        var name = (NameTextBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            NameTextBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(_pendingPth))
        {
            // flash the border to indicate it's required
            SetHighlight(PthDropBorder, true);
            return;
        }

        Close(new AddVoiceModelResult(name, _pendingPth, _pendingIndex));
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static void SetHighlight(Border border, bool active)
    {
        border.BorderBrush = active
            ? new SolidColorBrush(Color.Parse("#38bdf8"))
            : new SolidColorBrush(Color.Parse("#D0D0D0"));
        border.BorderThickness = active ? new Avalonia.Thickness(2) : new Avalonia.Thickness(1);
    }
}
