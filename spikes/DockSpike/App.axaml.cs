using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Dock.Avalonia.Controls;

namespace DockSpike;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        var hideChrome = new Style(x => x.OfType<ToolChromeControl>());
        hideChrome.Setters.Add(new Setter(Visual.IsVisibleProperty, false));
        Styles.Add(hideChrome);

        var hideTabs = new Style(x => x.OfType<ToolTabStrip>());
        hideTabs.Setters.Add(new Setter(Visual.IsVisibleProperty, false));
        Styles.Add(hideTabs);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
