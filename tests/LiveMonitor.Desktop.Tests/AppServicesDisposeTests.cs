using LiveMonitor.Desktop.Services;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

/// <summary>
/// 组合根释放契约：<see cref="AppServices.Dispose"/> 幂等（重复调用安全），且在停妥后台服务
/// 后清空 SQLite 连接池释放数据库文件句柄；<see cref="MainWindowViewModel.Dispose"/> 只做视图态
/// 退订并把基础设施销毁统一交给组合根。这些用例不需要启动 UI。
/// </summary>
[Collection(AppServicesDatabaseCollection.Name)]
public sealed class AppServicesDisposeTests
{
    [Fact]
    public async Task Dispose_IsIdempotent_AndReleasesDatabaseFileHandles()
    {
        var previous = Environment.GetEnvironmentVariable(ViewModelTestHarness.DatabaseEnvironmentVariable);
        var tempDirectory = CreateTempDirectory();
        var databasePath = SetDatabasePath(tempDirectory);
        try
        {
            var services = await AppServices.CreateAsync();
            Assert.True(File.Exists(databasePath), $"数据库文件未创建：{databasePath}");

            services.Dispose();
            // 重复 Dispose 必须安全：窗口关闭路径与兜底路径可能各调用一次。
            services.Dispose();

            // 连接池已清空，SQLite 不再持有库文件句柄，Windows 上可以直接删除数据库文件。
            File.Delete(databasePath);
            Assert.False(File.Exists(databasePath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ViewModelTestHarness.DatabaseEnvironmentVariable, previous);
            TryDeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public async Task MainWindowViewModelDispose_CascadesToAppServicesAndReleasesDatabase()
    {
        var previous = Environment.GetEnvironmentVariable(ViewModelTestHarness.DatabaseEnvironmentVariable);
        var tempDirectory = CreateTempDirectory();
        var databasePath = SetDatabasePath(tempDirectory);
        try
        {
            var services = await AppServices.CreateAsync();
            var viewModel = new MainWindowViewModel(services);
            try
            {
                await viewModel.InitializeAsync();

                viewModel.Dispose();
                // ViewModel 的幂等守卫与 AppServices 的幂等守卫叠加，重复关闭仍安全。
                viewModel.Dispose();

                // ViewModel.Dispose 已级联 AppServices.Dispose（含 ClearAllPools），库文件可释放。
                File.Delete(databasePath);
                Assert.False(File.Exists(databasePath));
            }
            finally
            {
                viewModel.Dispose();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(ViewModelTestHarness.DatabaseEnvironmentVariable, previous);
            TryDeleteDirectory(tempDirectory);
        }
    }

    private static string CreateTempDirectory()
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "LiveMonitor.Desktop.Tests",
            nameof(AppServicesDisposeTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        return tempDirectory;
    }

    private static string SetDatabasePath(string tempDirectory)
    {
        var databasePath = Path.Combine(tempDirectory, "livemonitor.db");
        Environment.SetEnvironmentVariable(
            ViewModelTestHarness.DatabaseEnvironmentVariable,
            databasePath);
        return databasePath;
    }

    private static void TryDeleteDirectory(string tempDirectory)
    {
        try
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 测试失败路径下 SQLite 可能仍持有句柄；临时目录残留不影响断言结果。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
