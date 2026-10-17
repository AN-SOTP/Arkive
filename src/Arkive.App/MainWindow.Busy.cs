using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Arkive.App;

public partial class MainWindow
{
    private int _busyDepth;
    private Stopwatch? _busyClock;
    private DispatcherTimer? _busyTimer;
    private static readonly System.Windows.Media.Brush BusyDimBrush = CreateBusyDimBrush();

    private static System.Windows.Media.Brush CreateBusyDimBrush()
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x99, 0, 0, 0));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Covers the tabs with a "please wait" card while a mod operation runs. The card appears only once the
    /// operation has taken longer than a moment, so quick ones do not flash; until it is disposed the tabs take no
    /// clicks or typing, so nothing changed meanwhile is lost when the lists are rebuilt.
    /// </summary>
    private IDisposable ShowBusy(string title, string detail)
    {
        _busyDepth++;
        BusyTitleText.Text = title;
        BusyDetailText.Text = detail;
        if (_busyDepth == 1)
        {
            _busyClock = Stopwatch.StartNew();
            BusyElapsedText.Text = string.Empty;
            // Clicks and keys are refused at once; the card itself follows after a short delay.
            BusyOverlay.Background = System.Windows.Media.Brushes.Transparent;
            BusyOverlay.Visibility = Visibility.Visible;
            BusyOverlay.Cursor = Cursors.Wait;
            BusyOverlay.Focus();
            ((FrameworkElement)BusyOverlay.Children[0]).Visibility = Visibility.Collapsed;
            _busyTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, BusyTimer_Tick, Dispatcher);
            _busyTimer.Start();
        }
        return new BusyScope(this);
    }

    private void BusyTimer_Tick(object? sender, EventArgs e)
    {
        if (_busyClock is null) return;
        var elapsed = _busyClock.Elapsed;
        if (elapsed < TimeSpan.FromMilliseconds(400)) return;
        if (BusyOverlay.Children[0] is FrameworkElement { Visibility: not Visibility.Visible } card)
        {
            card.Visibility = Visibility.Visible;
            BusyOverlay.Background = BusyDimBrush;
        }
        BusyElapsedText.Text = elapsed.TotalSeconds < 3 ? string.Empty : $"{elapsed.TotalSeconds:N0} s";
    }

    /// <summary>Changes the card's text while the same operation moves to its next step.</summary>
    private void UpdateBusy(string title, string? detail = null)
    {
        if (_busyDepth == 0) return;
        BusyTitleText.Text = title;
        if (detail is not null) BusyDetailText.Text = detail;
    }

    private void HideBusy()
    {
        if (_busyDepth == 0 || --_busyDepth > 0) return;
        _busyTimer?.Stop();
        _busyClock = null;
        BusyOverlay.Visibility = Visibility.Collapsed;
    }

    private sealed class BusyScope(MainWindow window) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            window.HideBusy();
        }
    }
}
