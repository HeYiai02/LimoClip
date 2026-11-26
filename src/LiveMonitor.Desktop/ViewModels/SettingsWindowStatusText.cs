namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 设置页（SettingsWindow）宿主侧的导航分区标题与账号状态栏文案（V0.7.5）。
/// 账号登录/清除的 WebView2 交互与 Cookie 轮询留在 View，本类只负责显示文本。
/// </summary>
internal static class SettingsWindowStatusText
{
    /// <summary>左侧设置分组：Key 对应导航按钮 Tag，标题/副标题显示在右侧页头。</summary>
    public static readonly (string Key, string Title, string Subtitle)[] Pages =
    {
        ("account", "账号与平台", "登录态只保存在本机 DPAPI 加密的 PlatformCredentials 中。"),
        ("danmaku", "弹幕与过滤", "屏蔽词、独轮车与监控画面弹幕渲染开关都集中在这里。"),
        ("asr", "语音识别（ASR）", "为剪辑台生成逐字稿；API Key 使用 DPAPI 加密保存。"),
        ("highlight", "高能事件检测", "按短窗弹幕密度与基线倍率自动记录打点。"),
        ("updates", "组件与更新", "yt-dlp 与平台插件的版本检查、下载与验签。"),
    };

    public const string Unbound = "未绑定";

    public const string UnboundExpired = "未绑定（上次登录已过期）";

    public const string Bound = "已绑定（Cookie 已加密保存）";

    public const string ClearButton = "清除";

    public const string LoginWindowClosedWithoutCookie = "登录窗已关闭，未捕获到登录 Cookie。";

    public static string LoginButton(string displayName) => $"登录 {displayName}";

    public static string ReadSettingsFailed(string message) => $"读取设置失败：{message}";

    public static string OpeningLoginWindow(string displayName) => $"正在打开 {displayName} 登录窗…";

    public static string LoginSaved(string displayName) => $"{displayName} 登录态已保存（DPAPI）。";

    public static string LoginFailed(string message) => $"登录失败：{message}";

    public static string Cleared(string displayName) => $"{displayName} 登录态已清除。";

    public static string ClearFailed(string message) => $"清除失败：{message}";
}
