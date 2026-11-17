using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using HtaciAI.Services.KnowledgeBase;

namespace HtaciAI.Views.Settings;

/// <summary>
/// 本地嵌入模型的下载窗口。<b>非模态</b>（用 <see cref="Window.Show"/> 打开）：
/// 下载可能耗时很久，不该拦住主窗口；关掉这个窗口就等于取消下载，
/// 半截的 <c>.part</c> 会被清掉，下次检测会如实报「上次下载没有完成」。
///
/// 窗口本身不认识模型细节 —— 文件清单、校验与落盘都在 <see cref="LocalEmbeddingModel"/> 里，
/// 这里只负责把进度画出来。
/// </summary>
public partial class ModelDownloadWindow : Window
{
    private readonly CancellationTokenSource _cts = new();
    private bool _finished;
    private bool _cancelRequested;

    /// <summary>下载成功并且已通过检测。宿主据此刷新卡片。</summary>
    public bool Succeeded { get; private set; }

    /// <summary>结束（成功 / 失败 / 取消）时触发。</summary>
    public event Action? Completed;

    /// <summary>供 XAML 加载器使用。</summary>
    public ModelDownloadWindow()
    {
        InitializeComponent();

        TargetText.Text = $"{LocalEmbeddingModel.DisplayName} → {LocalEmbeddingModel.DirectoryPath}";

        CancelBtn.PointerEntered += (_, _) => CancelBtn.Background = new SolidColorBrush(Color.Parse("#F1F3F5"));
        CancelBtn.PointerExited += (_, _) => CancelBtn.Background = Brushes.Transparent;
        CancelBtn.PointerPressed += (_, _) => OnCancelClicked();

        // 进度条是两条 Border，填充宽度得自己算：轨道宽度随窗口定，这里跟着它走一次
        Track.GetObservable(BoundsProperty).Subscribe(new AnonymousObserver(_ => ApplyFraction(_lastFraction)));
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _ = RunAsync();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // 关窗即取消：用户关掉窗口的意图就是「别下了」，不能让它背着用户继续写盘
        if (!_finished)
        {
            _cancelRequested = true;
            _cts.Cancel();
        }

        base.OnClosing(e);
    }

    private void OnCancelClicked()
    {
        if (_finished)
        {
            Close();
            return;
        }

        _cancelRequested = true;
        _cts.Cancel();
        CancelText.Text = "正在取消…";
        DetailText.Text = "已通知下载停止，正在清理未完成的文件";
    }

    private async Task RunAsync()
    {
        try
        {
            await LocalEmbeddingModel.DownloadAsync(
                progress => Dispatcher.UIThread.Post(() => Render(progress)),
                _cts.Token);

            var status = LocalEmbeddingModel.Detect();
            Succeeded = status.Installed;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _finished = true;
                ApplyFraction(1);
                PercentText.Text = status.Installed ? "下载完成" : "下载结束，但校验没通过";
                PercentText.Foreground = new SolidColorBrush(
                    Color.Parse(status.Installed ? "#16A34A" : "#B91C1C"));
                DetailText.Text = status.Installed
                    ? $"{LocalEmbeddingModel.DisplayName} · {status.SizeText} · 已完成索引可用的向量化"
                    : status.Message;
                CancelText.Text = "关闭";
                CancelBtn.Background = new SolidColorBrush(Color.Parse("#4A90D9"));
                ((TextBlock)CancelBtn.Child!).Foreground = Brushes.White;
            });
        }
        catch (OperationCanceledException)
        {
            LocalEmbeddingModel.CleanPartFiles(LocalEmbeddingModel.DirectoryPath);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _finished = true;
                PercentText.Text = "已取消";
                PercentText.Foreground = new SolidColorBrush(Color.Parse("#6B7280"));
                DetailText.Text = "未完成的文件已清理，下次可以从头开始。";
                CancelText.Text = "关闭";
            });
        }
        catch (Exception ex)
        {
            LocalEmbeddingModel.CleanPartFiles(LocalEmbeddingModel.DirectoryPath);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _finished = true;
                PercentText.Text = "下载失败";
                PercentText.Foreground = new SolidColorBrush(Color.Parse("#B91C1C"));
                DetailText.Text = "";
                ErrorText.Text = ex.Message;
                ErrorBox.IsVisible = true;
                CancelText.Text = "关闭";
            });
        }
        finally
        {
            Completed?.Invoke();
        }
    }

    private double _lastFraction;

    private void Render(LocalEmbeddingModel.DownloadProgress progress)
    {
        if (_finished || _cancelRequested) return;

        ApplyFraction(progress.Fraction);

        // 还没开始收字节（连源、读清单）时不写「0% / 0 B」，那两串数字看着像卡死了
        PercentText.Text = progress.Total > 0
            ? $"{progress.Fraction * 100:0}%　" +
              $"{LocalEmbeddingModel.ModelStatus.Format(progress.Received)} / " +
              $"{LocalEmbeddingModel.ModelStatus.Format(progress.Total)}"
            : "准备中…";

        DetailText.Text = progress.DetailText;
    }

    private void ApplyFraction(double fraction)
    {
        _lastFraction = Math.Clamp(fraction, 0, 1);
        var width = Track.Bounds.Width;
        Fill.Width = width <= 0 ? 0 : width * _lastFraction;
    }

    /// <summary>把「属性变化」包成一个可订阅的观察者，免得为一行联动引入整个 Reactive 依赖。</summary>
    private sealed class AnonymousObserver(Action<Rect> onNext) : IObserver<Rect>
    {
        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void OnNext(Rect value) => onNext(value);
    }
}
