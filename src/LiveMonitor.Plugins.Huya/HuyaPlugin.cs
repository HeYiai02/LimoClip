using LiveMonitor.Core.Enums;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Platforms;

namespace LiveMonitor.Plugins.Huya;

/// <summary>
/// 虎牙平台插件入口：作为 V0.5-C 的内置插件随主程序发布，同时可被 manifest 热更新替换。
/// </summary>
public sealed class HuyaPlugin : IPlatformPlugin
{
    public PlatformType Platform => PlatformType.Huya;

    public string PluginId => "huya";

    public string Version => PluginVersionInfo.Resolve(typeof(HuyaPlugin));

    public IPlatformProvider CreateStreamProvider(HttpClient httpClient, string ytDlpPath)
        => new HuyaProvider(httpClient);

    public IDanmakuClient? CreateDanmakuClient(
        string roomId,
        string? authCookie = null,
        HttpClient? httpClient = null)
        => new HuyaDanmakuClient(roomId, authCookie, httpClient);
}
