using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ClothingRepacker.Gui.Services;
using ClothingRepacker.Gui.ViewModels;
using ClothingRepacker.Gui.Views;

namespace ClothingRepacker.Gui;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settingsStore = new RecentSettingsStore();
            var workflow = new WorkflowRunner(new RepackerServiceFactory());
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(workflow, settingsStore)
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
