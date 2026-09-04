namespace ClientAvalonia.Models;

public static class TrainingNameHelper
{
    public static string GetAvailableModelName(IEnumerable<string> occupiedNames)
    {
        var occupied = new HashSet<string>(
            occupiedNames.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var candidate = "my model";
        for (var suffix = 1; occupied.Contains(candidate); suffix++)
        {
            candidate = $"my model {suffix}";
        }
        return candidate;
    }
}
