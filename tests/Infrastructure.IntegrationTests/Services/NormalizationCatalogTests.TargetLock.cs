using Application.Normalization;
using FluentAssertions;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Fact]
	public async Task Approval_RejectsTargetChangedByIndependentSqlWriterWhileWaitingForItsRowLock()
	{
		await ResetAsync();
		var target = await CanonicalAsync("Beef franks");
		var ids = await ItemsAsync(Source, 1);
		var service = Service();
		var preview = await service.PreviewAsync(Actor, Request(target), CancellationToken.None);
		int audits = await AuditCountAsync(ids);
		await using var external = new NpgsqlConnection(fixture.ConnectionString);
		await external.OpenAsync();
		await using var transaction = await external.BeginTransactionAsync();
		await using var reject = new NpgsqlCommand("UPDATE matching.\"NormalizedDescriptions\" SET \"Status\" = 'Rejected' WHERE \"Id\" = @id", external, transaction);
		reject.Parameters.AddWithValue("id", target.Id);
		await reject.ExecuteNonQueryAsync();
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		var approval = service.ApproveAsync(Actor, preview.Id, preview.Revision, deadline.Token);
		bool observed = false;
		try
		{
			await using var waiting = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pg_backend_pid() = ANY(pg_blocking_pids(pid)))", external, transaction);
			while (!approval.IsCompleted && !observed)
			{
				observed = (bool)(await waiting.ExecuteScalarAsync(deadline.Token))!;
				if (!observed)
				{
					await Task.Delay(20, deadline.Token);
				}
			}
			observed.Should().BeTrue("an unguarded external writer must be fenced by the target row lock, not the app-only advisory gate");
		}
		finally { await transaction.CommitAsync(); }
		Func<Task> completion = async () => await approval;
		(await completion.Should().ThrowAsync<NormalizationCatalogException>()).Which.StatusCode.Should().Be(409);
		(await ReadAsync(ids)).Single().NormalizedDescriptionId.Should().BeNull();
		(await service.ListMappingsAsync(Actor, Source, CancellationToken.None)).Should().BeEmpty();
		(await AuditCountAsync(ids)).Should().Be(audits);
	}
}
