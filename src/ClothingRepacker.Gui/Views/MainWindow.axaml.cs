using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ClothingRepacker.Gui.ViewModels;
using System.Diagnostics;

namespace ClothingRepacker.Gui.Views;

public partial class MainWindow : Window
{
    private const string StoreUrl = "https://red40.dev/";
    private const string SupportUrl = "https://red40.dev/support";
    private const string TeamSupportUrl = "https://slrn.dev/ko-fi";

    private MainWindowViewModel? _subscribedViewModel;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachViewModelEvents();
        AddHandler(DragDrop.DragOverEvent, DragOver);
        AddHandler(DragDrop.DropEvent, Drop);
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void AttachViewModelEvents()
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.HelpRequested -= ViewModel_HelpRequested;
        }

        _subscribedViewModel = ViewModel;
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.HelpRequested += ViewModel_HelpRequested;
        }
    }

    private async void ViewModel_HelpRequested(object? sender, EventArgs e)
        => await ShowHelpAsync();

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

    private async void OpenProject_Click(object? sender, RoutedEventArgs e)
    {
        var path = await OpenFileAsync("Open project", "Project files", ["json"]);
        if (path is not null && ViewModel is { } vm)
        {
            await vm.LoadProjectAsync(path);
        }
    }

    private async void SaveProject_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        if (vm.HasCurrentProject)
        {
            await vm.SaveCurrentProjectAsync();
            return;
        }

        await SaveProjectAsAsync(vm);
    }

    private async void SaveProjectAs_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            await SaveProjectAsAsync(vm);
        }
    }

    private async void ClearProject_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            await vm.ClearProjectAsync();
        }
    }

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

    private async void UpdateAvailable_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { UpdateReleaseUrl.Length: > 0 } vm)
        {
            return;
        }

        await OpenExternalUrlAsync(vm.UpdateReleaseUrl, "Could not open update");
    }

    private async void StoreLogo_Click(object? sender, RoutedEventArgs e)
        => await OpenExternalUrlAsync(StoreUrl, "Could not open store");

    private async void TeamSupport_Click(object? sender, RoutedEventArgs e)
        => await OpenExternalUrlAsync(TeamSupportUrl, "Could not open support page");

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

    private async Task SaveProjectAsAsync(MainWindowViewModel vm)
    {
        var path = await SaveFileAsync("Save project", "clothing-repacker-project.json");
        if (path is not null)
        {
            await vm.SaveProjectAsync(path);
        }
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

    private async Task ShowHelpAsync()
    {
        var closeButton = new Button
        {
            Content = "OK",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
        };
        var supportLinkButton = new Button
        {
            MinHeight = 0,
            Padding = new Avalonia.Thickness(0),
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Content = new TextBlock
            {
                Text = "Red40 Support",
                TextDecorations = Avalonia.Media.TextDecorations.Underline,
                Foreground = Avalonia.Media.Brush.Parse("#2B6CB0"),
            },
        };

        var dialog = new Window
        {
            Title = "How to use Red40 Clothing Repacker",
            Width = 560,
            Height = 370,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(18),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Basic workflow",
                        FontWeight = Avalonia.Media.FontWeight.SemiBold,
                    },
                    new TextBlock
                    {
                        Text = "1. Add one or more clothing resource folders with Add Resource or drag and drop.\n"
                               + "2. Set the merged resource name, target prefix, output location, and backup location.\n"
                               + "3. Click Analyze and review the Summary, Warnings, and Errors tabs.\n"
                               + "4. Click Build Preview to generate the merged output and inspect the results.\n"
                               + "5. Click Apply when ready. Backups are written before source files are changed.",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 4,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "Need more help or have feature suggestions?",
                                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                            },
                            supportLinkButton,
                        },
                    },
                    closeButton,
                }
            }
        };

        supportLinkButton.Click += async (_, _) => await OpenExternalUrlAsync(SupportUrl, "Could not open support");
        closeButton.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);
    }

    private async Task OpenExternalUrlAsync(string url, string errorTitle)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(errorTitle, ex.Message);
        }
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
