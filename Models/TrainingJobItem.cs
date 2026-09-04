using System;

namespace ClientAvalonia.Models;

public sealed class TrainingJobItem
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string Stage { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public double Progress { get; init; }
    public int Epoch { get; init; }
    public double Loss { get; init; }
    public string ModelFile { get; init; } = string.Empty;
    public string IndexFile { get; init; } = string.Empty;

    public bool CanCancel => State is "queued" or "running" or "cancelling";
    public string ProgressText => $"{Progress * 100:0.0}%";
    public string DetailText
    {
        get
        {
            var epochText = Epoch > 0 ? $" · Epoch {Epoch}" : string.Empty;
            var lossText = Loss > 0 ? $" · Loss {Loss:0.0000}" : string.Empty;
            return $"{State} · {Stage}{epochText}{lossText} · {Message}";
        }
    }
}
