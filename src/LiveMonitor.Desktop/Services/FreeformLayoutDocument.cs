using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveMonitor.Desktop.Services;

/// <summary>
/// 自由画布上的一个直播间槽位。坐标全部为归一化值，持久化时也只写归一化值。
/// </summary>
public sealed class FreeformTilePlacement
{
    /// <summary>画布内唯一槽位 ID（如 pane-1），与旧版 LayoutSlots.PaneId 兼容。</summary>
    public string PaneId { get; set; } = string.Empty;

    /// <summary>绑定的直播间 ID；空槽位为 null。</summary>
    public Guid? LiveRoomId { get; set; }

    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; } = 1.0;

    public double Height { get; set; } = 1.0;

    /// <summary>普通层 z（1~999）；置顶层另加 1000 基准。</summary>
    public int ZIndex { get; set; } = 1;

    /// <summary>置顶为画中画：永远浮在普通层之上，点击底层大画面不会把它盖住。</summary>
    public bool IsPinned { get; set; }

    /// <summary>锁定位置：隐藏缩放热区、禁止拖动，但音量/画质/弹幕按钮照常可用。</summary>
    public bool IsLocked { get; set; }

    public NormalizedRect ToRect() => new(Left, Top, Width, Height);

    public void SetRect(NormalizedRect rect)
    {
        Left = rect.Left;
        Top = rect.Top;
        Width = rect.Width;
        Height = rect.Height;
    }

    /// <summary>实际渲染层级：置顶层固定在普通层之上。</summary>
    public int EffectiveZIndex => IsPinned ? 1000 + ZIndex : ZIndex;

    public FreeformTilePlacement Clone() => new()
    {
        PaneId = PaneId,
        LiveRoomId = LiveRoomId,
        Left = Left,
        Top = Top,
        Width = Width,
        Height = Height,
        ZIndex = ZIndex,
        IsPinned = IsPinned,
        IsLocked = IsLocked
    };

    public static FreeformTilePlacement Create(
        string paneId,
        NormalizedRect rect,
        Guid? liveRoomId = null,
        int zIndex = 1,
        bool isPinned = false,
        bool isLocked = false)
    {
        var placement = new FreeformTilePlacement
        {
            PaneId = paneId,
            LiveRoomId = liveRoomId,
            ZIndex = zIndex,
            IsPinned = isPinned,
            IsLocked = isLocked
        };
        placement.SetRect(rect);
        return placement;
    }
}

/// <summary>
/// 自由画布方案文档（JSON v2）。复用 LayoutProfiles.DockLayoutJson 列，通过
/// version=2 + mode=freeform 与旧版 Dock 平铺 JSON 区分，实现零数据库迁移。
/// </summary>
public sealed class FreeformLayoutDocument
{
    public const int CurrentVersion = 2;
    public const string FreeformMode = "freeform";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public int Version { get; set; } = CurrentVersion;

    public string Mode { get; set; } = FreeformMode;

    public List<FreeformTilePlacement> Tiles { get; set; } = new();

    public string Serialize() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>判断一段 DockLayoutJson 是否是自由画布 v2 文档（旧 Dock JSON / 空串返回 false）。</summary>
    public static bool LooksLikeFreeform(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number)
            {
                return false;
            }

            if (!root.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            return version.GetInt32() >= CurrentVersion &&
                   string.Equals(mode.GetString(), FreeformMode, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 解析自由画布文档；非 v2 格式返回 null（调用方应回退到预设生成 / 旧版迁移）。
    /// 解析时统一做边界与最小尺寸约束，坏数据不会把窗格排到画布外。
    /// </summary>
    public static FreeformLayoutDocument? TryParse(string? json)
    {
        if (!LooksLikeFreeform(json))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<FreeformLayoutDocument>(json!, SerializerOptions);
            if (parsed is null)
            {
                return null;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var tiles = new List<FreeformTilePlacement>(parsed.Tiles.Count);
            foreach (var tile in parsed.Tiles)
            {
                if (tile is null || string.IsNullOrWhiteSpace(tile.PaneId) || !seen.Add(tile.PaneId))
                {
                    continue;
                }

                var clamped = tile.ToRect().ClampToCanvas();
                tile.SetRect(clamped);
                tile.ZIndex = Math.Max(1, tile.ZIndex);
                tiles.Add(tile);
            }

            parsed.Version = CurrentVersion;
            parsed.Mode = FreeformMode;
            parsed.Tiles = tiles;
            return parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static FreeformLayoutDocument FromPlacements(IEnumerable<FreeformTilePlacement> placements) => new()
    {
        Version = CurrentVersion,
        Mode = FreeformMode,
        Tiles = placements.Select(placement => placement.Clone()).ToList()
    };
}
