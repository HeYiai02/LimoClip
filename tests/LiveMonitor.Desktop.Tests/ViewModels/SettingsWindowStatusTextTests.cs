using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests.ViewModels;

public sealed class SettingsWindowStatusTextTests
{
    [Fact]
    public void Pages_keep_keys_titles_and_subtitles()
    {
        Assert.Equal(5, SettingsWindowStatusText.Pages.Length);

        Assert.Equal(("account", "账号与平台"), (SettingsWindowStatusText.Pages[0].Key, SettingsWindowStatusText.Pages[0].Title));
        Assert.Equal("登录态只保存在本机 DPAPI 加密的 PlatformCredentials 中。", SettingsWindowStatusText.Pages[0].Subtitle);

        Assert.Equal(("danmaku", "弹幕与过滤"), (SettingsWindowStatusText.Pages[1].Key, SettingsWindowStatusText.Pages[1].Title));
        Assert.Contains("独轮车", SettingsWindowStatusText.Pages[1].Subtitle);

        Assert.Equal(("asr", "语音识别（ASR）"), (SettingsWindowStatusText.Pages[2].Key, SettingsWindowStatusText.Pages[2].Title));
        Assert.Contains("DPAPI", SettingsWindowStatusText.Pages[2].Subtitle);

        Assert.Equal(("highlight", "高能事件检测"), (SettingsWindowStatusText.Pages[3].Key, SettingsWindowStatusText.Pages[3].Title));

        Assert.Equal(("updates", "组件与更新"), (SettingsWindowStatusText.Pages[4].Key, SettingsWindowStatusText.Pages[4].Title));
        Assert.Contains("验签", SettingsWindowStatusText.Pages[4].Subtitle);
    }

    [Fact]
    public void Account_states_keep_original_copy()
    {
        Assert.Equal("未绑定", SettingsWindowStatusText.Unbound);
        Assert.Equal("未绑定（上次登录已过期）", SettingsWindowStatusText.UnboundExpired);
        Assert.Equal("已绑定（Cookie 已加密保存）", SettingsWindowStatusText.Bound);
        Assert.Equal("清除", SettingsWindowStatusText.ClearButton);
        Assert.Equal("登录窗已关闭，未捕获到登录 Cookie。", SettingsWindowStatusText.LoginWindowClosedWithoutCookie);
    }

    [Fact]
    public void LoginButton_prefixes_display_name()
        => Assert.Equal("登录 哔哩哔哩", SettingsWindowStatusText.LoginButton("哔哩哔哩"));

    [Fact]
    public void Account_bar_messages_prefix_display_name_or_reason()
    {
        Assert.Equal("读取设置失败：磁盘错误", SettingsWindowStatusText.ReadSettingsFailed("磁盘错误"));
        Assert.Equal("正在打开 斗鱼 登录窗…", SettingsWindowStatusText.OpeningLoginWindow("斗鱼"));
        Assert.Equal("斗鱼 登录态已保存（DPAPI）。", SettingsWindowStatusText.LoginSaved("斗鱼"));
        Assert.Equal("登录失败：超时", SettingsWindowStatusText.LoginFailed("超时"));
        Assert.Equal("虎牙 登录态已清除。", SettingsWindowStatusText.Cleared("虎牙"));
        Assert.Equal("清除失败：无权限", SettingsWindowStatusText.ClearFailed("无权限"));
    }
}
