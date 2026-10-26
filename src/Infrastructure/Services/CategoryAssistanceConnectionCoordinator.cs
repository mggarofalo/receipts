using System.Security.Cryptography;
using System.Text;
using Infrastructure.Entities.CategoryAssistance;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

/// <summary>
/// Deployment-local binding for the selected assistance transport. A transport calls
/// ReconcileAsync after loading its live configuration and IsCurrentAsync immediately
/// before a send. Neither operation sends a request to the provider.
/// </summary>
public sealed class CategoryAssistanceConnectionCoordinator(IDbContextFactory<ApplicationDbContext> factory)
{
	public sealed record Configuration(string Provider, string Model, string Credential,
		Uri Server, string TransportOptions);

	private static string Fingerprint(Configuration configuration)
	{
		if (string.IsNullOrWhiteSpace(configuration.Provider) || string.IsNullOrWhiteSpace(configuration.Model)
			|| string.IsNullOrWhiteSpace(configuration.Credential) || !configuration.Server.IsAbsoluteUri)
		{
			throw new ArgumentException("A complete assistance transport configuration is required.");
		}
		// Length-prefixed fields avoid ambiguous concatenations. Only the digest is persisted.
		using var hash = SHA256.Create();
		foreach (string value in new[] { configuration.Provider, configuration.Model,
			configuration.Credential, configuration.Server.AbsoluteUri, configuration.TransportOptions })
		{
			byte[] bytes = Encoding.UTF8.GetBytes(value);
			byte[] length = BitConverter.GetBytes(bytes.Length);
			hash.TransformBlock(length, 0, length.Length, null, 0);
			hash.TransformBlock(bytes, 0, bytes.Length, null, 0);
		}
		hash.TransformFinalBlock([], 0, 0);
		return Convert.ToHexString(hash.Hash!);
	}

	public async Task<Guid> ReconcileAsync(Configuration configuration, CancellationToken ct = default)
	{
		string fingerprint = Fingerprint(configuration);
		await using var db = await factory.CreateDbContextAsync(ct);
		await using var tx = await db.Database.BeginTransactionAsync(ct);
		await LockHeadAsync(db, ct);
		var row = await db.CategoryAssistanceConnection.FromSqlRaw(
			"SELECT * FROM category_assistance.\"Connection\" WHERE \"Id\"=1 FOR UPDATE").SingleAsync(ct);
		if (row.Changing || row.Generation == Guid.Empty || row.Provider != configuration.Provider
			|| row.Model != configuration.Model || row.ConfigurationFingerprint != fingerprint)
		{
			row.Generation = Guid.NewGuid();
			row.Provider = configuration.Provider;
			row.Model = configuration.Model;
			row.ConfigurationFingerprint = fingerprint;
			row.Changing = false;
			await db.SaveChangesAsync(ct);
		}
		await tx.CommitAsync(ct);
		return row.Generation;
	}

	public async Task InvalidateAsync(CancellationToken ct = default)
	{
		await using var db = await factory.CreateDbContextAsync(ct);
		await using var tx = await db.Database.BeginTransactionAsync(ct);
		await LockHeadAsync(db, ct);
		var row = await db.CategoryAssistanceConnection.FromSqlRaw(
			"SELECT * FROM category_assistance.\"Connection\" WHERE \"Id\"=1 FOR UPDATE").SingleAsync(ct);
		row.Generation = Guid.NewGuid();
		row.Changing = true;
		row.ConfigurationFingerprint = "";
		await db.SaveChangesAsync(ct);
		await tx.CommitAsync(ct);
	}

	public async Task<bool> IsCurrentAsync(Configuration configuration, Guid generation, CancellationToken ct = default)
	{
		if (generation == Guid.Empty)
		{
			return false;
		}

		string fingerprint = Fingerprint(configuration);
		await using var db = await factory.CreateDbContextAsync(ct);
		return await db.CategoryAssistanceConnection.AsNoTracking().AnyAsync(row => row.Id == 1
			&& !row.Changing && row.Generation == generation
			&& row.Provider == configuration.Provider && row.Model == configuration.Model
			&& row.ConfigurationFingerprint == fingerprint, ct);
	}

	private static async Task LockHeadAsync(ApplicationDbContext db, CancellationToken ct)
		=> await db.TaxonomyHeads.FromSqlRaw("SELECT * FROM taxonomy.\"Head\" WHERE \"Id\"=1 FOR UPDATE").SingleAsync(ct);
}
