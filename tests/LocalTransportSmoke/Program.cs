using System.Text;
using System.Text.Json;
using ClientAvalonia.Services;

if (args.Length == 1 && args[0] == "--download-source")
{
    var target = Path.Combine(
        AppPaths.LocalServerDataDirectory,
        "download-smoke-" + Guid.NewGuid().ToString("N")
    );
    try
    {
        var result = await LocalServerEnvironmentChecker.DownloadServerAsync(target);
        if (!result.Success)
            throw new InvalidOperationException($"Server source download failed: {result.Summary}\n{result.Details}");
        if (!LocalServerEnvironmentChecker.HasServerLayout(target))
            throw new InvalidDataException("Downloaded Server layout is invalid.");
        if (Directory.Exists(Path.Combine(target, ".git")))
            throw new InvalidDataException("Downloaded Server unexpectedly contains Git history.");
        Console.WriteLine("GitHub source snapshot smoke test passed.");
        return;
    }
    finally
    {
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }
}

if (args.Length == 2 && args[0] == "--install-dependencies")
{
    var result = await LocalServerEnvironmentChecker.InstallDependenciesAsync(args[1]);
    if (!result.Success)
        throw new InvalidOperationException($"Dependency installation failed: {result.Summary}\n{result.Details}");
    Console.WriteLine("Pixi dependency installation smoke test passed.");
    return;
}

if (args.Length != 1)
    throw new ArgumentException("Expected the RVC Streaming Server directory.");

var environment = await LocalServerEnvironmentChecker.CheckAsync(args[0]);
if (!environment.Success)
    throw new InvalidOperationException($"Local environment check failed: {environment.Summary}\n{environment.Details}");

var pong = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
await using var session = new LocalServerSession(args[0]);
session.TextMessageReceived += (_, message) =>
{
    using var document = JsonDocument.Parse(message);
    if (document.RootElement.TryGetProperty("type", out var type)
        && type.GetString() == "pong")
    {
        pong.TrySetResult(document.RootElement.Clone());
    }
};

await session.StartAsync(CancellationToken.None);
if (!session.IsOpen)
    throw new InvalidOperationException("The local process pipe did not become ready.");

var request = JsonSerializer.SerializeToUtf8Bytes(new { command = "ping", ts = 456 });
await session.SendTextAsync(LocalServerChannel.Control, request);
var response = await pong.Task.WaitAsync(TimeSpan.FromSeconds(15));
if (response.GetProperty("client_ts").GetInt32() != 456)
    throw new InvalidDataException("The local process pipe returned the wrong ping timestamp.");

Console.WriteLine("Local transport smoke test passed.");
