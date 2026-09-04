using System.Diagnostics;

namespace ClientAvalonia.Services;

public static class AppPaths
{
    public static string ExecutableDirectory { get; } =
        Path.GetFullPath(AppContext.BaseDirectory);

    public static string DataDirectory { get; } =
        EnsureDirectory(Path.Combine(ExecutableDirectory, "data"));

    public static string LogsDirectory { get; } =
        EnsureDirectory(Path.Combine(DataDirectory, "logs"));

    public static string LocalServerDataDirectory { get; } =
        EnsureDirectory(Path.Combine(DataDirectory, "server"));

    public static string DefaultLocalServerDirectory { get; } =
        Path.Combine(ExecutableDirectory, "localServer");

    public static string ClientSettingsPath { get; } =
        Path.Combine(DataDirectory, "settings.json");

    public static void ConfigureLocalServerProcess(ProcessStartInfo startInfo)
    {
        var cacheDirectory = EnsureDirectory(Path.Combine(LocalServerDataDirectory, "cache"));
        var configDirectory = EnsureDirectory(Path.Combine(LocalServerDataDirectory, "config"));
        var stateDirectory = EnsureDirectory(Path.Combine(LocalServerDataDirectory, "state"));
        var temporaryDirectory = EnsureDirectory(Path.Combine(LocalServerDataDirectory, "temp"));
        var pythonCacheDirectory = EnsureDirectory(Path.Combine(cacheDirectory, "pycache"));

        startInfo.Environment["RVC_DATA_DIR"] = LocalServerDataDirectory;
        startInfo.Environment["TEMP"] = temporaryDirectory;
        startInfo.Environment["TMP"] = temporaryDirectory;
        startInfo.Environment["TMPDIR"] = temporaryDirectory;
        startInfo.Environment["XDG_CACHE_HOME"] = cacheDirectory;
        startInfo.Environment["XDG_CONFIG_HOME"] = configDirectory;
        startInfo.Environment["XDG_DATA_HOME"] = LocalServerDataDirectory;
        startInfo.Environment["XDG_STATE_HOME"] = stateDirectory;
        startInfo.Environment["PYTHONPYCACHEPREFIX"] = pythonCacheDirectory;
        startInfo.Environment["PIP_CACHE_DIR"] = Path.Combine(cacheDirectory, "pip");
        startInfo.Environment["UV_CACHE_DIR"] = Path.Combine(cacheDirectory, "uv");
        startInfo.Environment["NUMBA_CACHE_DIR"] = Path.Combine(cacheDirectory, "numba");
        startInfo.Environment["MPLCONFIGDIR"] = Path.Combine(configDirectory, "matplotlib");
        startInfo.Environment["HF_HOME"] = Path.Combine(cacheDirectory, "huggingface");
        startInfo.Environment["HF_HUB_CACHE"] = Path.Combine(cacheDirectory, "huggingface", "hub");
        startInfo.Environment["HF_ASSETS_CACHE"] = Path.Combine(cacheDirectory, "huggingface", "assets");
        startInfo.Environment["MODELSCOPE_CACHE"] = Path.Combine(cacheDirectory, "modelscope");
        startInfo.Environment["MODELSCOPE_HOME"] = Path.Combine(cacheDirectory, "modelscope");
        startInfo.Environment["TORCH_HOME"] = Path.Combine(cacheDirectory, "torch");
        startInfo.Environment["TORCH_EXTENSIONS_DIR"] = Path.Combine(cacheDirectory, "torch-extensions");
        startInfo.Environment["TORCHINDUCTOR_CACHE_DIR"] = Path.Combine(cacheDirectory, "torchinductor");
        startInfo.Environment["TRITON_CACHE_DIR"] = Path.Combine(cacheDirectory, "triton");
        startInfo.Environment["CUDA_CACHE_PATH"] = Path.Combine(cacheDirectory, "cuda");
        startInfo.Environment["PIXI_CACHE_DIR"] = Path.Combine(cacheDirectory, "pixi");
    }

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
