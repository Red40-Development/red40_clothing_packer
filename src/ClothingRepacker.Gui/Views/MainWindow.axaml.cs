using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ClothingRepacker.Gui.ViewModels;

namespace ClothingRepacker.Gui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, DragOver);
        AddHandler(DragDrop.DropEvent, Drop);
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void BrowseResources_Click(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Select clothing resources folder");
        if (path is not null)
        {
            ViewModel?.SelectResourcesFolder(path);
        }
    }

    private async void BrowseOutput_Click(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Select preview output folder");
        if (path is not null && ViewModel is { } vm)
        {
            vm.OutputPath = path;
        }
    }

    private async void BrowseBackup_Click(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Select backup root folder");
        if (path is not null && ViewModel is { } vm)
        {
            vm.BackupRoot = path;
        }
    }

    private async void BrowsePlan_Click(object? sender, RoutedEventArgs e)
    {
        var path = await SaveFileAsync("Save plan JSON", "plan.json");
        if (path is not null && ViewModel is { } vm)
        {
            vm.PlanPath = path;
        }
    }

    private async void BrowseRestoreManifest_Click(object? sender, RoutedEventArgs e)
    {
        var path = await OpenFileAsync("Select backup manifest", "JSON files", ["json"]);
        if (path is not null && ViewModel is { } vm)
        {
            vm.RestoreManifestPath = path;
        }
    }

    private async void Apply_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || !vm.CanApply())
        {
            return;
        }

        var confirmed = await ConfirmApplyAsync(vm);
        if (confirmed)
        {
            await vm.ApplyAsync();
        }
    }

    private void DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private async void Drop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File) || ViewModel is null)
        {
            return;
        }

        var files = e.DataTransfer.TryGetFiles();
        if (files is null)
        {
            return;
        }

        foreach (var item in files)
        {
            if (item is IStorageFolder folder)
            {
                ViewModel.SelectResourcesFolder(folder.Path.LocalPath);
                return;
            }

            var localPath = item.Path.LocalPath;
            if (Directory.Exists(localPath))
            {
                ViewModel.SelectResourcesFolder(localPath);
                return;
            }
        }

        await ShowMessageAsync("Folder required", "Drop a clothing resources folder, not an individual file.");
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel is null)
        {
            return null;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });

        return folders.Count == 0 ? null : folders[0].Path.LocalPath;
    }

    private async Task<string?> OpenFileAsync(string title, string fileTypeName, IReadOnlyList<string> extensions)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel is null)
        {
            return null;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(fileTypeName)
                {
                    Patterns = extensions.Select(extension => $"*.{extension}").ToArray()
                }
            ],
        });

        return files.Count == 0 ? null : files[0].Path.LocalPath;
    }

    private async Task<string?> SaveFileAsync(string title, string suggestedFileName)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel is null)
        {
            return null;
        }

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            FileTypeChoices =
            [
                new FilePickerFileType("JSON files")
                {
                    Patterns = ["*.json"]
                }
            ],
        });

        return file?.Path.LocalPath;
    }

    private async Task<bool> ConfirmApplyAsync(MainWindowViewModel vm)
    {
        var dialog = new Window
        {
            Title = "Apply plan",
            Width = 520,
            Height = 250,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = BuildApplyDialogContent(vm),
        };

        var result = false;
        if (dialog.Content is Panel panel)
        {
            var applyButton = panel.FindControl<Button>("ApplyButton");
            var cancelButton = panel.FindControl<Button>("CancelButton");
            if (applyButton is not null)
            {
                applyButton.Click += (_, _) =>
                {
                    result = true;
                    dialog.Close();
                };
            }

            if (cancelButton is not null)
            {
                cancelButton.Click += (_, _) => dialog.Close();
            }
        }

        await dialog.ShowDialog(this);
        return result;
    }

    private static Panel BuildApplyDialogContent(MainWindowViewModel vm)
    {
        return new StackPanel
        {
            Margin = new Avalonia.Thickness(18),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "Apply will rename stream files, remove merged source YMT files after backing them up, and copy generated resources into place.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new TextBlock { Text = $"Backup root: {vm.BackupRoot}", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = $"Planned stream renames: {vm.Summary.StreamRenameCount}" },
                new TextBlock { Text = $"Planned source backups: {vm.PlannedBackupCount}" },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children =
                    {
                        new Button { Name = "CancelButton", Content = "Cancel" },
                        new Button { Name = "ApplyButton", Content = "Apply" },
                    }
                }
            }
        };
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(18),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new Button
                    {
                        Content = "OK",
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    }
                }
            }
        };

        if (dialog.Content is Panel panel && panel.Children.OfType<Button>().FirstOrDefault() is { } button)
        {
            button.Click += (_, _) => dialog.Close();
        }

        await dialog.ShowDialog(this);
    }
}
