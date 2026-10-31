using System.Security.Cryptography;

namespace Receipts.JevProductionParity;

/// <summary>One recorded provider exchange, bound to the exact request bytes it answered.</summary>
public sealed record ReplayExchange(string Body, int StatusCode = 200, string? RequestSha256 = null);

public static class Hashing
{
	public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
