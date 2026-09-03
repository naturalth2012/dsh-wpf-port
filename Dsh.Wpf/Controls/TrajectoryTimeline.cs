using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Dsh.App;

// UseWindowsForms pulls in System.Drawing global usings that collide with WPF
// (Size/Brush/Point/Brushes/MouseEventArgs). Alias the WPF ones explicitly so the file can
// keep using idiomatic WPF names.

using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;
using WpfRect = System.Windows.Rect;
using WpfPen = System.Windows.Media.Pen;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using WpfMouseButton = System.Windows.Input.MouseButton;
using WpfMouseWheelEventArgs = System.Windows.Input.MouseWheelEventArgs;
using WpfDrawingContext = System.Windows.Media.DrawingContext;
using WpfFormattedText = System.Windows.Media.FormattedText;
using WpfTypeface = System.Windows.Media.Typeface;
using WpfFlowDirection = System.Windows.FlowDirection;
using WpfCursors = System.Windows.Input.Cursors;
using WpfApplication = System.Windows.Application;

namespace Dsh.Wpf.Controls;

public sealed class TrajectoryTimeline : Canvas
{
    public static readonly DependencyProperty TrajectoryProperty =
        DependencyProperty.Register(
            nameof(Trajectory), typeof(System.Collections.IEnumerable), typeof(TrajectoryTimeline),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnTrajectoryChanged));

    public System.Collections.IEnumerable? Trajectory
    {
        get => (System.Collections.IEnumerable?)GetValue(TrajectoryProperty);
        set => SetValue(TrajectoryProperty, value);
    }

    /// <summary>
    /// Bindable, two-way focused step index (-1 = none). Lets the ledger table and the timeline
    /// stay in sync: selecting a row highlights the dot, and clicking a dot selects the row.
    /// Mirrors the internal focus state used as the zoom anchor.
    /// </summary>
    public static readonly DependencyProperty FocusIndexProperty =
        DependencyProperty.Register(
            nameof(FocusIndex), typeof(int), typeof(TrajectoryTimeline),
            new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender, OnFocusIndexChanged));

    public int FocusIndex
    {
        get => (int)GetValue(FocusIndexProperty);
        set => SetValue(FocusIndexProperty, value);
    }

    private static void OnFocusIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (TrajectoryTimeline)d;
        self._focusIndex = (int)e.NewValue;
        // Ledger→timeline linkage: when the selected row is OUTSIDE the visible window (easy at
        // high zoom), slide the window to reveal it. When it is already visible, do NOT touch the
        // viewport — that would make every click feel like the chart is drifting.
        if (self._focusIndex >= 0 && self._focusIndex < self._snapshot.Length && self._zoom > 1.0)
        {
            self.RevealStep(self._focusIndex);
        }
        self.InvalidateVisual();
    }

    /// <summary>Pan the window just enough to bring <paramref name="index"/> inside it (with a
    /// small margin), leaving it untouched if the step is already visible.</summary>
    private void RevealStep(int index)
    {
        int n = _snapshot.Length;
        if (n <= 1) { _viewportCenterSteps = 0; return; }
        double halfWindow = (n - 1) / (2.0 * _zoom);
        double margin = halfWindow * 0.1;
        if (index < _viewportCenterSteps - halfWindow + margin)
        {
            _viewportCenterSteps = index + halfWindow - margin;
        }
        else if (index > _viewportCenterSteps + halfWindow - margin)
        {
            _viewportCenterSteps = index - halfWindow + margin;
        }
        else
        {
            return;   // already visible
        }
        ClampViewport(n);
    }

    private SessionFold.TrajectoryStep[] _snapshot = [];
    private int _hoverIndex = -1;
    private int _focusIndex = -1;   // step selected by drag (highlighted + time-zoom anchor)
    private double _zoom = 1.0;     // 1.0 = whole timeline; >1 = zoom into a time window
    private bool _dragging = false;

    /// <summary>Fired when the user drag-selects a step (focus). Value = focused step index or -1.</summary>
    public event Action<int>? FocusChanged;

    /// <summary>Fired when the user wheel-zooms. Value = current zoom factor.</summary>
    public event Action<double>? ZoomChanged;

    public TrajectoryTimeline()
    {
        ClipToBounds = true;
        Background = WpfBrushes.Transparent;   // enable hit-testing for ToolTip
        // Per-step Ellipses are children we manage ourselves.
        Focusable = true;                       // receive keyboard/mouse focus for drag + wheel
        Cursor = WpfCursors.Cross;
    }

    private static void OnTrajectoryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((TrajectoryTimeline)d).Rebuild();

    private void Rebuild()
    {
        Children.Clear();
        _snapshot = Trajectory is System.Collections.ICollection c
            ? new SessionFold.TrajectoryStep[c.Count]
            : Array.Empty<SessionFold.TrajectoryStep>();
        if (Trajectory is System.Collections.IEnumerable e)
        {
            int i = 0;
            foreach (var item in e)
            {
                if (i >= _snapshot.Length) break;
                if (item is SessionFold.TrajectoryStep s) _snapshot[i++] = s;
            }
        }
        // The step count just changed (streaming appends steps; a session switch replaces them
        // all), so a viewport center computed for the previous count may now point past the end.
        // Keep the user's zoom/pan position where it is still valid, and re-center when starting
        // over from empty.
        if (_snapshot.Length == 0)
        {
            _viewportCenterSteps = 0;
        }
        else
        {
            ClampViewport(_snapshot.Length);
        }
        // One transparent hit/tooltip target per step.
        // The visible gantt bars are drawn in OnRender (Input/Model/Tools lanes), so these
        // children must NOT paint anything — an earlier version filled them with the kind colour,
        // which double-drew a dot on top of every bar. They exist purely so WPF's tooltip service
        // has a UIElement to attach to; hit-testing itself is computed analytically in
        // HitTestIndex (nearest-x), so these stay cheap and invisible.
        for (int i = 0; i < _snapshot.Length; i++)
        {
            var target = new System.Windows.Controls.Border
            {
                Width = 6,
                Background = WpfBrushes.Transparent,
                Cursor = WpfCursors.Hand,
            };
            var tip = BuildTooltip(_snapshot[i], i);
            ToolTipService.SetToolTip(target, tip);
            // Children.Add takes UIElement; Canvas handles layout via Left/Top.
            Children.Add(target);
            SetLeft(target, 0);
            SetTop(target, 0);
        }
        InvalidateVisual();
    }

    private string BuildTooltip(SessionFold.TrajectoryStep s, int idx)
    {
        // seq / time-relative. Seq is the wire sequence; Time is event time (ms, monotonic from session start).
        string kind = KindLabel(s.Kind);
        string idxLabel = $"#{idx + 1}/{_snapshot.Length}";

        // Native-parity header: "ASSISTANT Turn 1 · Request 2".
        var header = new System.Text.StringBuilder();
        header.Append(kind);
        if (s.TurnIndex is { } turn) header.Append(" Turn ").Append(turn);
        if (s.RequestIndex is { } req) header.Append(" · Request ").Append(req);
        if (s.IsError) header.Append(" · ERROR");

        // Timing line when the payload reported a duration (native shows "Total 3.0s").
        var meta = new System.Collections.Generic.List<string> { $"seq={s.Seq}" };
        meta.Add(s.Time > 0 ? $"+{FormatTime(s.Time)}" : "t=0");
        if (s.DurationMs is { } ms) meta.Add($"Total {FormatTime(ms)}");
        if (s.TtftMs is { } ttft) meta.Add($"TTFT {FormatTime(ttft)}");
        // Token totals (native parity). Formatted with invariant culture so a comma decimal
        // separator locale can't turn "1.2k" into something ambiguous.
        var tokens = new System.Collections.Generic.List<string>();
        if (s.InputTokens is { } input) tokens.Add($"in {input}");
        if (s.CacheReadTokens is { } cacheRead) tokens.Add($"cache+ {cacheRead}");
        if (s.CacheWriteTokens is { } cacheWrite) tokens.Add($"cache- {cacheWrite}");
        if (s.OutputTokens is { } output) tokens.Add($"out {output}");
        if (s.ThinkTokens is { } think) tokens.Add($"think {think}");
        if (tokens.Count > 0) meta.Add(string.Join(" · ", tokens));
        if (s.CallId is { Length: > 0 } call) meta.Add($"call={call}");

        string text = string.IsNullOrWhiteSpace(s.Text) ? "(no payload)" : s.Text.Trim();
        if (text.Length > 240) text = text[..240] + "…";
        return $"{header}  [{string.Join("  ", meta)}]\n{text}";
    }

    // MM:SS or HH:MM:SS for larger values.
    private static string FormatTime(long ms)
    {
        if (ms <= 0) return "0s";
        var ts = TimeSpan.FromMilliseconds(ms);
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}:{ts.Seconds:00}";
        return $"{ts.TotalSeconds:0.0}s";
    }

    protected override WpfSize MeasureOverride(WpfSize constraint)
    {
        double w = double.IsNaN(constraint.Width) || constraint.Width <= 0 ? 320 : Math.Min(constraint.Width, 2000);
        return new WpfSize(w, 64);   // taller for legend above + ticks below
    }

    // ---- zoom-aware layout ----
    // Baseline: windowed layout. At _zoom==1 the window spans every step, so x is uniform.
    // When _zoom>1 the window shows only the steps around _viewportCenter (half the steps per
    // side at zoom 2, a quarter at zoom 4, …); steps outside the window fall off the edges and
    // ClipToBounds crops them. Dragging pans _viewportCenter through the timeline — the native
    // product behaves the same way (zoom narrows the time window, drag moves it).
    //
    // _viewportCenter is a DOUBLE in step units so slow pans can rest between steps; it is
    // clamped so the window never slides past either end (no dead space beyond the data).
    private double _viewportCenterSteps;   // window center, in step-index units (0 .. n-1)

    private double LayoutWidth(double w) => Math.Max(40, w - 14 - 14);

    private double XPos(int i, double w)
    {
        int n = _snapshot.Length;
        if (n <= 1) return w / 2;
        double usable = LayoutWidth(w);
        if (_zoom <= 1.0)
        {
            _viewportCenterSteps = (n - 1) / 2.0;   // window covers everything
            return 14 + i * (usable / (n - 1));
        }

        double halfWindow = (n - 1) / (2.0 * _zoom);
        double left = _viewportCenterSteps - halfWindow;
        return 14 + (i - left) / (2.0 * halfWindow) * usable;
    }

    /// <summary>Clamp the viewport center so the window never slides past the data.</summary>
    private void ClampViewport(int n)
    {
        if (n <= 1) { _viewportCenterSteps = 0; return; }
        double halfWindow = (n - 1) / (2.0 * _zoom);
        _viewportCenterSteps = Math.Clamp(_viewportCenterSteps, halfWindow, n - 1 - halfWindow);
    }

    // ── Gantt lanes (native parity: the native timeline stacks Input / Model / Tools lanes) ──
    private const int LaneCount = 3;
    private const int LaneInput = 0;   // user / context / system / compacted — input into the model
    private const int LaneModel = 1;   // message (assistant) / todo — model output
    private const int LaneTools = 2;   // tool / subtool — tool execution

    private static readonly string[] LaneLabels = { "Input", "Model", "Tools" };

    /// <summary>Which lane a step kind belongs to, matching the native timeline grouping.</summary>
    private static int LaneForStep(string kind) => kind switch
    {
        "user" or "context" or "system" or "compacted" => LaneInput,
        "tool" or "subtool" => LaneTools,
        _ => LaneModel,   // message (assistant) and the WPF todo extension
    };

    /// <summary>Vertical geometry of the lane area (below the legend, above the time ticks).</summary>
    private static void LaneMetrics(double h, out double top, out double laneH)
    {
        top = 16;
        double bottom = h - 14;                 // leave room for the time-tick row
        laneH = Math.Max(6, (bottom - top) / LaneCount);
    }

    private static double LaneCenterY(int lane, double h)
    {
        LaneMetrics(h, out double top, out double laneH);
        return top + laneH * (lane + 0.5);
    }

    protected override WpfSize ArrangeOverride(WpfSize arrangeBounds)
    {
        // Lay out the (invisible) child hit targets as thin vertical strips spanning the full
        // lane area, so tooltips/clicks work anywhere on a step's column instead of only on a
        // 10px dot. The visible gantt bars are drawn in OnRender.
        double w = arrangeBounds.Width;
        double h = arrangeBounds.Height;
        LaneMetrics(h, out double top, out double laneH);
        for (int i = 0; i < Children.Count && i < _snapshot.Length; i++)
        {
            double x = XPos(i, w);
            SetLeft(Children[i], x - 3);
            SetTop(Children[i], top);
            // Children are Borders created in Rebuild; give them the lane height.
            if (Children[i] is System.Windows.FrameworkElement fe) fe.Height = laneH * LaneCount;
        }
        return arrangeBounds;
    }

    protected override void OnRender(WpfDrawingContext dc)
    {
        double w = ActualWidth > 0 ? ActualWidth : 320;
        double h = ActualHeight > 0 ? ActualHeight : 64;

        // Background fill so the hit-test transparent area paints the timeline surface token color.
        dc.DrawRectangle(Resolve("Surface1"), null, new WpfRect(0, 0, w, h));

        LaneMetrics(h, out double laneTop, out double laneH);

        if (_snapshot.Length == 0)
        {
            var ft = MakeText("No events yet", 11, Resolve("TextMuted"));
            dc.DrawText(ft, new WpfPoint(8, laneTop + 4));
            return;
        }

        // Lane backdrops + labels, so the three bands read as a gantt chart.
        var lanePen = new WpfPen(Resolve("Divider"), 1);
        for (int lane = 0; lane < LaneCount; lane++)
        {
            double y = laneTop + laneH * lane;
            if (lane % 2 == 1)
            {
                dc.DrawRectangle(Resolve("Surface2"), null, new WpfRect(4, y, w - 8, laneH));
            }
            dc.DrawLine(lanePen, new WpfPoint(4, y), new WpfPoint(w - 4, y));
            var lbl = MakeText(LaneLabels[lane], 8, Resolve("TextMuted"));
            dc.DrawText(lbl, new WpfPoint(6, y + 1));
        }

        // Time tick labels (row below the lanes).
        DrawTimeTicks(dc, w, h - 2);

        // Gantt bars: one per step, in its lane, spanning until the next step's x (min 2px so a
        // zero-duration step is still visible). This replaces the old single-baseline dots and
        // matches the native Input/Model/Tools timeline.
        double barH = Math.Max(3, laneH - 4);
        for (int i = 0; i < _snapshot.Length; i++)
        {
            var step = _snapshot[i];
            double x0 = XPos(i, w);
            double x1 = i + 1 < _snapshot.Length ? XPos(i + 1, w) : x0 + 6;
            double barW = Math.Max(2, x1 - x0);
            double y = LaneCenterY(LaneForStep(step.Kind), h) - barH / 2;

            var fill = KindBrush(step.Kind);
            dc.DrawRectangle(fill, null, new WpfRect(x0, y, barW, barH));

            // Error steps get a dashed outline so failures stand out without changing the fill.
            if (step.IsError)
            {
                dc.DrawRectangle(null, new WpfPen(Resolve("Danger"), 1), new WpfRect(x0, y, barW, barH));
            }
        }

        // Hover highlight: a vertical guide across all lanes at the hovered step's x.
        if (_hoverIndex >= 0 && _hoverIndex < _snapshot.Length)
        {
            double hx = XPos(_hoverIndex, w);
            var hoverPen = new WpfPen(Resolve("Accent"), 1) { DashStyle = System.Windows.Media.DashStyles.Dash };
            dc.DrawLine(hoverPen, new WpfPoint(hx, laneTop), new WpfPoint(hx, laneTop + laneH * LaneCount));
            double hy = LaneCenterY(LaneForStep(_snapshot[_hoverIndex].Kind), h);
            dc.DrawRectangle(null, new WpfPen(Resolve("Accent"), 2),
                new WpfRect(hx - 3, hy - barH / 2, 6, barH));
        }

        // Focus highlight: stronger guide plus the zoom badge.
        if (_focusIndex >= 0 && _focusIndex < _snapshot.Length)
        {
            double fx = XPos(_focusIndex, w);
            dc.DrawLine(new WpfPen(Resolve("Accent"), 1.5),
                new WpfPoint(fx, laneTop), new WpfPoint(fx, laneTop + laneH * LaneCount));
            double fy = LaneCenterY(LaneForStep(_snapshot[_focusIndex].Kind), h);
            dc.DrawRectangle(null, new WpfPen(Resolve("Accent"), 2.5),
                new WpfRect(fx - 4, fy - barH / 2 - 1, 8, barH + 2));
            if (_zoom > 1.0)
            {
                var zb = MakeText($"×{_zoom:0.#}", 9, Resolve("Accent"));
                dc.DrawText(zb, new WpfPoint(fx + 6, laneTop - 2));
            }
        }

        // Legend (top-right).
        DrawLegend(dc, w, 6);
    }

    private void DrawTimeTicks(WpfDrawingContext dc, double w, double baselineY)
    {
        if (_snapshot.Length == 0) return;

        // When zoomed, label the focused step's time (the zoom anchor); otherwise first/mid/last.
        int[] indices;
        if (_zoom > 1.0 && _focusIndex >= 0 && _focusIndex < _snapshot.Length)
            indices = new[] { _focusIndex };
        else if (_snapshot.Length == 1) indices = new[] { 0 };
        else if (_snapshot.Length == 2) indices = new[] { 0, 1 };
        else indices = new[] { 0, _snapshot.Length / 2, _snapshot.Length - 1 };

        foreach (int i in indices)
        {
            double x = XPos(i, w);
            var step = _snapshot[i];
            string label = step.Time <= 0 ? "t=0" : $"+{FormatTime(step.Time)}";
            var ft = MakeText(label, 9, Resolve("TextMuted"));
            // baselineY here is the tick row's baseline (passed by OnRender as h - 2), so draw the
            // label ABOVE it rather than below (there is no room under the control).
            dc.DrawText(ft, new WpfPoint(x - ft.Width / 2, baselineY - ft.Height));
            // Tiny tick down.
            dc.DrawLine(new WpfPen(Resolve("Divider"), 1),
                new WpfPoint(x, baselineY), new WpfPoint(x, baselineY + 3));
        }
    }

    private void DrawLegend(WpfDrawingContext dc, double w, double y)
    {
        // Only label the kinds that actually occur, in a stable order. A fixed 4-kind legend
        // overflows the narrow right-hand panel once context/todo steps exist (drawing at a
        // negative x would push it off-screen), so trailing labels are dropped until the row fits.
        // Native-parity kind order (turn removed: it is a grouping level, not a step kind).
        string[] order = { "user", "message", "tool", "subtool", "context", "compacted", "system", "todo" };
        var labels = new System.Collections.Generic.List<string>();
        foreach (var k in order)
        {
            for (int i = 0; i < _snapshot.Length; i++)
            {
                if (_snapshot[i].Kind == k) { labels.Add(k); break; }
            }
        }
        if (labels.Count == 0) return;

        const double gap = 14;
        double avail = w - 12;
        var widths = new System.Collections.Generic.List<double>();
        double total = 0;
        foreach (var lb in labels)
        {
            double wi = Math.Max(22, MeasureText(lb));
            widths.Add(wi);
            total += wi + gap;
        }
        while (labels.Count > 1 && total > avail)
        {
            int last = labels.Count - 1;
            total -= widths[last] + gap;
            labels.RemoveAt(last);
            widths.RemoveAt(last);
        }

        double x = w - total - 6;
        for (int i = 0; i < labels.Count; i++)
        {
            dc.DrawEllipse(KindBrush(labels[i]), null, new WpfPoint(x + 4, y + 6), 3, 3);
            var ft = MakeText(KindLabel(labels[i]), 9, Resolve("TextMuted"));
            dc.DrawText(ft, new WpfPoint(x + 9, y));
            x += widths[i] + gap;
        }
    }

    protected override void OnMouseMove(WpfMouseEventArgs e)
    {
        base.OnMouseMove(e);

        // Pan: convert the pixel delta since the drag started back into step units using the
        // current window width, so the content follows the pointer 1:1 at any zoom level.
        if (_panning)
        {
            double w = ActualWidth > 0 ? ActualWidth : 320;
            double usable = LayoutWidth(w);
            double halfWindow = (_snapshot.Length - 1) / (2.0 * _zoom);
            double stepsPerPixel = (2.0 * halfWindow) / usable;
            double dx = e.GetPosition(this).X - _panStartX;
            _viewportCenterSteps = _panStartCenter - dx * stepsPerPixel;
            ClampViewport(_snapshot.Length);
            InvalidateVisual();
            return;
        }

        if (_dragging)
        {
            // While dragging, the focused step follows the pointer so the user can scrub across
            // the trajectory; the hover ring tracks the same step.
            int idx = HitTestIndex(e.GetPosition(this));
            if (idx >= 0 && idx != _focusIndex)
            {
                // Assign the DP (not the field) so a bound view model observes the change.
                FocusIndex = idx;
                _hoverIndex = idx;
                InvalidateVisual();
            }
        }
        else
        {
            int idx = HitTestIndex(e.GetPosition(this));
            if (idx != _hoverIndex)
            {
                _hoverIndex = idx;
                InvalidateVisual();   // redraw the hover ring
            }
        }
    }

    // ── Pan gesture (middle button drag) ─────────────────────────────────────────────────────
    // The LEFT button keeps its existing meaning (click/drag = select a step). Middle-drag pans
    // the zoomed window, which is the only way to reach steps outside the window once zoomed in
    // (plus Shift+wheel). A drag→steps conversion is computed from the current layout so pan
    // speed matches what is on screen at any zoom level.
    private bool _panning;
    private double _panStartX;
    private double _panStartCenter;

    protected override void OnMouseDown(WpfMouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        // Middle button starts a pan (only meaningful when zoomed in).
        if (e.ChangedButton == WpfMouseButton.Middle && _zoom > 1.0 && _snapshot.Length > 1)
        {
            _panning = true;
            _panStartX = e.GetPosition(this).X;
            _panStartCenter = _viewportCenterSteps;
            CaptureMouse();
            Cursor = WpfCursors.ScrollAll;
            e.Handled = true;
            return;
        }

        _dragging = true;
        CaptureMouse();
        int idx = HitTestIndex(e.GetPosition(this));
        if (idx >= 0)
        {
            FocusIndex = idx;   // setter syncs _focusIndex and invalidates
            _hoverIndex = idx;
            FocusChanged?.Invoke(idx);
            InvalidateVisual();
        }
        e.Handled = true;
    }

    protected override void OnMouseUp(WpfMouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_panning)
        {
            _panning = false;
            ReleaseMouseCapture();
            Cursor = WpfCursors.Cross;
            e.Handled = true;
            return;
        }
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
            FocusChanged?.Invoke(_focusIndex);
        }
        e.Handled = true;
    }

    protected override void OnMouseWheel(WpfMouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_snapshot.Length == 0) return;

        // Shift+wheel pans the window (common convention for horizontal navigation); plain wheel
        // zooms around the current viewport center so the visible region stays put.
        double delta = e.Delta > 0 ? 1.25 : 1.0 / 1.25;
        if ((e.Delta != 0) && IsShiftDown())
        {
            PanBySteps(-(_snapshot.Length - 1) * 0.06 * Math.Sign(e.Delta));
            e.Handled = true;
            return;
        }

        double next = Math.Clamp(_zoom * delta, 1.0, 10.0);
        if (next == _zoom) { e.Handled = true; return; }
        _zoom = next;
        ClampViewport(_snapshot.Length);
        InvalidateVisual();
        ZoomChanged?.Invoke(_zoom);
        e.Handled = true;
    }

    private static bool IsShiftDown()
        => (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;

    /// <summary>Pan the window by the given number of steps (negative = towards earlier steps).</summary>
    private void PanBySteps(double deltaSteps)
    {
        double old = _viewportCenterSteps;
        _viewportCenterSteps += deltaSteps;
        ClampViewport(_snapshot.Length);
        if (Math.Abs(_viewportCenterSteps - old) < 0.001) return;   // clamped at the edge
        InvalidateVisual();
    }

    protected override void OnMouseLeave(WpfMouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex != -1) { _hoverIndex = -1; InvalidateVisual(); }
    }

    /// <summary>Reset zoom + focus (call when a new trajectory is loaded).</summary>
    public void ResetView()
    {
        _zoom = 1.0;
        _viewportCenterSteps = Math.Max(0, (_snapshot.Length - 1) / 2.0);   // re-center the window
        FocusIndex = -1;   // setter syncs _focusIndex and invalidates
        _hoverIndex = -1;
        InvalidateVisual();
    }

    private int HitTestIndex(WpfPoint p)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        LaneMetrics(h, out double laneTop, out double laneH);
        double laneBottom = laneTop + laneH * LaneCount;

        // Outside the lane area there is nothing to hit (keeps the legend row inert).
        if (p.Y < laneTop || p.Y > laneBottom) return -1;

        // Gantt hit-test: find the nearest step by x (the bars tile the whole width, so a plain
        // nearest-x lookup is both simpler and far more forgiving than testing each small dot).
        // This is O(n) per mouse-move with n = number of steps; at a few thousand steps and
        // ~60 mouse-moves/sec that is still trivial (no allocation, no layout).
        int best = -1;
        double bestDist = double.MaxValue;
        for (int i = 0; i < _snapshot.Length; i++)
        {
            double d = Math.Abs(p.X - XPos(i, w));
            if (d < bestDist) { bestDist = d; best = i; }
        }
        // Require the pointer to be reasonably close; otherwise hovering empty space far from any
        // step would still report one.
        return bestDist <= 8 ? best : -1;
    }

    // ---- helpers ----

    private WpfFormattedText MakeText(string s, double size, WpfBrush fill) =>
        new(s, CultureInfo.CurrentUICulture, WpfFlowDirection.LeftToRight,
            new WpfTypeface("Segoe UI"), size, fill, 1.0);

    private double MeasureText(string s)
    {
        var ft = new WpfFormattedText(s, CultureInfo.CurrentUICulture, WpfFlowDirection.LeftToRight,
            new WpfTypeface("Segoe UI"), 9, WpfBrushes.Black, 1.0);
        return ft.WidthIncludingTrailingWhitespace;
    }

    private static WpfBrush Resolve(string key) =>
        WpfApplication.Current.TryFindResource(key) is WpfBrush b ? b : WpfBrushes.Gray;

    /// <summary>Native-parity kind → brush. Kinds mirror the native-web ui-trajectory ledger:
    /// user / message (assistant) / tool / context / compacted / system / subtool, plus the WPF
    /// extension `todo`. Note `turn` is NOT a kind: the native product treats it as a grouping
    /// level carried by <see cref="SessionFold.TrajectoryStep.TurnIndex"/>.</summary>
    private static WpfBrush KindBrush(string kind) => kind switch
    {
        "user" => Resolve("InfoText"),
        "message" => Resolve("TextPrimary"),   // assistant output (native label: ASSISTANT)
        "tool" => Resolve("WarningText"),
        "subtool" => Resolve("WarningText"),
        "context" => Resolve("Success"),       // injected input / config change
        "compacted" => Resolve("Success"),     // context compaction
        "system" => Resolve("Accent"),
        "todo" => Resolve("TextMuted"),        // WPF extension: todo/write snapshots
        _ => Resolve("TextSecondary"),
    };

    /// <summary>Native display label for a kind (the native ledger upper-cases these).</summary>
    private static string KindLabel(string kind) => kind switch
    {
        "message" => "ASSISTANT",
        "subtool" => "SUBTOOL",
        _ => kind.ToUpperInvariant(),
    };
}