using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;

namespace DockSpike;

public sealed class GridFactory : Factory
{
    public override IRootDock CreateLayout() => CreateGrid(2, 2);

    public IRootDock CreateGrid(int rows, int cols)
    {
        var root = CreateRootDock();
        root.Id = "Root";

        var outer = new ProportionalDock
        {
            Id = "Grid",
            Orientation = Dock.Model.Core.Orientation.Vertical,
            IsCollapsable = false,
            Proportion = 1
        };

        var rowDocks = new List<IDockable>();
        var paneNumber = 1;
        for (var r = 0; r < rows; r++)
        {
            var row = new ProportionalDock
            {
                Id = $"Row{r}",
                Orientation = Dock.Model.Core.Orientation.Horizontal,
                IsCollapsable = false,
                Proportion = 1
            };

            var panes = new List<IDockable>();
            for (var c = 0; c < cols; c++)
            {
                panes.Add(Pane(r, c, paneNumber++));
            }

            row.VisibleDockables = CreateList<IDockable>(panes.ToArray());
            row.ActiveDockable = row.VisibleDockables[0];
            rowDocks.Add(row);
        }

        outer.VisibleDockables = CreateList<IDockable>(rowDocks.ToArray());
        outer.ActiveDockable = outer.VisibleDockables[0];

        root.VisibleDockables = CreateList<IDockable>(outer);
        root.DefaultDockable = outer;
        return root;
    }

    private ToolDock Pane(int row, int col, int number)
    {
        var label = $"{row + 1}-{col + 1}";
        var tool = new Tool
        {
            Id = $"tool-{label}",
            Title = $"Pane {label}",
            CanClose = false,
            CanFloat = false,
            Context = new Grid
            {
                Background = new SolidColorBrush(Color.Parse("#161b24")),
                Children =
                {
                    new TextBlock
                    {
                        Text = $"PANE {label}",
                        Foreground = Brushes.White,
                        FontSize = 20,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                    },
                    new Border
                    {
                        Background = new SolidColorBrush(Color.Parse("#cc0b0e14")),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(6, 2),
                        Margin = new Thickness(6),
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                        Child = new TextBlock
                        {
                            Text = $"#{number} 晚风{label}",
                            Foreground = Brushes.White,
                            FontSize = 10
                        }
                    }
                }
            }
        };
        return new ToolDock
        {
            Id = $"dock-{label}",
            VisibleDockables = CreateList<IDockable>(tool),
            ActiveDockable = tool,
            Proportion = 1,
            CanClose = false,
            CanFloat = false
        };
    }
}
