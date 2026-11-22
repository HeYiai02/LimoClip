using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using LiveMonitor.Desktop.Services;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

public sealed class LayoutTrackingTests
{
    [Fact]
    public async Task ProportionChange_AfterPointerArm_MarksCustomLayout()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"livemonitor-{Guid.NewGuid():N}.db");
        var previous = Environment.GetEnvironmentVariable("LIVEMONITOR_DB");
        try
        {
            Environment.SetEnvironmentVariable("LIVEMONITOR_DB", databasePath);
            var services = await AppServices.CreateAsync();
            var viewModel = new MainWindowViewModel(services);
            await viewModel.InitializeAsync();

            var outer = (ProportionalDock)viewModel.Layout!.VisibleDockables![0];
            var row = (ProportionalDock)outer.VisibleDockables![0];
            var pane = row.VisibleDockables!.OfType<ToolDock>().First();

            viewModel.BeginCustomLayoutTracking();
            pane.Proportion = 0.7;

            Assert.True(viewModel.IsCustomLayout);
            Assert.Equal("自定义比例", viewModel.LayoutModeText);
            Assert.True(viewModel.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIVEMONITOR_DB", previous);
            // SQLite pooling may still hold the file; temp DB cleanup is best-effort.
            try
            {
                if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
