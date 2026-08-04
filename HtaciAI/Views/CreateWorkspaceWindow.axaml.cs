using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using HtaciAI.Models;

namespace HtaciAI.Views;

public partial class CreateWorkspaceWindow : Window
{
    private readonly List<string> _folders = new();

    public Workspace? Result { get; private set; }

    public CreateWorkspaceWindow()
    {
        InitializeComponent();

        ModelCombo.ItemsSource = WorkspaceOptions.Models;
        ModelCombo.SelectedIndex = 0;

        FrameworkCombo.ItemsSource = WorkspaceOptions.AgentFrameworks;
        FrameworkCombo.SelectedIndex = 0;

        PermissionCombo.ItemsSource = WorkspaceOptions.PermissionModes;
        PermissionCombo.SelectedIndex = 0;
        UpdatePermissionHint();
    }

    private async void OnAddFolderClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "选择工作目录", AllowMultiple = true });

        foreach (var folder in folders)
        {
            var path = folder.Path.LocalPath;
            if (!_folders.Contains(path))
                _folders.Add(path);
        }

        RefreshFolderList();
    }

    private void RefreshFolderList()
    {
        FolderList.Children.Clear();
        foreach (var path in _folders)
            FolderList.Children.Add(CreateFolderItem(path));
    }

    private Border CreateFolderItem(string path)
    {
        var pathText = new TextBlock
        {
            Text = path,
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.Parse("#1A1A2E")),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(10, 0, 0, 0)
        };

        var removeBtn = new Button
        {
            Content = new TextBlock
            {
                Text = "\uE8BB",
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 11,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            },
            Background = Brushes.Transparent,
            Foreground = Brushes.Gray,
            Width = 22,
            Height = 22,
            Padding = new Avalonia.Thickness(0),
            CornerRadius = new Avalonia.CornerRadius(5),
            Margin = new Avalonia.Thickness(6, 0, 6, 0)
        };

        var item = new Border
        {
            Height = 36,
            CornerRadius = new Avalonia.CornerRadius(7),
            Background = new SolidColorBrush(Color.Parse("#F1F3F5")),
            Cursor = new Cursor(StandardCursorType.Arrow),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children = { pathText, removeBtn }
            }
        };
        Grid.SetColumn(removeBtn, 1);

        item.PointerEntered += (s, e) =>
        {
            removeBtn.Foreground = new SolidColorBrush(Color.Parse("#546E7A"));
        };
        item.PointerExited += (s, e) =>
        {
            removeBtn.Foreground = Brushes.Gray;
        };

        removeBtn.Click += (s, e) =>
        {
            _folders.Remove(path);
            RefreshFolderList();
        };

        return item;
    }

    private void OnPermissionChanged(object? sender, SelectionChangedEventArgs e)
        => UpdatePermissionHint();

    private void UpdatePermissionHint()
    {
        var mode = PermissionCombo.SelectedItem as string ?? "";
        PermissionHint.Text = "权限说明：" + WorkspaceOptions.GetPermissionHint(mode);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
        => Close();

    private void OnCreateClick(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim();
        var model = ModelCombo.SelectedItem as string ?? WorkspaceOptions.Models[0];
        var framework = FrameworkCombo.SelectedItem as string ?? WorkspaceOptions.AgentFrameworks[0];
        var permission = PermissionCombo.SelectedItem as string ?? WorkspaceOptions.PermissionModes[0];

        var folders = new List<string>(_folders);
        if (folders.Count == 0)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "HtaciAI-Workspace-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            folders.Add(tmp);
        }

        if (string.IsNullOrEmpty(name))
            name = Path.GetFileName(folders[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        Result = new Workspace
        {
            Name = name,
            Folders = folders,
            Model = model,
            AgentFramework = framework,
            PermissionMode = permission,
            Prompt = PromptBox.Text ?? "",
            EnvVars = EnvVarsBox.Text ?? ""
        };
        Close();
    }
}
