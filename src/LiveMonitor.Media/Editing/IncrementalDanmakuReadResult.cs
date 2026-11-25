using LiveMonitor.Core.Models;

namespace LiveMonitor.Media.Editing;

/// <summary>
/// 增量读取伴随弹幕的结果：<see cref="NewItems"/> 是本次从 <c>fromByteOffset</c> 起新读到的
/// 完整行（meta 行与坏行已跳过），<see cref="NextByteOffset"/> 是下一次续读应该使用的字节偏移
/// （只推进到最后一个完整行之后；文件尾部的半截行不会被消费，等写完换行后再读）。
/// </summary>
/// <param name="NewItems">本次新增的完整弹幕行。</param>
/// <param name="NextByteOffset">下一次续读的字节偏移。</param>
public sealed record IncrementalDanmakuReadResult(
    IReadOnlyList<RecordedDanmakuItem> NewItems,
    long NextByteOffset);
