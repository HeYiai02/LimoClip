using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;

namespace LiveMonitor.Platforms.TestPlugin;

/// <summary>
/// 供 PlatformPluginLoader / PluginHost 单测使用的最小平台插件实现。
/// 只依赖 LiveMonitor.Core / LiveMonitor.Platforms.Common（插件工程约束）。
/// </summary>
public sealed class TestPlatformPlugin : IPlatformPlugin
{
    public PlatformType Platform => PlatformType.YouTube;

    public string PluginId => "test-plugin";

    public string Version => "1.2.3";

    public IPlatformProvider CreateStreamProvider(HttpClient httpClient, string ytDlpPath)
        => new TestPlatformProvider(httpClient, ytDlpPath);

    public IDanmakuClient? CreateDanmakuClient(
        string roomId,
        string? authCookie = null,
        HttpClient? httpClient = null)
        => null;
}

internal sealed class TestPlatformProvider : IPlatformProvider
{
    public TestPlatformProvider(HttpClient httpClient, string ytDlpPath)
    {
        HttpClient = httpClient;
        YtDlpPath = ytDlpPath;
    }

    public HttpClient HttpClient { get; }

    public string YtDlpPath { get; }

    public PlatformType Platform => PlatformType.YouTube;

    public bool CanHandle(string roomUrl) => roomUrl is not null && roomUrl.Contains("test-", StringComparison.OrdinalIgnoreCase);

    public string ExtractRoomId(string roomUrl) => "room-" + (roomUrl ?? string.Empty).Length.ToString();

    public Task<RoomStatus> GetRoomStatusAsync(
        string roomId,
        string? cookie = null,
        CancellationToken ct = default)
        => Task.FromResult(RoomStatus.Offline(roomId, PlatformType.YouTube));
}
