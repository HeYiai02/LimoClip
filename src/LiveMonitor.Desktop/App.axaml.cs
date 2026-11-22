using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LiveMonitor.Desktop.Player;
using LiveMonitor.Desktop.Views;

namespace LiveMonitor.Desktop;

public partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var smoke = string.Equals(
                Environment.GetEnvironmentVariable("LIVEMONITOR_PLAYER_SMOKE"),
                "1",
                StringComparison.Ordinal);
            desktop.MainWindow = smoke ? new PlayerSmokeWindow() : new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

