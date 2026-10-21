using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Snail.Toolkit.Minio.Adapters;

/// <summary>
/// The few names a caller wrote on one object, each with its value, looked up in any case.
/// </summary>
/// <param name="capacity">The most names it will hold.</param>
/// <remarks>
/// <para>
/// An object carries two or three names, and a listing describes thousands of objects. A dictionary behind a
/// read-only view costs a table, its buckets and the view for each of them; this costs one array of pairs, and
/// comparing two or three names in turn finds one sooner than hashing it ignoring case. Past
/// <see cref="MostNames"/> the callers keep the dictionary, where hashing pays.
/// </para>
/// <para>
/// It answers as that dictionary answered: names in the order they were first written, a name written again in
/// another case keeping its first spelling and taking the later value, and nothing a caller can write through.
/// </para>
/// </remarks>
internal sealed class ObjectMetadata(int capacity) : IReadOnlyDictionary<string, string>
{
    /// <summary>The most names compared in turn rather than hashed.</summary>
    internal const int MostNames = 8;

    private readonly KeyValuePair<string, string>[] _entries = new KeyValuePair<string, string>[capacity];

    private int _count;

    /// <inheritdoc />
    public int Count => _count;

    /// <inheritdoc />
    public IEnumerable<string> Keys
    {
        get
        {
            for (var index = 0; index < _count; index++)
                yield return _entries[index].Key;
        }
    }

    /// <inheritdoc />
    public IEnumerable<string> Values
    {
        get
        {
            for (var index = 0; index < _count; index++)
                yield return _entries[index].Value;
        }
    }

    /// <inheritdoc />
    public string this[string key] => TryGetValue(key, out var value)
        ? value
        : throw new KeyNotFoundException($"The given key '{key}' was not present in the dictionary.");

    /// <summary>
    /// Keeps a name, or gives a name already kept in any case the later value.
    /// </summary>
    /// <param name="key">The name.</param>
    /// <param name="value">Its value.</param>
    /// <returns><see langword="false"/> when a new name finds no room left.</returns>
    internal bool TrySet(string key, string value)
    {
        var index = IndexOf(key);

        if (index >= 0)
        {
            _entries[index] = new KeyValuePair<string, string>(_entries[index].Key, value);

            return true;
        }

        return TryAppend(key, value);
    }

    /// <summary>
    /// Keeps a name that is not kept yet in any case.
    /// </summary>
    /// <param name="key">The name.</param>
    /// <param name="value">Its value.</param>
    /// <returns><see langword="false"/> when the name is already kept, in this case or another, or finds no room left.</returns>
    internal bool TryAdd(string key, string value) => IndexOf(key) < 0 && TryAppend(key, value);

    /// <inheritdoc />
    public bool ContainsKey(string key) => IndexOf(key) >= 0;

    /// <inheritdoc />
    public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value)
    {
        var index = IndexOf(key);

        if (index < 0)
        {
            value = null;

            return false;
        }

        value = _entries[index].Value;

        return true;
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
    {
        for (var index = 0; index < _count; index++)
            yield return _entries[index];
    }

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private bool TryAppend(string key, string value)
    {
        if (_count == _entries.Length)
            return false;

        _entries[_count++] = new KeyValuePair<string, string>(key, value);

        return true;
    }

    private int IndexOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        for (var index = 0; index < _count; index++)
        {
            if (string.Equals(_entries[index].Key, key, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return -1;
    }
}
