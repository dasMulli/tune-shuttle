// Port of core/src/dealer/maps.rs.
//
// Both maps are tries keyed by uri components (see Dealer.SplitUri). Upstream passes the remaining path as a
// consumed-by-recursion iterator; here it is an IEnumerator<string> advanced with MoveNext.
namespace Librespot.Core.Dealer;

/// <summary>Upstream <c>HandlerMapError</c> (kind Aborted).</summary>
internal sealed class HandlerMapException : LibrespotException
{
    public enum Reason
    {
        AlreadyHandled,
    }

    public Reason Cause { get; }

    private HandlerMapException(Reason cause, string detail) : base(ErrorKind.Aborted, detail)
    {
        Cause = cause;
    }

    public static HandlerMapException AlreadyHandled() => new(Reason.AlreadyHandled, "request was already handled");
}

/// <summary>
/// Upstream <c>HandlerMap&lt;T&gt;</c>: either a leaf holding exactly one handler or a branch of children. A handler
/// registered for a path handles every uri below it; a path can only be handled once.
/// </summary>
internal sealed class HandlerMap<T> where T : class
{
    // Leaf when _leaf is not null, otherwise Branch(_children).
    private T? _leaf;
    private readonly Dictionary<string, HandlerMap<T>> _children = new(StringComparer.Ordinal);

    private bool IsLeaf => _leaf is not null;

    /// <summary>
    /// Upstream <c>contains(path)</c>: true when this node is a branch with a direct child named
    /// <paramref name="path"/> (upstream compares the whole string against the first level only).
    /// </summary>
    public bool Contains(string path) => !IsLeaf && _children.ContainsKey(path);

    /// <summary>Upstream <c>insert(path, handler)</c>; throws <see cref="HandlerMapException"/> when already handled.</summary>
    public void Insert(IEnumerable<string> path, T handler)
    {
        using var enumerator = path.GetEnumerator();
        Insert(enumerator, handler);
    }

    private void Insert(IEnumerator<string> path, T handler)
    {
        if (IsLeaf)
        {
            throw HandlerMapException.AlreadyHandled();
        }

        if (path.MoveNext())
        {
            if (!_children.TryGetValue(path.Current, out var node))
            {
                node = new HandlerMap<T>();
                _children.Add(path.Current, node);
            }

            node.Insert(path, handler);
        }
        else if (_children.Count == 0)
        {
            _leaf = handler;
        }
        else
        {
            throw HandlerMapException.AlreadyHandled();
        }
    }

    /// <summary>Upstream <c>get(path)</c>: the handler of the first leaf on the path, if any.</summary>
    public T? Get(IEnumerable<string> path)
    {
        using var enumerator = path.GetEnumerator();
        return Get(enumerator);
    }

    private T? Get(IEnumerator<string> path)
    {
        if (_leaf is not null)
        {
            return _leaf;
        }

        if (!path.MoveNext())
        {
            return null;
        }

        return _children.TryGetValue(path.Current, out var child) ? child.Get(path) : null;
    }

    /// <summary>Upstream <c>remove(path)</c>: removes and returns the handler of the first leaf on the path.</summary>
    public T? Remove(IEnumerable<string> path)
    {
        using var enumerator = path.GetEnumerator();
        return Remove(enumerator);
    }

    private T? Remove(IEnumerator<string> path)
    {
        if (_leaf is not null)
        {
            // upstream: std::mem::take(self) leaves an empty branch behind
            var t = _leaf;
            _leaf = null;
            return t;
        }

        if (!path.MoveNext())
        {
            return null;
        }

        var component = path.Current;
        if (!_children.TryGetValue(component, out var next))
        {
            return null;
        }

        var result = next.Remove(path);
        if (!next.IsLeaf && next._children.Count == 0)
        {
            _children.Remove(component);
        }

        return result;
    }

    /// <summary>All handlers in the map (used to release them when the dealer goes away, upstream <c>Drop</c>).</summary>
    public IEnumerable<T> Values()
    {
        if (_leaf is not null)
        {
            yield return _leaf;
            yield break;
        }

        foreach (var child in _children.Values)
        {
            foreach (var value in child.Values())
            {
                yield return value;
            }
        }
    }
}

/// <summary>
/// Upstream <c>SubscriberMap&lt;T&gt;</c>: any number of subscribers per node; a subscriber of a path receives the
/// messages of every uri below it.
/// </summary>
internal sealed class SubscriberMap<T>
{
    private readonly List<T> _subscribed = [];
    private readonly Dictionary<string, SubscriberMap<T>> _children = new(StringComparer.Ordinal);

    /// <summary>Upstream <c>insert(path, handler)</c>.</summary>
    public void Insert(IEnumerable<string> path, T handler)
    {
        using var enumerator = path.GetEnumerator();
        Insert(enumerator, handler);
    }

    private void Insert(IEnumerator<string> path, T handler)
    {
        if (path.MoveNext())
        {
            if (!_children.TryGetValue(path.Current, out var node))
            {
                node = new SubscriberMap<T>();
                _children.Add(path.Current, node);
            }

            node.Insert(path, handler);
        }
        else
        {
            _subscribed.Add(handler);
        }
    }

    /// <summary>Upstream <c>contains(path)</c>: true when a subscriber on the path (or below its end) exists.</summary>
    public bool Contains(IEnumerable<string> path)
    {
        using var enumerator = path.GetEnumerator();
        return Contains(enumerator);
    }

    private bool Contains(IEnumerator<string> path)
    {
        if (_subscribed.Count != 0)
        {
            return true;
        }

        if (path.MoveNext())
        {
            if (_children.TryGetValue(path.Current, out var nextMap))
            {
                return nextMap.Contains(path);
            }
        }
        else
        {
            return !IsEmpty;
        }

        return false;
    }

    /// <summary>Upstream <c>is_empty()</c>.</summary>
    public bool IsEmpty => _children.Count == 0 && _subscribed.Count == 0;

    /// <summary>
    /// Upstream <c>retain(path, fun)</c>: calls <paramref name="fun"/> for every subscriber on the path, drops the
    /// subscribers for which it returns false and prunes empty nodes. Returns whether any subscriber was called.
    /// </summary>
    public bool Retain(IEnumerable<string> path, Func<T, bool> fun)
    {
        using var enumerator = path.GetEnumerator();
        return Retain(enumerator, fun);
    }

    private bool Retain(IEnumerator<string> path, Func<T, bool> fun)
    {
        var handledByAny = false;
        _subscribed.RemoveAll(x =>
        {
            handledByAny = true;
            return !fun(x);
        });

        if (path.MoveNext())
        {
            var next = path.Current;
            if (_children.TryGetValue(next, out var y))
            {
                // upstream: `handled_by_any = handled_by_any || y.retain(path, fun)` - short-circuits, so deeper
                // subscribers are not visited once a subscriber on a shorter prefix took the message.
                handledByAny = handledByAny || y.Retain(path, fun);
                if (y.IsEmpty)
                {
                    _children.Remove(next);
                }
            }
        }

        return handledByAny;
    }

    /// <summary>All subscribers in the map (used to release them when the dealer goes away, upstream <c>Drop</c>).</summary>
    public IEnumerable<T> Values()
    {
        foreach (var value in _subscribed)
        {
            yield return value;
        }

        foreach (var child in _children.Values)
        {
            foreach (var value in child.Values())
            {
                yield return value;
            }
        }
    }
}
