using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;

namespace DataBaseExporter.Gui;

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new StyleInclude(new Uri("avares://DataBaseExporter.Gui/Styles"))
        {
            Source = new Uri("avares://Avalonia.Themes.Fluent/FluentTheme.xaml")
        });
        RequestedThemeVariant = ThemeVariant.Light;
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
