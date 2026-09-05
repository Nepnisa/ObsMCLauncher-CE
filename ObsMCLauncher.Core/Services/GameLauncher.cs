using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ObsMCLauncher.Core.Models;
using ObsMCLauncher.Core.Plugins;
using ObsMCLauncher.Core.Services.Accounts;
using ObsMCLauncher.Core.Utils;

namespace ObsMCLauncher.Core.Services;

public class GameLauncher
{
    private static readonly JsonSerializerOptions CachedJsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task<GameIntegrityResult> CheckGameIntegrityAsync(
        string versionId,
        LauncherConfig config,
        Action<string>? onProgressUpdate = null,
        CancellationToken cancellationToken = default)
    {
        var errorMessage = string.Empty;

        try
        {
            onProgressUpdate?.Invoke("正在读取版本信息...");
            cancellationToken.ThrowIfCancellationRequested();

            var versionJsonPath = Path.Combine(config.GameDirectory, "versions", versionId, $"{versionId}.json");
            if (!File.Exists(versionJsonPath))
            {
                errorMessage = $"版本配置文件不存在: {versionJsonPath}";
                throw new FileNotFoundException(errorMessage);
            }

            var versionJson = await File.ReadAllTextAsync(versionJsonPath, cancellationToken).ConfigureAwait(false);
            var versionInfo = JsonSerializer.Deserialize<VersionInfo>(versionJson, CachedJsonOptions);

            if (versionInfo == null)
            {
                errorMessage = "无法解析版本配置文件";
                throw new Exception(errorMessage);
            }

            string actualMcVersion = versionId;
            if (!string.IsNullOrEmpty(versionInfo.InheritsFrom))
            {
                actualMcVersion = versionInfo.InheritsFrom;
                versionInfo = MergeInheritedVersion(config.GameDirectory, versionId, versionInfo);
            }

            onProgressUpdate?.Invoke("正在验证Java环境...");
            cancellationToken.ThrowIfCancellationRequested();

            var actualJavaPath = ResolveJavaPath(config, versionId, actualMcVersion);
            if (!File.Exists(actualJavaPath))
            {
                errorMessage = $"Java路径不存在: {actualJavaPath}";
                return new GameIntegrityResult { HasIssue = true, ErrorMessage = errorMessage };
            }

            onProgressUpdate?.Invoke("正在检查游戏主文件...");
            cancellationToken.ThrowIfCancellationRequested();

            bool isModLoader = versionInfo.MainClass?.Contains("forge", StringComparison.OrdinalIgnoreCase) == true ||
                               versionInfo.MainClass?.Contains("fabric", StringComparison.OrdinalIgnoreCase) == true ||
                               versionInfo.MainClass?.Contains("quilt", StringComparison.OrdinalIgnoreCase) == true;

            var clientJarPath = Path.Combine(config.GameDirectory, "versions", versionId, $"{versionId}.jar");
            if (!isModLoader && !File.Exists(clientJarPath))
            {
                errorMessage = $"游戏主文件不存在: {clientJarPath}\n请先下载游戏版本";
                throw new FileNotFoundException(errorMessage);
            }

            onProgressUpdate?.Invoke("正在检查游戏依赖库...");
            cancellationToken.ThrowIfCancellationRequested();

            var (missingRequired, missingOptional) = GetMissingLibraries(config.GameDirectory, versionInfo);

            if (missingRequired.Count > 0)
            {
                errorMessage = $"检测到 {missingRequired.Count} 个缺失或不完整的必需库文件";
                return new GameIntegrityResult
                {
                    HasIssue = true,
                    MissingLibraries = missingRequired,
                    MissingOptionalLibraries = missingOptional,
                    ErrorMessage = errorMessage
                };
            }

            onProgressUpdate?.Invoke("游戏完整性检查完成");
            return new GameIntegrityResult
            {
                HasIssue = false,
                MissingLibraries = missingRequired,
                MissingOptionalLibraries = missingOptional
            };
        }
        catch (OperationCanceledException)
        {
            errorMessage = "检查已取消";
            return new GameIntegrityResult { HasIssue = true, ErrorMessage = errorMessage };
        }
        catch (Exception ex)
        {
            if (string.IsNullOrEmpty(errorMessage))
            {
                errorMessage = ex.Message;
            }
            return new GameIntegrityResult { HasIssue = true, ErrorMessage = errorMessage };
        }
    }

    public static async Task<GameLaunchResult> LaunchGameAsync(
        string versionId,
        GameAccount account,
        LauncherConfig config,
        Action<string>? onProgressUpdate = null,
        Action<string>? onGameOutput = null,
        Action<int>? onGameExit = null,
        CancellationToken cancellationToken = default)
    {
        return await LaunchGameInternalAsync(versionId, account, config, null, 0, onProgressUpdate, onGameOutput, onGameExit, cancellationToken);
    }

    public static async Task<GameLaunchResult> LaunchAndConnectServerAsync(
        string versionId,
        GameAccount account,
        LauncherConfig config,
        string serverAddress,
        int serverPort = 25565,
        Action<string>? onProgressUpdate = null,
        Action<string>? onGameOutput = null,
        Action<int>? onGameExit = null,
        CancellationToken cancellationToken = default)
    {
        return await LaunchGameInternalAsync(versionId, account, config, serverAddress, serverPort, onProgressUpdate, onGameOutput, onGameExit, cancellationToken);
    }

    private static async Task<GameLaunchResult> LaunchGameInternalAsync(
        string versionId,
        GameAccount account,
        LauncherConfig config,
        string? serverAddress,
        int serverPort,
        Action<string>? onProgressUpdate,
        Action<string>? onGameOutput,
        Action<int>? onGameExit,
        CancellationToken cancellationToken)
    {
        var errorMessage = string.Empty;
        Process? process = null;
        DateTime? gameStartTime = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (account.Type == AccountType.Yggdrasil)
            {
                onProgressUpdate?.Invoke("正在检查外置登录文件...");
                if (!AuthlibInjectorService.IsAuthlibInjectorExists())
                {
                    errorMessage = "外置登录需要 authlib-injector.jar 文件\n请在账号管理中重新登录以自动下载";
                    throw new Exception(errorMessage);
                }

                onProgressUpdate?.Invoke("正在刷新外置登录令牌...");
                _ = await AccountService.Instance.RefreshYggdrasilAccountAsync(account.Id, onProgressUpdate, cancellationToken).ConfigureAwait(false);
            }
            else if (account.Type == AccountType.Microsoft && account.IsTokenExpired())
            {
                onProgressUpdate?.Invoke("正在刷新微软账号令牌...");

                using var refreshCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var refreshTask = Task.Run(async () =>
                    await AccountService.Instance.RefreshMicrosoftAccountAsync(account.Id, onProgressUpdate, refreshCts.Token));

                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                var completedTask = await Task.WhenAny(refreshTask, timeoutTask).ConfigureAwait(false);

                if (completedTask == timeoutTask)
                {
                    refreshCts.Cancel();
                    _ = refreshTask.ContinueWith(t => _ = t.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default);
                    errorMessage = "微软账号令牌刷新超时（30秒）\n请检查网络连接或重新登录";
                    throw new Exception(errorMessage);
                }

                var refreshSuccess = await refreshTask.ConfigureAwait(false);
                if (!refreshSuccess)
                {
                    errorMessage = "微软账号令牌已过期且刷新失败\n请重新登录微软账号";
                    throw new Exception(errorMessage);
                }
            }

            EnsureOldVersionIconsExist(config.GameDirectory);

            onProgressUpdate?.Invoke("正在读取游戏版本信息...");
            cancellationToken.ThrowIfCancellationRequested();

            var versionJsonPath = Path.Combine(config.GameDirectory, "versions", versionId, $"{versionId}.json");
            if (!File.Exists(versionJsonPath))
            {
                errorMessage = $"版本JSON文件不存在\n路径: {versionJsonPath}";
                throw new FileNotFoundException(errorMessage);
            }

            var versionJson = File.ReadAllText(versionJsonPath);
            var versionInfo = JsonSerializer.Deserialize<VersionInfo>(versionJson, CachedJsonOptions);

            if (versionInfo == null)
            {
                errorMessage = "无法解析版本JSON文件，文件格式可能不正确";
                throw new Exception(errorMessage);
            }

            string actualMcVersion = versionId;
            if (!string.IsNullOrEmpty(versionInfo.InheritsFrom))
            {
                actualMcVersion = versionInfo.InheritsFrom;
                versionInfo = MergeInheritedVersion(config.GameDirectory, versionId, versionInfo);
            }

            onProgressUpdate?.Invoke("正在验证Java环境...");
            cancellationToken.ThrowIfCancellationRequested();

            var actualJavaPath = ResolveJavaPath(config, versionId, actualMcVersion);
            if (!File.Exists(actualJavaPath))
            {
                errorMessage = $"Java可执行文件不存在\n路径: {actualJavaPath}";
                throw new FileNotFoundException(errorMessage);
            }

            if (string.IsNullOrEmpty(versionInfo.MainClass))
            {
                errorMessage = "版本JSON中缺少MainClass字段";
                throw new Exception(errorMessage);
            }

            bool isModLoader = versionInfo.MainClass?.Contains("forge", StringComparison.OrdinalIgnoreCase) == true ||
                               versionInfo.MainClass?.Contains("fabric", StringComparison.OrdinalIgnoreCase) == true ||
                               versionInfo.MainClass?.Contains("quilt", StringComparison.OrdinalIgnoreCase) == true;

            var versionDir = Path.Combine(config.GameDirectory, "versions", versionId);
            var nativesDir = Path.Combine(versionDir, "natives");
            Directory.CreateDirectory(nativesDir);

            onProgressUpdate?.Invoke("正在解压本地库文件...");
            cancellationToken.ThrowIfCancellationRequested();
            ExtractNatives(config.GameDirectory, versionInfo, nativesDir);

            onProgressUpdate?.Invoke("正在验证游戏客户端文件...");
            cancellationToken.ThrowIfCancellationRequested();

            var clientJar = Path.Combine(versionDir, $"{versionId}.jar");
            if (!isModLoader && !File.Exists(clientJar))
            {
                errorMessage = $"客户端JAR文件不存在\n路径: {clientJar}";
                throw new FileNotFoundException(errorMessage);
            }

            onProgressUpdate?.Invoke("正在检查游戏依赖库...");
            cancellationToken.ThrowIfCancellationRequested();

            var (missingRequired, missingOptional) = GetMissingLibraries(config.GameDirectory, versionInfo);

            if (missingRequired.Count > 0)
            {
                onProgressUpdate?.Invoke($"正在下载 {missingRequired.Count} 个缺失的库文件...");

                var (successCount, failedCount) = await LibraryDownloader.DownloadMissingLibrariesAsync(
                    config.GameDirectory,
                    versionId,
                    missingRequired,
                    (progress, current, total) => { onProgressUpdate?.Invoke(progress); },
                    cancellationToken).ConfigureAwait(false);

                if (failedCount > 0)
                {
                    errorMessage = "❌ 必需依赖库下载失败！";
                    return new GameLaunchResult { Success = false, ErrorMessage = errorMessage };
                }
            }

            onProgressUpdate?.Invoke("正在验证游戏资源...");
            cancellationToken.ThrowIfCancellationRequested();
            var assetsResult = await AssetsDownloadService.DownloadAndCheckAssetsAsync(
                config.GameDirectory,
                versionId,
                (p, total, msg, speed) => 
                {
                    onProgressUpdate?.Invoke($"{msg} ({p}%)|{p}");
                },
                cancellationToken).ConfigureAwait(false);

            if (!assetsResult.Success)
            {
                errorMessage = "❌ 游戏资源检查或下载失败！";
                return new GameLaunchResult { Success = false, ErrorMessage = errorMessage };
            }

            onProgressUpdate?.Invoke("正在准备启动参数...");
            cancellationToken.ThrowIfCancellationRequested();

            var arguments = BuildLaunchArguments(versionId, account, config, versionInfo, serverAddress, serverPort);

            var launchHook = new GameLaunchHookContext
            {
                VersionId = versionId,
                McVersion = actualMcVersion,
                GameDirectory = config.GameDirectory,
                JavaPath = actualJavaPath
            };
            await PluginContext.TriggerGameLaunchHooksAsync(GameLaunchPhase.BeforeLaunch, launchHook);
            if (launchHook.CancelLaunch)
            {
                errorMessage = "启动已被插件取消";
                return new GameLaunchResult { Success = false, ErrorMessage = errorMessage };
            }
            foreach (var arg in launchHook.ExtraJvmArguments)
            {
                if (!string.IsNullOrWhiteSpace(arg)) arguments += " " + QuoteArgument(arg);
            }
            foreach (var arg in launchHook.ExtraGameArguments)
            {
                if (!string.IsNullOrWhiteSpace(arg)) arguments += " " + QuoteArgument(arg);
            }

            onProgressUpdate?.Invoke("正在启动游戏进程...");
            cancellationToken.ThrowIfCancellationRequested();

            var workingDirectory = config.GetRunDirectory(versionId);

            var processInfo = new ProcessStartInfo
            {
                FileName = actualJavaPath,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            try
            {
                // 记录游戏开始时间
                gameStartTime = DateTime.Now;
                config.LastGameStartTime = gameStartTime;
                config.Save();

                process = new Process { StartInfo = processInfo };

                process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        onGameOutput?.Invoke(e.Data);
                    }
                };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        onGameOutput?.Invoke(e.Data);
                    }
                };

                if (!process.Start())
                {
                    errorMessage = "无法启动Java进程，请检查Java路径是否正确";
                    throw new Exception(errorMessage);
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (onGameExit != null)
                {
                    process.EnableRaisingEvents = true;
                    process.Exited += (_, _) =>
                    {
                        var exitCode = process.ExitCode;
                        // 计算游玩时长并保存
                        if (gameStartTime.HasValue)
                        {
                            var elapsed = (DateTime.Now - gameStartTime.Value).TotalSeconds;
                            config.TotalPlayTimeSeconds += (long)elapsed;
                            config.LastGameStartTime = null;
                            config.Save();
                        }
                        _ = FireGameExitHooksAsync(versionId, actualMcVersion, config.GameDirectory, actualJavaPath, exitCode);
                        onGameExit.Invoke(exitCode);
                    };
                }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);

                if (process.HasExited)
                {
                    errorMessage = $"游戏进程启动后立即退出\n退出代码: {process.ExitCode}\n请检查Debug输出窗口查看详细错误日志";
                    var exitCode = process.ExitCode;
                    // 即使立即退出，也记录实际运行时间
                    if (gameStartTime.HasValue)
                    {
                        var elapsed = (DateTime.Now - gameStartTime.Value).TotalSeconds;
                        if (elapsed > 1) // 只记录超过1秒的
                        {
                            config.TotalPlayTimeSeconds += (long)elapsed;
                            config.LastGameStartTime = null;
                            config.Save();
                        }
                    }
                    _ = FireGameExitHooksAsync(versionId, actualMcVersion, config.GameDirectory, actualJavaPath, exitCode);
                    process.Dispose();
                    onGameExit?.Invoke(exitCode);
                    return new GameLaunchResult { Success = false, ErrorMessage = errorMessage };
                }

                await PluginContext.TriggerGameLaunchHooksAsync(GameLaunchPhase.AfterLaunch, new GameLaunchHookContext
                {
                    VersionId = versionId,
                    McVersion = actualMcVersion,
                    GameDirectory = config.GameDirectory,
                    JavaPath = actualJavaPath
                });
                PluginContext.TriggerGlobalEvent(IPluginContext.EventNames.GameLaunched, versionId);

                onProgressUpdate?.Invoke("启动完成");
                return new GameLaunchResult { Success = true };
            }
            catch (OperationCanceledException)
            {
                TryKillGameProcess(process);
                process?.Dispose();
                if (gameStartTime.HasValue)
                {
                    var elapsed = (DateTime.Now - gameStartTime.Value).TotalSeconds;
                    if (elapsed > 5)
                    {
                        config.TotalPlayTimeSeconds += (long)elapsed;
                        config.LastGameStartTime = null;
                        config.Save();
                    }
                }
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            errorMessage = "启动已取消";
            return new GameLaunchResult { Success = false, ErrorMessage = errorMessage };
        }
        catch (Exception ex)
        {
            TryKillGameProcess(process);
            process?.Dispose();
            if (string.IsNullOrEmpty(errorMessage))
            {
                errorMessage = ex.Message;
            }
            return new GameLaunchResult { Success = false, ErrorMessage = errorMessage };
        }
    }

    /// <summary>
    /// 触发游戏退出/崩溃钩子与游戏关闭事件。退出码为 0 视为正常退出，否则视为崩溃。
    /// </summary>
    private static async Task FireGameExitHooksAsync(string versionId, string mcVersion, string gameDir, string javaPath, int exitCode)
    {
        var phase = exitCode == 0 ? GameLaunchPhase.OnExited : GameLaunchPhase.OnCrash;
        await PluginContext.TriggerGameLaunchHooksAsync(phase, new GameLaunchHookContext
        {
            VersionId = versionId,
            McVersion = mcVersion,
            GameDirectory = gameDir,
            JavaPath = javaPath,
            ExitCode = exitCode
        });
        PluginContext.TriggerGlobalEvent(IPluginContext.EventNames.GameClosed, exitCode);
    }

    private static void TryKillGameProcess(Process? process)
    {
        if (process == null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
        }
    }

    // ===== 以下为原有方法，完全保留（BuildLaunchArguments, GetLibraryPath, MergeInheritedVersion 等） =====
    // 由于篇幅限制，此处省略，请确保保留原文件中的所有其他方法。
    // 包括：ResolveJavaPath, BuildLaunchArguments, IsVeryOldForgeVersion, QuoteArgument,
    // ReplaceArgVariables, NeedsQuoting, ShouldSkipJvmArg, FixModuleArgument,
    // ShouldSkipGameArg, IsLibraryAllowedPublic, GetLibraryPathPublic, BuildLaunchScriptContent,
    // CheckVersionIntegrity, CompleteVersionFilesAsync, IsLibraryAllowed, GetMissingLibraries,
    // GetLibraryPath, GetOSName, VersionInfo, GameArguments, AssetIndexInfo, Library,
    // LibraryDownloads, Artifact, Rule, OsInfo, ExtractNatives, GetLibraryKey,
    // MergeInheritedVersion, EnsureOldVersionIconsExist, CreateMinimalTransparentPng,
    // GetShortPath, GetShortPathName

    // 下面仅保留原有方法的签名，实际使用时请保持原文件完整
    private static string ResolveJavaPath(LauncherConfig config, string versionId, string actualMcVersion) => throw new NotImplementedException();
    public static string BuildLaunchArguments(string versionId, GameAccount account, LauncherConfig config, VersionInfo versionInfo, string? serverAddress = null, int serverPort = 25565) => throw new NotImplementedException();
    private static bool IsVeryOldForgeVersion(string versionId) => throw new NotImplementedException();
    private static string QuoteArgument(string value) => throw new NotImplementedException();
    private static string ReplaceArgVariables(string arg, string versionId, string gameDir, string librariesDir, string nativesDir, string assetsDir, string classpath) => throw new NotImplementedException();
    private static bool NeedsQuoting(string value) => throw new NotImplementedException();
    private static bool ShouldSkipJvmArg(string arg) => throw new NotImplementedException();
    private static string FixModuleArgument(string arg, bool isModularNeoForge) => throw new NotImplementedException();
    private static bool ShouldSkipGameArg(string arg) => throw new NotImplementedException();
    public static bool IsLibraryAllowedPublic(Library lib) => throw new NotImplementedException();
    public static string GetLibraryPathPublic(string librariesDir, Library lib) => throw new NotImplementedException();
    public static string BuildLaunchScriptContent(string versionId, LauncherConfig config, GameAccount account) => throw new NotImplementedException();
    public static (int missingLibraries, int missingAssets) CheckVersionIntegrity(string gameDir, string versionId) => throw new NotImplementedException();
    public static async Task<(int libsDownloaded, int libsFailed, bool assetsOk)> CompleteVersionFilesAsync(string gameDir, string versionId, Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    private static bool IsLibraryAllowed(Library lib) => throw new NotImplementedException();
    private static (List<string> missingRequired, List<string> missingOptional) GetMissingLibraries(string gameDir, VersionInfo versionInfo) => throw new NotImplementedException();
    private static string GetLibraryPath(string librariesDir, Library lib) => throw new NotImplementedException();
    private static string GetOSName() => throw new NotImplementedException();
    public class VersionInfo { public string? MainClass { get; set; } public string? Assets { get; set; } public AssetIndexInfo? AssetIndex { get; set; } public Library[]? Libraries { get; set; } public GameArguments? Arguments { get; set; } public string? MinecraftArguments { get; set; } public string? InheritsFrom { get; set; } public string? VersionName { get; set; } }
    public class GameArguments { public List<object>? Game { get; set; } public List<object>? Jvm { get; set; } }
    public class AssetIndexInfo { public string? Id { get; set; } }
    public class Library { public string? Name { get; set; } public LibraryDownloads? Downloads { get; set; } public Rule[]? Rules { get; set; } public Dictionary<string, string>? Natives { get; set; } }
    public class LibraryDownloads { public Artifact? Artifact { get; set; } public Dictionary<string, Artifact>? Classifiers { get; set; } }
    public class Artifact { public string? Path { get; set; } public string? Url { get; set; } public long Size { get; set; } }
    public class Rule { public string? Action { get; set; } public OsInfo? Os { get; set; } }
    public class OsInfo { public string? Name { get; set; } }
    private static void ExtractNatives(string gameDir, VersionInfo versionInfo, string nativesDir) => throw new NotImplementedException();
    private static string GetLibraryKey(string? libraryName) => throw new NotImplementedException();
    private static VersionInfo MergeInheritedVersion(string gameDirectory, string childVersionId, VersionInfo childVersion) => throw new NotImplementedException();
    private static void EnsureOldVersionIconsExist(string gameDirectory) => throw new NotImplementedException();
    private static void CreateMinimalTransparentPng(string filePath) => throw new NotImplementedException();
    private static string GetShortPath(string longPath) => throw new NotImplementedException();
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static extern uint GetShortPathName(string lpszLongPath, StringBuilder lpszShortPath, int cchBuffer);
}

public sealed class GameIntegrityResult
{
    public bool HasIssue { get; init; }
    public List<string> MissingLibraries { get; init; } = [];
    public List<string> MissingOptionalLibraries { get; init; } = [];
    public string ErrorMessage { get; init; } = "";
}

public sealed class GameLaunchResult
{
    public bool Success { get; init; }
    public string ErrorMessage { get; init; } = "";
}