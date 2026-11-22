using System;
using System.IO;
using Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Dock.Serializer.SystemTextJson;

namespace DockSpike;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var rows = GetEnvInt("DOCK_ROWS", 2);
        var cols = GetEnvInt("DOCK_COLS", 2);
        var factory = new GridFactory();
        var layout = factory.CreateGrid(rows, cols);
        factory.InitLayout(layout);
        Dock.Layout = layout;

        try
        {
            var modelOnly = factory.CreateGrid(rows, cols);
            ClearContexts(modelOnly);

            var serializer = new DockSerializer();
            var json = serializer.Serialize<IRootDock>(modelOnly);
            var path = Path.Combine(AppContext.BaseDirectory, "dock-layout.json");
            File.WriteAllText(path, json);
            var restored = serializer.Deserialize<IRootDock>(json);
            Console.WriteLine($"[ser] json={json.Length} restored={restored is not null} visible={restored?.VisibleDockables?.Count}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ser] FAIL {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static int GetEnvInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    private static void ClearContexts(IDockable dockable)
    {
        if (dockable is Tool tool)
        {
            tool.Context = null;
        }

        if (dockable is IDock dock)
        {
            foreach (var child in dock.VisibleDockables ?? Array.Empty<IDockable>())
            {
                ClearContexts(child);
            }
        }
    }
}
