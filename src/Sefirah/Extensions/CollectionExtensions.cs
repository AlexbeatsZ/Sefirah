// System
namespace Sefirah.Extensions;

/// <summary>
/// Collection helpers previously provided by Uno.Extensions.Specialized.
/// </summary>
public static class CollectionExtensions
{
    public static ObservableCollection<T> ToObservableCollection<T>(this IEnumerable<T> source) => [.. source];

    public static void AddRange<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }

    public static void AddRange<T>(this HashSet<T> set, IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            set.Add(item);
        }
    }
}
