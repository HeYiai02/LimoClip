using LiveMonitor.Media.Editing;

namespace LiveMonitor.Desktop.ViewModels;

/// <summary>
/// 增量读取仍被写入的伴随弹幕 .jsonl：生产实现直接转发 <see cref="DanmakuJsonlReader"/>，
/// 单元测试注入可控结果，从而在无文件系统依赖下断言偏移推进与追加行为。
/// </summary>
internal interface IIncrementalDanmakuReader
{
    Task<IncrementalDanmakuReadResult> ReadIncrementalAsync(
        string jsonlPath,
        long fromByteOffset,
        CancellationToken ct);
}

/// <summary>把静态 <see cref="DanmakuJsonlReader.ReadIncrementalAsync(string,long,CancellationToken)"/> 适配为可注入接口。</summary>
internal sealed class DanmakuJsonlReaderAdapter : IIncrementalDanmakuReader
{
    public Task<IncrementalDanmakuReadResult> ReadIncrementalAsync(
        string jsonlPath,
        long fromByteOffset,
        CancellationToken ct)
        => DanmakuJsonlReader.ReadIncrementalAsync(jsonlPath, fromByteOffset, ct);
}
