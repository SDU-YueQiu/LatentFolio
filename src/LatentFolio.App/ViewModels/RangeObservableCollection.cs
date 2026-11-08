using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace LatentFolio.App.ViewModels;

/// <summary>批量替换时不刷一堆通知，只发一次 Reset。</summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    private bool _bulk;

    public void ReplaceAll(IEnumerable<T> items)
    {
        _bulk = true;
        try
        {
            Items.Clear();
            foreach (var i in items) Items.Add(i);
        }
        finally
        {
            _bulk = false;
        }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_bulk) base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_bulk || e.PropertyName == nameof(Count)) base.OnPropertyChanged(e);
    }
}
