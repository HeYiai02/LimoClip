using LiveMonitor.Core.Entities;
using LiveMonitor.Core.Enums;
using LiveMonitor.Desktop.Services;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

/// <summary>
/// ViewModel 基线测试的共享脚手架：为每个测试创建独立临时目录与 SQLite 数据库，
/// 设置并恢复 <c>LIVEMONITOR_DB</c>，避免测试之间串库；同时统一释放被测 ViewModel，
/// 让 Dispatcher 计时器与录制引擎后台循环不会残留到后续测试。
/// </summary>
internal static class ViewModelTestHarness
{
    public const string DatabaseEnvironmentVariable = "LIVEMONITOR_DB";

    /// <summary>单个测试的隔离环境：服务容器 + 该测试专属的临时目录与数据库路径。</summary>
    public sealed class ViewModelTestContext
    {
        public ViewModelTestContext(AppServices services, string tempDirectory, string databasePath)
        {
            Services = services;
            TempDirectory = tempDirectory;
            DatabasePath = databasePath;
        }

        public AppServices Services { get; }

        public string TempDirectory { get; }

        public string DatabasePath { get; }
    }

    public static Task RunRecordingCenterAsync(
        string className,
        Func<ViewModelTestContext, RecordingCenterViewModel, Task> body) =>
        RunAsync(className, async context =>
        {
            var center = CreateRecordingCenter(context.Services);
            try
            {
                await body(context, center);
            }
            finally
            {
                TryDispose(center);
            }
        });

    public static Task RunMainWindowAsync(
        string className,
        Func<ViewModelTestContext, MainWindowViewModel, Task> body) =>
        RunMainWindowAsync(className, arrange: null, body);

    /// <param name="arrange">在创建并初始化 <see cref="MainWindowViewModel"/> 之前执行（用于预置主库数据）。</param>
    public static Task RunMainWindowAsync(
        string className,
        Func<ViewModelTestContext, Task>? arrange,
        Func<ViewModelTestContext, MainWindowViewModel, Task> body) =>
        RunAsync(className, async context =>
        {
            if (arrange is not null)
            {
                await arrange(context);
            }

            var viewModel = new MainWindowViewModel(context.Services);
            try
            {
                await viewModel.InitializeAsync();
                await body(context, viewModel);
            }
            finally
            {
                TryDispose(viewModel);
            }
        });

    public static RecordingCenterViewModel CreateRecordingCenter(AppServices services) =>
        new(
            services.Rooms,
            services.RecordingTargets,
            services.RecordingSessions,
            services.Settings,
            services.Recordings);

    /// <summary>直接向主库写入一个直播间（默认自定义清流，初始化时的开播轮询不会访问网络）。</summary>
    public static async Task<LiveRoom> AddRoomAsync(
        ViewModelTestContext context,
        int displayNumber,
        string alias,
        string? groupTag = null,
        string? roomId = null,
        PlatformType platform = PlatformType.CustomStream)
    {
        var resolvedRoomId = string.IsNullOrWhiteSpace(roomId) ? $"cs_test_{Guid.NewGuid():N}" : roomId;
        var room = new LiveRoom
        {
            Id = Guid.NewGuid(),
            DisplayNumber = displayNumber,
            Platform = platform,
            RoomId = resolvedRoomId,
            RawUrl = $"https://example.com/live/{resolvedRoomId}.flv",
            Alias = alias,
            GroupTag = string.IsNullOrWhiteSpace(groupTag) ? null : groupTag.Trim(),
            CreatedAt = DateTime.UtcNow
        };
        await context.Services.Rooms.AddAsync(room);
        return room;
    }

    /// <summary>
    /// 轮询等待异步续体完成（用于 fire-and-forget 的方案切换等路径）；最多等待 5 秒，
    /// 超时直接失败并给出明确描述，避免依赖固定 sleep。
    /// </summary>
    public static async Task WaitUntilAsync(Func<bool> predicate, string description)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(predicate(), $"等待条件超时（5s）：{description}");
    }

    private static async Task RunAsync(string className, Func<ViewModelTestContext, Task> body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "LiveMonitor.Desktop.Tests",
            className,
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(tempDirectory, "livemonitor.db");
        var previousDatabase = Environment.GetEnvironmentVariable(DatabaseEnvironmentVariable);
        try
        {
            Directory.CreateDirectory(tempDirectory);
            Environment.SetEnvironmentVariable(DatabaseEnvironmentVariable, databasePath);
            var services = await AppServices.CreateAsync();
            await body(new ViewModelTestContext(services, tempDirectory, databasePath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(DatabaseEnvironmentVariable, previousDatabase);
            TryDeleteDirectory(tempDirectory);
        }
    }

    private static void TryDispose(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch (Exception)
        {
            // 清理阶段尽力而为：Dispose 抛错不应该掩盖测试本身的断言结果。
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // SQLite 连接池可能仍持有文件句柄；临时目录留在系统 temp 下不影响测试结果。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
