namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 直播间主库（<see cref="RoomCatalogViewModel"/>）的校验消息与操作状态文案唯一来源。
/// 仅承载用户可见文案；异常类型与参数名仍由调用点决定。
/// </summary>
internal static class RoomCatalogStatusText
{
    /// <summary>添加房间时链接/房间号为空。</summary>
    public const string MissingUrl = "请粘贴直播间链接或房间号。";

    /// <summary>自定义清流地址不是受支持的绝对地址。</summary>
    public const string InvalidCustomStreamUrl = "自定义清流需要 http(s) / rtmp(s) / rtsp(s) / srt 或 mms 绝对地址。";

    /// <summary>自定义清流 Provider 未注册。</summary>
    public const string CustomProviderMissing = "自定义清流 Provider 未注册，无法添加。";

    /// <summary>主库中已存在同一房间。</summary>
    public const string DuplicateRoom = "这个直播间已经在主库中，不能重复添加。";

    /// <summary>编辑房间时显示名称被清空。</summary>
    public const string AliasRequired = "显示名称不能为空。";

    /// <summary>编辑房间时序号非法。</summary>
    public const string DisplayNumberOutOfRange = "序号必须大于 0。";

    /// <summary>编辑/删除的目标房间已不存在。</summary>
    public const string RoomNotFound = "直播间不存在。";

    /// <summary>编辑房间时清流地址非法。</summary>
    public const string InvalidStreamUrl = "清流地址必须是 http(s) / rtmp(s) / rtsp(s) / srt 或 mms 绝对地址。";

    /// <summary>添加成功后的状态栏提示。</summary>
    public static string Added(string alias) => $"已添加 {alias}";

    /// <summary>从主库删除成功后的状态栏提示。</summary>
    public static string Removed(string alias) => $"已从主库删除 {alias}";
}
