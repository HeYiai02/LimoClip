using Avalonia.Input;
using LiveMonitor.Desktop.Controls;
using Xunit;

namespace LiveMonitor.Desktop.Tests.Controls;

/// <summary>
/// 自由画布房间拖拽负载格式的守卫：Avalonia 11.3 的 DataFormat 对 application 标识符有格式要求，
/// 非法标识符会在创建时抛异常，因此用测试锁住格式与拖放两端的读写约定。
/// </summary>
public sealed class FreeformDragFormatTests
{
    [Fact]
    public void RoomIdFormat_uses_application_scope()
    {
        Assert.Equal(DataFormatKind.Application, FreeformWorkspaceCanvas.RoomIdFormat.Kind);
        Assert.Equal("limoclip.room-id", FreeformWorkspaceCanvas.RoomIdFormat.Identifier);
    }

    [Fact]
    public void RoomIdFormat_round_trips_through_data_transfer()
    {
        var roomId = Guid.NewGuid();
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(FreeformWorkspaceCanvas.RoomIdFormat, roomId.ToString()));

        IDataTransfer data = transfer;
        Assert.True(data.Contains(FreeformWorkspaceCanvas.RoomIdFormat));
        Assert.Equal(roomId.ToString(), data.TryGetValue(FreeformWorkspaceCanvas.RoomIdFormat));
    }

    [Fact]
    public void Other_formats_are_not_mistaken_for_room_ids()
    {
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText("csgo-room"));

        IDataTransfer data = transfer;
        Assert.False(data.Contains(FreeformWorkspaceCanvas.RoomIdFormat));
        Assert.Null(data.TryGetValue(FreeformWorkspaceCanvas.RoomIdFormat));
    }
}
