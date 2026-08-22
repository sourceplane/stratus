namespace Stratus.BuildingBlocks;

/// <summary>
/// An expected failure is a value, not an exception. Callers must handle it,
/// the type says which failures are possible, and the web layer maps it to a
/// status code without a try/catch pyramid or exceptions used for control flow.
/// </summary>
public readonly record struct Error(string Code, string Message, ErrorKind Kind)
{
    public static Error NotFound(string message) => new("not_found", message, ErrorKind.NotFound);
    public static Error Conflict(string message) => new("conflict", message, ErrorKind.Conflict);
    public static Error Validation(string message) => new("validation", message, ErrorKind.Validation);
}

public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
}

public readonly struct Result<T>
{
    private readonly T? _value;

    private Result(T value)
    {
        _value = value;
        Error = default;
        IsSuccess = true;
    }

    private Result(Error error)
    {
        _value = default;
        Error = error;
        IsSuccess = false;
    }

    public bool IsSuccess { get; }

    public Error Error { get; }

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot read Value from a failed Result.");

    public static Result<T> Success(T value) => new(value);

    public static Result<T> Failure(Error error) => new(error);

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => Failure(error);
}
