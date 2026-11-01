namespace Application.Exceptions;

/// <summary>A trend report range that would produce more buckets than a report returns.</summary>
public sealed class ReportRangeTooLargeException(string message) : Exception(message);
