using System;
using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Dsh.Wpf;

/// <summary>
/// Attached behavior that auto-scrolls an ItemsControl to the last item whenever its
/// ItemsSource collection reports a new add. Used on the chat Transcript so streaming
/// chunks and historical-loads both stay pinned to the bottom.
/// Behavior is opt-in: set <c>autoScrollBehavior:AutoScrollBehavior.ScrollToEnd="True"</c>
/// on the ListBox/ItemsControl. ItemsSource may be set after the behavior is attached;
/// the behavior subscribes to whatever ItemsSource is current and re-subscribes on changes.
/// </summary>
public static class AutoScrollBehavior
{
    public static readonly DependencyProperty ScrollToEndProperty =
        DependencyProperty.RegisterAttached(
            "ScrollToEnd", typeof(bool), typeof(AutoScrollBehavior),
            new PropertyMetadata(false, OnScrollToEndChanged));

    public static void SetScrollToEnd(DependencyObject d, bool v) => d.SetValue(ScrollToEndProperty, v);
    public static bool GetScrollToEnd(DependencyObject d) => (bool)d.GetValue(ScrollToEndProperty);

    // PauseScrollToEnd: while true, collection changes do NOT auto-scroll. Used during a history
    // load that renders in batches — suppressing per-batch scrolling avoids the "content fills in
    // and jumps to bottom repeatedly" animation. When it flips back to false we pin to the end once,
    // so the load lands on the latest message. Streaming (which keeps it false) is unaffected.
    public static readonly DependencyProperty PauseScrollToEndProperty =
        DependencyProperty.RegisterAttached(
            "PauseScrollToEnd", typeof(bool), typeof(AutoScrollBehavior),
            new PropertyMetadata(false, OnPauseScrollToEndChanged));

    public static void SetPauseScrollToEnd(DependencyObject d, bool v) => d.SetValue(PauseScrollToEndProperty, v);
    public static bool GetPauseScrollToEnd(DependencyObject d) => (bool)d.GetValue(PauseScrollToEndProperty);

    private static void OnPauseScrollToEndChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Resume (true→false): pin to the latest once — the load is done, land at the bottom.
        if (d is ItemsControl ic && (bool)e.NewValue is false && (bool)e.OldValue is true)
        {
            ScrollToEnd(ic);
        }
    }

    private static readonly DependencyProperty ItemsSourceProxyProperty =
        DependencyProperty.RegisterAttached(
            "ItemsSourceProxy", typeof(object), typeof(AutoScrollBehavior),
            new PropertyMetadata(null, OnItemsSourceProxyChanged));

    // Hook ItemsSource changes (the public DP cannot be subscribed directly) by re-routing
    // through a private proxy and the corresponding changed callback.
    private static readonly DependencyProperty HostItemsControlProperty =
        DependencyProperty.RegisterAttached(
            "HostItemsControl", typeof(ItemsControl), typeof(AutoScrollBehavior),
            new PropertyMetadata(null));

    private static void OnScrollToEndChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl ic) return;
        if ((bool)e.NewValue)
        {
            // Track ItemsControl and re-subscribe on every ItemsSource change.
            ic.SetValue(HostItemsControlProperty, ic);
            // Subscribe ScrollChanged to track whether the user is at the bottom. The default
            // (always scroll on Add) yanks the view back to the latest item every flush, so a user
            // who tried to read older content while streaming is fought by the autoscroll on every
            // 250 ms SyncFoldToUi and ends up looking at "just above" the bottom — the symptom that
            // showed up as "the last few items are below the viewport, the ScrollBar says it's at
            // the bottom, and scrolling down does nothing."
            ic.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChangedForAutoScroll));
            var binding = new System.Windows.Data.Binding("ItemsSource")
            {
                Source = ic,
                Mode = System.Windows.Data.BindingMode.OneWay,
            };
            ic.SetBinding(ItemsSourceProxyProperty, binding);
        }
        else
        {
            ic.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChangedForAutoScroll));
            ic.ClearValue(ItemsSourceProxyProperty);
            ic.ClearValue(HostItemsControlProperty);
            Detach(ic);
        }
    }

    private static void OnItemsSourceProxyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl ic) return;
        Detach(ic);
        if (e.NewValue is INotifyCollectionChanged ncc)
        {
            // Keep a named handler so we can actually unsubscribe (N1 leak fix): a bare lambda
            // can't be `-=`, so it would stay attached to the collection forever, pinning the
            // ItemsControl and firing pointless ScrollToEnd no-ops after detach.
            NotifyCollectionChangedEventHandler handler = (_, ev) => OnCollectionChanged(ic, ev);
            ncc.CollectionChanged += handler;
            _attached[ic] = new Subscription(ncc, handler);
            // The user is "at the bottom" by default at first attach: the items were just
            // rendered, the scrollbar is at the top, but the typical use case is "I want to
            // follow the new content", so we treat the freshly-populated list as bottom-aligned.
            // As soon as the user scrolls up, OnScrollChangedForAutoScroll will flip this false.
            _isAtBottom[ic] = true;
            // After attaching, also scroll to the current end (covers initial binding of a
            // pre-populated transcript loaded from history).
            ScrollToEnd(ic);
        }
    }

    private static void OnCollectionChanged(ItemsControl ic, NotifyCollectionChangedEventArgs ev)
    {
        // While a history load is paused (see PauseScrollToEnd), suppress per-batch scrolling so
        // the view doesn't repeatedly jump to the bottom as batches are appended.
        if (GetPauseScrollToEnd(ic)) return;
        // Only follow the tail when the user has actually been at the bottom — otherwise every
        // streaming flush would yank a reader who scrolled up back to the latest item, AND the
        // repeated ScrollIntoView on the very last item is exactly what left a few items below
        // the viewport in the screenshot: ScrollIntoView on the last item races with the
        // VirtualizingStackPanel re-realize, so the offset lands a few pixels short and the bar
        // sits at the bottom visually without showing the last content.
        if (ev.Action != NotifyCollectionChangedAction.Add &&
            ev.Action != NotifyCollectionChangedAction.Reset) return;
        if (!IsAtBottom(ic)) return;
        ScrollToEnd(ic);
    }

    /// <summary>
    /// True when the user's viewport offset is at (or within one item of) the extent end — i.e.
    /// "the last row is fully visible or would be the next one revealed by scrolling down".
    /// <para>
    /// Scrolling is considered "at the bottom" only when the user has not moved the bar away,
    /// because the previous behaviour (always scroll on Add) overrode the user: the streaming
    /// 250 ms trigger pulled them back to the latest every time, leaving a few items below the
    /// viewport (the symptom shown in the 2026-08-31 screenshot).
    /// </para>
    /// </summary>
    private static bool IsAtBottom(ItemsControl ic)
    {
        var scroll = FindScrollViewer(ic);
        if (scroll is null) return true;   // no viewport: nothing to track, let it scroll
        // A tolerance of a couple of pixels absorbs floating-point d rounding when the user
        // *just* reached the bottom with the mouse wheel; without it, the very last pixel
        // counts as "not at the bottom" and the next ScrollIntoView is the one that fails.
        const double Tolerance = 2.0;
        return scroll.VerticalOffset + scroll.ViewportHeight >= scroll.ExtentHeight - Tolerance;
    }

    /// <summary>
    /// Walk the visual tree to find the inner ScrollViewer. ItemsControl does not expose it, so
    /// the only way to read VerticalOffset / ExtentHeight is via the template part. WPF's
    /// ScrollViewer has a known ItemsControl.ScrollViewer attached property in newer SDKs but
    /// not always; this helper is robust.
    /// </summary>
    private static ScrollViewer? FindScrollViewer(ItemsControl ic)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(ic); i++)
        {
            if (VisualTreeHelper.GetChild(ic, i) is ScrollViewer sv) return sv;
        }
        // Fall back to a depth-first search (some templates put a Border / ScrollContentPresenter
        // before the ScrollViewer).
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(ic); i++)
        {
            if (VisualTreeHelper.GetChild(ic, i) is DependencyObject d)
            {
                if (d is ScrollViewer direct) return direct;
                if (d is System.Windows.Media.Visual v)
                {
                    var found = FindScrollViewerDescendant(v);
                    if (found is not null) return found;
                }
            }
        }
        return null;
    }

    private static ScrollViewer? FindScrollViewerDescendant(System.Windows.Media.Visual v)
    {
        int count = VisualTreeHelper.GetChildrenCount(v);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(v, i);
            if (child is ScrollViewer sv) return sv;
            if (child is System.Windows.Media.Visual vc)
            {
                var nested = FindScrollViewerDescendant(vc);
                if (nested is not null) return nested;
            }
        }
        return null;
    }

    /// <summary>
    /// Keep <see cref="_isAtBottom"/> honest on every user-driven scroll. Mouse-wheel / scrollbar
    /// events arrive here even during streaming; we do NOT want to keep autoscroll on just
    /// because no collection change happened recently.
    /// </summary>
    private static void OnScrollChangedForAutoScroll(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ItemsControl ic) return;
        // Only user-initiated scrolls update the flag; collection-driven ScrollChanged events fire
        // here too, and would set the flag from the position the auto-scroll just brought us to.
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0) return;
        _isAtBottom[ic] = IsAtBottom(ic);
    }

    /// <summary>
    /// Per-ItemsControl cache of the most recent user-driven bottom state. We cannot infer it
    /// from <c>VerticalOffset</c> alone on every scroll, because ScrollChanged also fires when
    /// the auto-scroll itself moved the bar. The simplest correct one: this snapshot is updated
    /// only when the user-driven HorizontalChange is zero AND ExtentHeight/ViewportHeight are
    /// unchanged — i.e. only a finger/wheel/arrow initiated the scroll.
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<ItemsControl, bool> _isAtBottom = new();

    private static void ScrollToEnd(ItemsControl ic)
    {
        if (ic.Items.Count == 0) return;
        var last = ic.Items[ic.Items.Count - 1];

        // ScrollIntoView(last) on a Recycling virtualizing stack panel races the container
        // re-realize and lands a few pixels short of ExtentHeight, so the last row sits below
        // the viewport even though the ScrollBar reports "fully scrolled". Two-pass strategy:
        //   1. ScrollIntoView at low priority — forces the last container to be realized so
        //      ExtentHeight becomes accurate.
        //   2. Then explicitly set VerticalOffset = ExtentHeight - ViewportHeight on the
        //      ScrollViewer at lower priority. By now the realized container is part of the
        //      extent, so the math is exact.
        // Defer both passes so the new item has been processed by the virtualization pass
        // before we measure the viewport.
        ic.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ic is System.Windows.Controls.ListBox lb) lb.ScrollIntoView(last);
            else if (ic is System.Windows.Controls.DataGrid dg) dg.ScrollIntoView(last, dg.Columns[0]);

            // NOTE (2026-08-31): a second pass that forced
// ScrollToVerticalOffset(ExtentHeight - ViewportHeight) was REMOVED. Pinning the offset to the
// extreme end of the extent makes the recycling panel release the entire visible window and
// re-realize it at the tail; combined with per-control render state that was not reset on
// container reuse, the viewport came back empty ("scroll to the bottom and nothing renders").
// The real fix for the missing tail was resetting that per-control state on DataContextChanged
// (see AssistantMessageControl); ScrollIntoView alone is sufficient and far safer here.
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Pairs the source collection with the (named) handler we subscribed, so it can
    /// be unsubscribed on detach (N1 leak fix).</summary>
    private sealed record Subscription(INotifyCollectionChanged Source, NotifyCollectionChangedEventHandler Handler);

    private static readonly Dictionary<ItemsControl, Subscription> _attached = new();

    private static void Detach(ItemsControl ic)
    {
        if (_attached.TryGetValue(ic, out var sub))
        {
            sub.Source.CollectionChanged -= sub.Handler;
            _attached.Remove(ic);
        }
        // Per-control cache cleanup; otherwise a detached ItemsControl that gets re-attached
        // would start with the bottom state from its previous lifetime, which may no longer be
        // a real bottom state in its new host (different parent / different size / etc.).
        _isAtBottom.Remove(ic);
    }
}