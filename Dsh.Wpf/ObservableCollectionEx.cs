using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Dsh.Wpf;

/// <summary>
/// <see cref="ObservableCollection{T}"/> with a batch <see cref="ReplaceAll"/> that swaps in a new
/// item set while raising exactly ONE <see cref="NotifyCollectionChangedAction.Reset"/> notification
/// (instead of one CollectionChanged per row).
///
/// Why this matters: for a 600-row transcript window, the original per-item Clear+Add pattern fired
/// 600 CollectionChanged events per re-sync — each forcing a WPF layout pass. A single Reset makes
/// the UI re-render the (virtualized) window once. Crucially this MUTATES the same collection
/// instance rather than reassigning the view-model property, so it never raises the VM's
/// INotifyPropertyChanged.PropertyChanged off the UI thread (which would deadlock/freeze WPF
/// bindings during a background history fold).
/// </summary>
public sealed class ObservableCollectionEx<T> : ObservableCollection<T>
{
    private bool _suppress;

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_suppress) return;
        base.OnCollectionChanged(e);
    }

    /// <summary>
    /// Replace the entire contents with <paramref name="items"/>, raising a single Reset
    /// notification. The per-item mutations are performed while CollectionChanged is suppressed.
    /// </summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        _suppress = true;
        try
        {
            Clear();
            foreach (var item in items)
            {
                Add(item);
            }
        }
        finally
        {
            _suppress = false;
        }
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
