namespace Application.Exceptions;

/// <summary>A requested write was based on state changed by another operation.</summary>
public class ConcurrencyConflictException(string message) : Exception(message)
{
}
