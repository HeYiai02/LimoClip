namespace LiveMonitor.Core.Interfaces;

/// <summary>
/// 全局键值配置。字符串值的解析与默认值由调用方负责。
/// </summary>
public interface IAppSettingsRepository
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default);

    /// <summary>不存在则插入，存在则覆盖。</summary>
    Task SetAsync(string key, string value, CancellationToken ct = default);

    Task DeleteAsync(string key, CancellationToken ct = default);
}
