using System.Collections;
using System.Collections.Generic;

namespace RecursiveAI.OneModelContext;

/// <summary>
/// Everything a full listing returned (all pages), as a read-only list that also says whether the client answered from memory.
/// </summary>
/// <typeparam name="T">The listed item: a tool, resource, resource template or prompt.</typeparam>
public sealed class ListedItems<T> : IReadOnlyList<T>
{
  private readonly List<T> _items;

  internal ListedItems(List<T> items, bool fromCache)
  {
    _items = items;
    FromCache = fromCache;
  }

  /// <summary>
  /// True when every page came from the client's cache because the server's <c>ttlMs</c> had not run out, so no request was sent. False
  /// as soon as one page was fetched, and always false for servers that send no <c>ttlMs</c>.
  /// </summary>
  public bool FromCache { get; }

  /// <inheritdoc />
  public int Count => _items.Count;

  /// <inheritdoc />
  public T this[int index] => _items[index];

  /// <inheritdoc />
  public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

  IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
