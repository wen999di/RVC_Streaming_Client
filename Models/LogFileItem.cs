namespace ClientAvalonia.Models;

public sealed class LogFileItem
{
    public string FileName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public override string ToString()
    {
        return DisplayName;
    }
}