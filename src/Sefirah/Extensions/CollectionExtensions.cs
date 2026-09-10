using System.Collections.ObjectModel;

namespace Sefirah.Extensions;

/// <summary>
/// Collection helpers previously provided by Uno.Extensions.Specialized.
/// </summary>
public static class CollectionExtensions
{
#if !HAS_UNO
    public static ObservableCollection<T> ToObservableCollection<T>(this IEnumerable<T> source) => [.. source];
#endif

    public static void AddRange<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }

    public static void SynchronizeByKey<T, TKey>(
        this ObservableCollection<T> collection,
        IEnumerable<T> items,
        Func<T, TKey> keySelector,
        Func<T, T, bool>? areEquivalent = null,
        IEqualityComparer<TKey>? keyComparer = null)
    {
        var desired = items.ToList();
        keyComparer ??= EqualityComparer<TKey>.Default;
        areEquivalent ??= EqualityComparer<T>.Default.Equals;

        for (var targetIndex = 0; targetIndex < desired.Count; targetIndex++)
        {
            var desiredItem = desired[targetIndex];
            var desiredKey = keySelector(desiredItem);
            var currentIndex = -1;
            for (var index = targetIndex; index < collection.Count; index++)
            {
                if (keyComparer.Equals(keySelector(collection[index]), desiredKey))
                {
                    currentIndex = index;
                    break;
                }
            }

            if (currentIndex < 0)
            {
                collection.Insert(targetIndex, desiredItem);
            }
            else
            {
                if (currentIndex != targetIndex)
                {
                    collection.Move(currentIndex, targetIndex);
                }

                if (!areEquivalent(collection[targetIndex], desiredItem))
                {
                    collection[targetIndex] = desiredItem;
                }
            }
        }

        while (collection.Count > desired.Count)
        {
            collection.RemoveAt(collection.Count - 1);
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
