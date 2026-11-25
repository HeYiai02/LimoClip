using Xunit;

namespace LiveMonitor.Desktop.Tests;

/// <summary>
/// 这些用例通过进程级环境变量 LIVEMONITOR_DB 指向各自的临时库，
/// 并行执行会互相串库（表现为 LiveRooms.DisplayNumber 唯一约束冲突），因此统一串行。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppServicesDatabaseCollection
{
    public const string Name = "AppServicesDatabase";
}
