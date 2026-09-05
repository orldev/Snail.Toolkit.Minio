using System.Diagnostics.CodeAnalysis;

namespace Snail.Toolkit.Minio.Domain;

/// <summary>
/// The outcome of an operation that returns nothing but can fail.
/// </summary>
/// <remarks>
/// Failure is data here, not an exception: everything the caller can act on — a missing bucket, rejected
/// credentials, an unreachable server — comes back as <see cref="Error"/>. Exceptions are left for what a
/// caller cannot act on.
/// </remarks>
public sealed record StorageResult
{
    private StorageResult(StorageError? error) => Error = error;

    /// <summary>Gets the failure, or <see langword="null"/> when the operation succeeded.</summary>
    public StorageError? Error { get; }

    /// <summary>Gets a value indicating whether the operation succeeded.</summary>
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    /// <summary>Reports a successful operation.</summary>
    /// <returns>A successful result.</returns>
    public static StorageResult Success() => new(error: null);

    /// <summary>Reports a failed operation.</summary>
    /// <param name="error">Why the operation failed.</param>
    /// <returns>A failed result.</returns>
    public static StorageResult Failure(StorageError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return new StorageResult(error);
    }
}

/// <summary>
/// The outcome of an operation that produces a value.
/// </summary>
/// <typeparam name="T">The type produced by a successful operation.</typeparam>
/// <remarks>
/// A successful result always carries a value: <see cref="Success"/> refuses <see langword="null"/>, so
/// success and "there is something to read" are the same fact and callers never have to test both.
/// </remarks>
public sealed record StorageResult<T> where T : notnull
{
    private readonly T? _value;

    private StorageResult(T? value, StorageError? error)
    {
        _value = value;
        Error = error;
    }

    /// <summary>Gets the failure, or <see langword="null"/> when the operation succeeded.</summary>
    public StorageError? Error { get; }

    /// <summary>Gets a value indicating whether the operation succeeded.</summary>
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    /// <summary>Gets the value produced by a successful operation.</summary>
    /// <exception cref="InvalidOperationException">The operation failed.</exception>
    /// <remarks>
    /// Reading this on a failed result is a mistake in the calling code rather than a runtime condition, so
    /// it throws instead of handing back a default. Use <see cref="TryGetValue"/> where the outcome is open.
    /// </remarks>
    public T Value => _value ?? throw new InvalidOperationException(
        $"The operation failed and carries no value. {Error}");

    /// <summary>Reads the value of a successful result.</summary>
    /// <param name="value">The value, when the operation succeeded.</param>
    /// <returns><see langword="true"/> when the operation succeeded.</returns>
    public bool TryGetValue([NotNullWhen(true)] out T? value)
    {
        value = _value;

        return _value is not null;
    }

    /// <summary>Reports a successful operation.</summary>
    /// <param name="value">The value produced.</param>
    /// <returns>A successful result carrying <paramref name="value"/>.</returns>
    public static StorageResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return new StorageResult<T>(value, null);
    }

    /// <summary>Reports a failed operation.</summary>
    /// <param name="error">Why the operation failed.</param>
    /// <returns>A failed result.</returns>
    public static StorageResult<T> Failure(StorageError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return new StorageResult<T>(default, error);
    }
}
