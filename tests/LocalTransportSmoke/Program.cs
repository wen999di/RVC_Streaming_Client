using System.Text;
using System.Text.Json;
using ClientAvalonia.Services;

if (args.Length == 1 && args[0] == "--download-source")
{
    var target = Path.Combine(
        AppPaths.LocalServerDataDirectory,
        "download-smoke-" + Guid.NewGuid().ToString("N")
    );
    var legacyTarget = target + "-legacy";
    try
    {
        var result = await LocalServerEnvironmentChecker.DownloadServerAsync(target);
        if (!result.Success)
            throw new InvalidOperationException($"Server source download failed: {result.Summary}\n{result.Details}");
        if (!LocalServerEnvironmentChecker.HasServerLayout(target))
            throw new InvalidDataException("Downloaded Server layout is invalid.");
        if (Directory.Exists(Path.Combine(target, ".git")))
            throw new InvalidDataException("Downloaded Server unexpectedly contains Git history.");

        var current = await LocalServerEnvironmentChecker.CheckForUpdatesAsync(target);
        if (!current.Success || current.UpdateAvailable)
            throw new InvalidOperationException("Freshly downloaded source was not recognized as current.");

        CopyDirectory(target, legacyTarget);
        await File.AppendAllTextAsync(Path.Combine(legacyTarget, "server.py"), "\n# update smoke mutation\n");
        var legacyCheck = await LocalServerEnvironmentChecker.CheckForUpdatesAsync(legacyTarget);
        if (!legacyCheck.Success || !legacyCheck.UpdateAvailable)
            throw new InvalidOperationException("Changed legacy source was not recognized as outdated.");

        var pixiSentinel = Path.Combine(legacyTarget, ".pixi", "preserve.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(pixiSentinel)!);
        await File.WriteAllTextAsync(pixiSentinel, "preserve");
        var update = await LocalServerEnvironmentChecker.UpdateServerAsync(legacyTarget);
        if (!update.Success)
            throw new InvalidOperationException($"Source update failed: {update.Summary}\n{update.Details}");
        if (!File.Exists(pixiSentinel))
            throw new InvalidDataException("Source update did not preserve the Pixi environment.");

        var updated = await LocalServerEnvironmentChecker.CheckForUpdatesAsync(legacyTarget);
        if (!updated.Success || updated.UpdateAvailable)
            throw new InvalidOperationException("Updated source was not recognized as current.");
        Console.WriteLine("GitHub source snapshot smoke test passed.");
        return;
    }
    finally
    {
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        if (Directory.Exists(legacyTarget)) Directory.Delete(legacyTarget, recursive: true);
    }
}

if (args.Length == 2 && args[0] == "--install-dependencies")
{
    var outputLineCount = 0;
    var output = new DirectProgress<string>(line =>
    {
        Interlocked.Increment(ref outputLineCount);
        Console.WriteLine(line);
    });
    var result = await LocalServerEnvironmentChecker.InstallDependenciesAsync(args[1], output);
    if (!result.Success)
        throw new InvalidOperationException($"Dependency installation failed: {result.Summary}\n{result.Details}");
    if (outputLineCount == 0)
        throw new InvalidOperationException("Pixi dependency installation emitted no live output events.");
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

static void CopyDirectory(string sourceDirectory, string destinationDirectory)
{
    Directory.CreateDirectory(destinationDirectory);
    foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
    {
        Directory.CreateDirectory(Path.Combine(
            destinationDirectory,
            Path.GetRelativePath(sourceDirectory, directory)
        ));
    }
    foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
    {
        var destination = Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, file));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(file, destination);
    }
}

sealed class DirectProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
