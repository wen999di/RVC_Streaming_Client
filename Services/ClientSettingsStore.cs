using System.Text.Json;

namespace ClientAvalonia.Services;

public sealed class ClientSettings
{
    public int Version { get; set; } = 3;
    public string ServerUri { get; set; } = string.Empty;
    public string ConnectionMode { get; set; } = "remote";
    public string LocalServerDirectory { get; set; } = string.Empty;
    public bool LocalServerEnvironmentVerified { get; set; }

    public int F0UpKey { get; set; }
    public float BlockTimeSeconds { get; set; } = 0.25f;
    public float CrossfadeSeconds { get; set; } = 0.04f;
    public float ExtraTimeSeconds { get; set; } = 2.0f;
    public int ServerStreamChunkMs { get; set; } = 20;
    public float FormantShift { get; set; }
    public string F0Method { get; set; } = "rmvpe";
    public float IndexRate { get; set; } = 0.5f;
    public float SilenceDbThreshold { get; set; } = -70.0f;
    public float SilenceGateAttenuation { get; set; }
    public bool InputNoiseReduce { get; set; }
    public bool OutputNoiseReduce { get; set; }
    public float NoiseReduceStrength { get; set; } = 0.9f;
    public float RmsMixRate { get; set; } = 0.8f;

    public bool UseAdaptiveBuffer { get; set; } = true;
    public int TargetBufferLatencyMs { get; set; } = 40;
    public int MaxBufferMs { get; set; } = 500;
    public int BufferCapacityMs { get; set; } = 1500;
    public int NetworkSliceMs { get; set; } = 20;
    public double JitterFactor { get; set; } = 1.5;
    public double JitterAlpha { get; set; } = 0.90;
    public double JitterMaxBufferMs { get; set; } = 350;
    public double MinNetworkProtectionMs { get; set; }
}

public static class ClientSettingsStore
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string SettingsPath => AppPaths.ClientSettingsPath;

    public static ClientSettings Load()
    {
        lock (Sync)
        {
            try
            {
                if (!File.Exists(SettingsPath)) return new ClientSettings();
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<ClientSettings>(json, JsonOptions) ?? new ClientSettings();
            }
            catch
            {
                return new ClientSettings();
            }
        }
    }

    public static void Save(ClientSettings settings)
    {
        lock (Sync)
        {
            var path = SettingsPath;
            var directory = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("无法确定客户端设置目录");
            Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, path, true);
        }
    }
}
