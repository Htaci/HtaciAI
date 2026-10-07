using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using HtaciAI.Data;
using HtaciAI.Models;

namespace HtaciAI.Views.Settings;

/// <summary>模型服务页：自定义模型（按服务商分组，可增删改）。</summary>
public partial class ModelServicePage : UserControl
{
    public ModelServicePage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            try
            {
                await RefreshProvidersAsync();
            }
            catch (Exception ex)
            {
                ProviderList.Children.Add(new TextBlock
                {
                    Text = "加载自定义模型失败：" + ex.Message,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
                    Margin = new Thickness(20, 12, 0, 12),
                });
            }
        };
    }

    private async Task RefreshProvidersAsync()
    {
        ProviderList.Children.Clear();
        foreach (var provider in await ProviderRepository.GetAllAsync())
            ProviderList.Children.Add(await CreateProviderGroupAsync(provider));
    }

    // ---- 服务商分组 ----

    private async Task<Control> CreateProviderGroupAsync(AiProvider provider)
    {
        var models = await ModelRepository.GetByProviderAsync(provider.Id);

        // 服务商标题行独立在表格外；带边框的只有表格本身
        var header = CreateProviderHeader(provider, models.Count);
        var table = CreateModelTable(provider, models);

        return new StackPanel
        {
            Margin = new Thickness(0, 10, 0, 0),
            Children = { header, table },
        };
    }

    private Control CreateProviderHeader(AiProvider provider, int modelCount)
    {
        var title = new TextBlock
        {
            Text = $"{provider.Name} {modelCount}",
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#1A1A2E")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 6),
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
        };
        grid.Children.Add(title);

        var addModelBtn = CreateSmallButton("+ 添加模型", () => _ = AddModelAsync(provider));
        var editBtn = CreateSmallButton("编辑", () => _ = EditProviderAsync(provider));
        var deleteBtn = CreateSmallButton("删除", () => _ = DeleteProviderAsync(provider));
        Grid.SetColumn(addModelBtn, 1); grid.Children.Add(addModelBtn);
        Grid.SetColumn(editBtn, 2); grid.Children.Add(editBtn);
        Grid.SetColumn(deleteBtn, 3); grid.Children.Add(deleteBtn);

        return grid;
    }

    private Control CreateModelTable(AiProvider provider, List<AiModel> models)
    {
        var header = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F5F5F5")),
            CornerRadius = new CornerRadius(8, 8, 0, 0),
            Height = 35,
            Child = CreateModelRowHeader(),
        };

        var rows = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        if (models.Count == 0)
        {
            rows.Children.Add(new TextBlock
            {
                Text = "暂无模型，点击「+ 添加模型」添加",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")),
                Margin = new Thickness(20, 12, 0, 12),
            });
        }
        else
        {
            foreach (var model in models)
                rows.Children.Add(CreateModelRow(provider, model));
        }

        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 35,
            CornerRadius = new CornerRadius(8),
            BoxShadow = BoxShadows.Parse("0 0 1 0 #80808080"),
            Child = new StackPanel { Children = { header, rows } },
        };
    }

    // 注意：操作列必须是固定宽度而非 Auto —— 表头与内容行是两个独立 Grid，
    // 若为 Auto，表头（"操作"）与内容行（按钮）量出的宽度不同，会导致前方 Star 列错位。
    private static Grid CreateModelGrid()
        => new() { ColumnDefinitions = new ColumnDefinitions("2*,1*,1*,1*,0.8*,1.4*,140") };

    private Control CreateModelRowHeader()
    {
        var grid = CreateModelGrid();
        string[] headers = { "模型名称", "每百万输入", "缓存命中", "每百万输出", "上下文", "模型能力", "操作" };
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = new TextBlock
            {
                Text = headers[i],
                FontSize = 12,
                FontWeight = FontWeight.DemiBold,
                Foreground = new SolidColorBrush(Color.Parse("#374151")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20, 0, 0, 0),
            };
            if (i == headers.Length - 1)
                cell.Margin = new Thickness(4, 0, 8, 0);
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private Control CreateModelRow(AiProvider provider, AiModel model)
    {
        var grid = CreateModelGrid();
        grid.Margin = new Thickness(0, 10, 0, 0);

        // 模型名称：调用 id 不单独列出，悬停时提示显示
        var nameCell = CreateModelCell(0, model.DisplayName, isBold: true);
        if (!string.IsNullOrEmpty(model.CallId))
            ToolTip.SetTip(nameCell, $"调用 id：{model.CallId}");
        grid.Children.Add(nameCell);

        grid.Children.Add(CreateModelCell(1, FormatPriceCell(model.PriceInput, model.Currency), gray: true));
        grid.Children.Add(CreateModelCell(2, FormatPriceCell(model.PriceCacheHit, model.Currency), gray: true));
        grid.Children.Add(CreateModelCell(3, FormatPriceCell(model.PriceOutput, model.Currency), gray: true));
        grid.Children.Add(CreateModelCell(4, FormatContextWindow(model.ContextWindow), gray: true));
        grid.Children.Add(CreateModelCell(5, FormatCapabilities(model.Capabilities), gray: true));

        var ops = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(4, 0, 8, 0),
            Children =
            {
                CreateSmallButton("编辑", () => _ = EditModelAsync(provider, model)),
                CreateSmallButton("删除", () => _ = DeleteModelAsync(provider, model)),
            },
        };
        Grid.SetColumn(ops, 6);
        grid.Children.Add(ops);

        return grid;
    }

    /// <summary>上下文窗口展示：≥1M 简写为 M，否则原样显示，未知返回"未知"。</summary>
    private static string FormatContextWindow(long window)
    {
        if (window <= 0) return "未知";
        return window >= 1_000_000 ? $"{window / 1_000_000.0:0.#}M" : window.ToString();
    }

    private static TextBlock CreateModelCell(int column, string text, bool isBold = false, bool gray = false)
    {
        var cell = new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = isBold ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = gray
                ? new SolidColorBrush(Color.Parse("#6B7280"))
                : new SolidColorBrush(Color.Parse("#1A1A2E")),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(20, 0, 0, 0),
        };
        Grid.SetColumn(cell, column);
        return cell;
    }

    private Button CreateSmallButton(string text, Action onClick)
    {
        var btn = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 12 },
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.Parse("#546E7A")),
            Height = 26,
            Padding = new Thickness(8, 0),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static string FormatCapabilities(List<string> caps)
    {
        if (caps.Count == 0) return "—";
        return string.Join("、", caps.Select(c => ModelCapabilities.Labels.TryGetValue(c, out var label) ? label : c));
    }

    private static string FormatPriceCell(double? value, string currency)
    {
        if (value is null) return "—";
        string cur = currency == "USD" ? "$" : "¥";
        return $"{cur}{value.Value.ToString("0.##")}";
    }

    // ---- 动作 ----

    private async void OnAddProviderClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new ProviderEditWindow();
        await ShowDialogAsync(dialog);
        if (dialog.Result is { } provider)
        {
            await ProviderRepository.CreateAsync(provider);
            await RefreshProvidersAsync();
        }
    }

    private async Task EditProviderAsync(AiProvider provider)
    {
        var dialog = new ProviderEditWindow(provider);
        await ShowDialogAsync(dialog);
        if (dialog.Result is { } updated)
        {
            await ProviderRepository.UpdateAsync(updated);
            await RefreshProvidersAsync();
        }
    }

    private async Task DeleteProviderAsync(AiProvider provider)
    {
        await ProviderRepository.DeleteAsync(provider.Id);
        await RefreshProvidersAsync();
    }

    private async Task AddModelAsync(AiProvider provider)
    {
        var dialog = new ModelEditWindow(provider.Id, null);
        await ShowDialogAsync(dialog);
        if (dialog.Result is { } model)
        {
            await ModelRepository.CreateAsync(model);
            await RefreshProvidersAsync();
        }
    }

    private async Task EditModelAsync(AiProvider provider, AiModel model)
    {
        var dialog = new ModelEditWindow(provider.Id, model);
        await ShowDialogAsync(dialog);
        if (dialog.Result is { } updated)
        {
            await ModelRepository.UpdateAsync(updated);
            await RefreshProvidersAsync();
        }
    }

    private async Task DeleteModelAsync(AiProvider provider, AiModel model)
    {
        await ModelRepository.DeleteAsync(model.Id);
        await RefreshProvidersAsync();
    }

    private async Task ShowDialogAsync(Window dialog)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();
    }
}
