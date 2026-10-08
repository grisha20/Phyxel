using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Phyxel.Core;

namespace Phyxel.UI;

internal sealed class StartupScreen : Form
{
    private readonly Label operation = new();
    private readonly Label elapsed = new();
    private readonly Label counter = new();
    private readonly ProgressBar progressBar = new();
    private readonly Button cancel = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    private readonly CancellationTokenSource cancellation = new();
    private readonly Stopwatch watch = new();
    private readonly object progressLock = new();
    private readonly Action<Action<int, int, string>, CancellationToken> prepare;
    private Task? preparation;
    private string message = "Проверка готовых шейдеров…";
    private int completed;
    private int total;
    private bool failed;
    internal bool Ready { get; private set; }
    internal bool Failed => failed;
    internal int MessageLoopTicks { get; private set; }
    internal event Action<StartupScreen>? DiagnosticTick;

    internal StartupScreen(Action<Action<int, int, string>, CancellationToken> prepare)
    {
        this.prepare = prepare;
        Text = "Phyxel — подготовка к запуску";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(620, 310);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(16, 23, 31);
        ForeColor = Color.FromArgb(222, 229, 235);
        Font = new Font("Segoe UI", 10);
        var title = new Label { Text = "PHYXEL", Font = new Font("Segoe UI", 23, FontStyle.Bold),
            ForeColor = Color.FromArgb(70, 186, 222), Location = new Point(26, 20), Size = new Size(550, 48) };
        var explanation = new Label { Text = "Готовим игру к запуску. При обновлении графики это может занять несколько минут.",
            Location = new Point(28, 76), Size = new Size(565, 42) };
        operation.SetBounds(28, 126, 565, 45);
        operation.Text = message;
        counter.SetBounds(28, 174, 565, 25);
        progressBar.SetBounds(28, 202, 564, 9);
        progressBar.Style = ProgressBarStyle.Marquee;
        progressBar.MarqueeAnimationSpeed = 25;
        elapsed.SetBounds(28, 224, 375, 58);
        elapsed.ForeColor = Color.FromArgb(155, 173, 188);
        cancel.SetBounds(450, 235, 142, 36);
        cancel.Text = "Отменить запуск";
        cancel.FlatStyle = FlatStyle.Flat;
        cancel.Click += (_, _) => Close();
        Controls.AddRange([title, explanation, operation, counter, progressBar, elapsed, cancel]);
        timer.Tick += (_, _) => TickProgress();
        FormClosing += (_, _) =>
        {
            cancellation.Cancel();
            if (!Ready && !failed) StartupLog.Write("cancelled by user");
        };
        Shown += (_, _) =>
        {
            watch.Start();
            preparation = Task.Run(() => prepare(Report, cancellation.Token), cancellation.Token);
            // Closing does not wait for uninterruptible D3DCompile. Thread-pool
            // workers are background threads; observe a late fault safely.
            _ = preparation.ContinueWith(task => { _ = task.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            timer.Start();
        };
    }

    internal static bool Run(Action<Action<int, int, string>, CancellationToken> prepare)
    {
        using StartupScreen screen = new(prepare);
        Application.Run(screen);
        return screen.Ready;
    }

    private void Report(int count, int size, string text)
    {
        lock (progressLock) { completed = count; total = size; message = text; }
    }

    private void TickProgress()
    {
        MessageLoopTicks++;
        lock (progressLock)
        {
            operation.Text = message;
            if (total > 0)
            {
                progressBar.Style = ProgressBarStyle.Continuous;
                progressBar.Maximum = total;
                progressBar.Value = Math.Clamp(completed, 0, total);
                counter.Text = $"Подготовлено программ: {completed} из {total}";
            }
        }
        elapsed.Text = $"Прошло времени: {watch.Elapsed:mm\\:ss}\nОкно активно · подготовка продолжается";
        DiagnosticTick?.Invoke(this);
        if (IsDisposed || preparation is null || !preparation.IsCompleted) return;
        timer.Stop();
        try
        {
            preparation.GetAwaiter().GetResult();
            Ready = true;
            StartupLog.Write($"loading-window completed seconds={watch.Elapsed.TotalSeconds:F3} uiTicks={MessageLoopTicks}");
            Close();
        }
        catch (OperationCanceledException) { Close(); }
        catch (Exception exception)
        {
            failed = true;
            Environment.ExitCode = 1;
            StartupLog.Write("preparation failed " + exception);
            operation.Text = "Не удалось подготовить графику: " + exception.Message;
            counter.Text = "Закройте окно и отправьте журнал разработчику.";
            elapsed.Text = StartupLog.Path ?? "Журнал недоступен.\n" + exception.GetType().Name;
            cancel.Text = "Закрыть";
            progressBar.Value = 0;
        }
    }

    internal static void ShowFailure(Exception exception)
    {
        StartupLog.Write("game startup/runtime failed " + exception);
        MessageBox.Show("Не удалось запустить Phyxel.\n\n" + exception.Message + "\n\nЖурнал:\n" +
            (StartupLog.Path ?? "Недоступен"), "Phyxel — ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        // CTS remains valid for the background worker after a cancelled window.
        // Dispose it once that worker has finished, without blocking the UI.
        if (disposing && preparation is not null)
            _ = preparation.ContinueWith(_ => cancellation.Dispose(), TaskScheduler.Default);
        else if (disposing) cancellation.Dispose();
        base.Dispose(disposing);
    }
}
