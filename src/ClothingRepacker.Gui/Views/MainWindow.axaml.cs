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
        var paths = await PickFoldersAsync("Select clothing resource folders", allowMultiple: true);
        if (paths.Count > 0)
        {
            ViewModel?.AddResourceFolders(paths);
        }
    }

    private void RemoveResource_Click(object? sender, RoutedEventArgs e)
        => ViewModel?.RemoveSelectedResourceFolder();

    private void ClearResources_Click(object? sender, RoutedEventArgs e)
        => ViewModel?.ClearResourceFolders();

    private async void BrowseOutput_Click(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Select output root folder");
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
        e.DragEffects = HasSupportedDropData(e)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Drop(object? sender, DragEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var resourceCountBeforeDrop = ViewModel.ResourcePaths.Count;
        var files = e.DataTransfer.TryGetFiles();
        if (files is not null)
        {
            var folders = new List<string>();
            foreach (var item in files)
            {
                if (item is IStorageFolder folder)
                {
                    folders.Add(folder.Path.LocalPath);
                    continue;
                }

                var localPath = item.Path.LocalPath;
                if (Directory.Exists(localPath))
                {
                    folders.Add(localPath);
                }
            }

            ViewModel.AddResourceFolders(folders);
        }

        if (e.DataTransfer.TryGetText() is { } text)
        {
            ViewModel.AddResourceFoldersFromText(text);
        }

        if (ViewModel.ResourcePaths.Count > resourceCountBeforeDrop)
        {
            return;
        }

        await ShowMessageAsync("Folder required", "Drop resource folder paths or clothing resource folders.");
    }

    private static bool HasSupportedDropData(DragEventArgs e)
        => e.DataTransfer.Contains(DataFormat.File)
           || e.DataTransfer.Contains(DataFormat.Text)
           || e.DataTransfer.TryGetFiles() is not null
           || e.DataTransfer.TryGetText() is not null;

    private async Task<string?> PickFolderAsync(string title)
        => (await PickFoldersAsync(title, allowMultiple: false)).FirstOrDefault();

    private async Task<IReadOnlyList<string>> PickFoldersAsync(string title, bool allowMultiple)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel is null)
        {
            return [];
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
        });

        return folders.Select(folder => folder.Path.LocalPath).ToList();
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
        var dialogContent = BuildApplyDialogContent(vm);
        var dialog = new Window
        {
            Title = "Apply plan",
            Width = 520,
            Height = 250,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = dialogContent.Panel,
        };

        var result = false;
        dialogContent.ApplyButton.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };
        dialogContent.CancelButton.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);
        return result;
    }

    private static ApplyDialogContent BuildApplyDialogContent(MainWindowViewModel vm)
    {
        var cancelButton = new Button { Content = "Cancel" };
        var applyButton = new Button { Content = "Apply" };
        var modeMessage = vm.CopyResourcesToOutputBeforeRename
            ? "Apply will copy source resources into the output root, rename stream files in that copy, remove copied merged source YMT files after backing them up, and copy generated resources into place. Original resources will not be renamed."
            : "Apply will rename stream files, remove merged source YMT files after backing them up, and copy generated resources into place.";
        var panel = new StackPanel
        {
            Margin = new Avalonia.Thickness(18),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = modeMessage,
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
                        cancelButton,
                        applyButton,
                    }
                }
            }
        };

        return new ApplyDialogContent(panel, applyButton, cancelButton);
    }

    private sealed record ApplyDialogContent(Panel Panel, Button ApplyButton, Button CancelButton);

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
