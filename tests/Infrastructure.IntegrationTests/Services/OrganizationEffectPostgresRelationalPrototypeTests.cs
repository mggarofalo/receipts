using System.Text;
using System.Text.Json;
using Application.Normalization;
using FluentAssertions;
using Infrastructure.Configurations;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static Infrastructure.Services.OrganizationEffectContractV1;

namespace Infrastructure.IntegrationTests.Services;

// Inactive relational projection only. Ordinary mapping validation remains a prerequisite.
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class OrganizationEffectPostgresRelationalPrototypeTests(PostgresFixture server) : IsolatedPostgresTest(server)
{
	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	public async Task FrozenRowsAndOnlyOrderedIdsProduceExactV1BytesAndHash(int memberCount)
	{
		ManifestRequest expected = await SeedAsync("valid", memberCount);
		await using NpgsqlConnection connection = new(fixture.ConnectionString);
		await connection.OpenAsync();
		await SetupAsync(connection);
		await AssertProjectionAsync(connection, expected);
		ManifestRequest reversed = expected with { Members = expected.Members.Reverse().ToArray() };
		await AssertProjectionAsync(connection, reversed);
		if (memberCount > 1) { ManifestRequestHash(reversed).Should().NotBe(ManifestRequestHash(expected)); }
		// SQL receives no source fields, category path, decision JSON, or digest from this caller.
		foreach (Guid[] ids in new[] {
			Array.Empty<Guid>(), expected.Members.Take(memberCount - 1).Select(m => m.Source.ItemId).ToArray(),
			new[] { expected.Members[0].Source.ItemId, expected.Members[0].Source.ItemId },
			new[] { expected.Members[0].Source.ItemId, Guid.NewGuid() },
			expected.Members.Select(m => m.Source.ItemId).Append(Guid.NewGuid()).ToArray() })
		{
			Func<Task> invalid = () => ProjectAsync(connection, expected.ManifestId, ids);
			(await invalid.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("22023");
		}
		Func<Task> missing = () => ProjectAsync(connection, Guid.NewGuid(), expected.Members.Select(m => m.Source.ItemId).ToArray());
		(await missing.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("22023");
	}

	[Theory]
	[InlineData("decision-kind")]
	[InlineData("decision-version")]
	[InlineData("decision-extra")]
	[InlineData("mapping-missing")]
	[InlineData("mapping-source")]
	[InlineData("mapping-category")]
	[InlineData("mapping-approval")]
	[InlineData("mapping-fingerprint")]
	[InlineData("source-group")]
	[InlineData("archived-ancestor")]
	public async Task InvalidFrozenContextIsRejected(string fault)
	{
		ManifestRequest expected = await SeedAsync(fault);
		await using NpgsqlConnection connection = new(fixture.ConnectionString);
		await connection.OpenAsync();
		await SetupAsync(connection);
		Func<Task> invalid = () => ProjectAsync(connection, expected.ManifestId, expected.Members.Select(m => m.Source.ItemId).ToArray());
		(await invalid.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("22023");
	}

	[Theory]
	[InlineData("missing-ancestor")]
	[InlineData("cycle")]
	[InlineData("operation")]
	[InlineData("taxonomy")]
	[InlineData("revision")]
	public async Task CorruptStoredRelationshipsCannotBeProjected(string fault)
	{
		ManifestRequest expected = await SeedAsync("valid");
		await using NpgsqlConnection connection = new(fixture.ConnectionString);
		await connection.OpenAsync();
		await SetupAsync(connection);
		await using var tx = await connection.BeginTransactionAsync();
		// Deliberate corruption in a private test DB, rolled back. Ordinary immutable/FK
		// guards prohibit these states; bypass them only to exercise projection rejection.
		string change = fault switch
		{
			"missing-ancestor" => "DELETE FROM taxonomy.\"Nodes\" WHERE \"CategoryId\" = @root",
			"cycle" => "UPDATE taxonomy.\"Nodes\" SET \"ParentId\" = @leaf WHERE \"CategoryId\" = @root",
			"operation" => "DELETE FROM taxonomy.\"OrganizationEffectOperations\" WHERE \"OperationId\" = @operation",
			"taxonomy" => "UPDATE taxonomy.\"OrganizationEffectItems\" SET \"TaxonomyRevisionId\" = @genesis",
			_ => "UPDATE taxonomy.\"OrganizationEffectItems\" SET \"ItemRevision\" = '00000000-0000-0000-0000-000000000000'"
		};
		await using (NpgsqlCommand corrupt = new("SET LOCAL session_replication_role = replica; " + change + "; SET LOCAL session_replication_role = origin", connection, tx))
		{
			corrupt.Parameters.AddWithValue("root", expected.Members[0].Target.Path[1]);
			corrupt.Parameters.AddWithValue("leaf", expected.Members[0].Target.CategoryId!.Value);
			corrupt.Parameters.AddWithValue("operation", expected.Members[0].ApplyOperationId);
			corrupt.Parameters.AddWithValue("genesis", TaxonomyConfiguration.GenesisId);
			await corrupt.ExecuteNonQueryAsync();
		}
		Func<Task> invalid = () => ProjectAsync(connection, expected.ManifestId, expected.Members.Select(m => m.Source.ItemId).ToArray());
		(await invalid.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("22023");
		await tx.RollbackAsync();
	}
	[Theory]
	[InlineData("Actor")]
	[InlineData("PolicyVersion")]
	public async Task MetadataWhitespaceMatchesActualDotNetContract(string field)
	{
		ManifestRequest expected = await SeedAsync("valid");
		await using NpgsqlConnection connection = new(fixture.ConnectionString);
		await connection.OpenAsync();
		await SetupAsync(connection);
		// This finite set is checked against the runtime below, rather than assuming
		// PostgreSQL regex/collation or btrim whitespace means .NET whitespace.
		string whitespace = "\u0009\u000a\u000b\u000c\u000d\u0020\u0085\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000";
		new string(Enumerable.Range(0, char.MaxValue + 1).Select(n => (char)n).Where(char.IsWhiteSpace).ToArray())
			.Should().Be(whitespace);
		foreach (string value in whitespace.Select(c => c.ToString()).Append(whitespace).Append(""))
		{
			ManifestRequest invalid = field == "Actor" ? expected with { Actor = value } : expected with { PolicyVersion = value };
			Action codec = () => WriteManifestRequest(invalid);
			codec.Should().Throw<InvalidDataException>();
			await using var tx = await connection.BeginTransactionAsync();
			string column = field == "Actor" ? "Actor" : "PolicyVersion";
			await using (NpgsqlCommand corrupt = new($"SET LOCAL session_replication_role = replica; UPDATE taxonomy.\"OrganizationEffectManifests\" SET \"{column}\" = @value; SET LOCAL session_replication_role = origin", connection, tx))
			{
				corrupt.Parameters.AddWithValue("value", value);
				await corrupt.ExecuteNonQueryAsync();
			}
			Func<Task> project = () => ProjectAsync(connection, expected.ManifestId, expected.Members.Select(m => m.Source.ItemId).ToArray());
			(await project.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("22023");
			await tx.RollbackAsync();
		}
		foreach (string value in new[] { whitespace + "x" + whitespace, "\u200b", "😀" })
		{
			ManifestRequest valid = field == "Actor" ? expected with { Actor = value } : expected with { PolicyVersion = value };
			valid = valid with { Members = valid.Members.Select(m => m with { Actor = valid.Actor, PolicyVersion = valid.PolicyVersion }).ToArray() };
			await using var tx = await connection.BeginTransactionAsync();
			string column = field == "Actor" ? "Actor" : "PolicyVersion";
			await using (NpgsqlCommand change = new($"SET LOCAL session_replication_role = replica; UPDATE taxonomy.\"OrganizationEffectManifests\" SET \"{column}\" = @value; SET LOCAL session_replication_role = origin", connection, tx))
			{
				change.Parameters.AddWithValue("value", value);
				await change.ExecuteNonQueryAsync();
			}
			await AssertProjectionAsync(connection, valid);
			await tx.RollbackAsync();
		}
		// Exhaust every PostgreSQL-representable BMP scalar, independently comparing
		// the SQL predicate to the actual .NET runtime (NUL/surrogates cannot be stored).
		await using NpgsqlCommand oracle = new("SELECT c FROM generate_series(1,65535) c WHERE c NOT BETWEEN 55296 AND 57343 AND NOT pg_temp.effect_v1_metadata_text(chr(c)) ORDER BY c", connection);
		await using NpgsqlDataReader reader = await oracle.ExecuteReaderAsync();
		List<char> rejected = [];
		while (await reader.ReadAsync()) { rejected.Add((char)reader.GetInt32(0)); }
		rejected.Should().Equal(whitespace);
	}
	private async Task<ManifestRequest> SeedAsync(string fault, int memberCount = 2)
	{
		Guid taxonomy = Guid.NewGuid(), root = Guid.NewGuid(), category = Guid.NewGuid(), manifest = Guid.NewGuid();
		Guid mapping = Guid.NewGuid();
		DateTimeOffset captured = new DateTimeOffset(2026, 10, 28, 12, 34, 56, TimeSpan.Zero).AddTicks(1234560);
		string fingerprint = NormalizationCatalogPolicy.Hash(new("café", "Item 😀", null));
		ItemRequest first = new(1, manifest, "operator é", "inactive/v1", captured,
			new(Guid.NewGuid(), Guid.NewGuid(), " café ", " Item 😀 ", " ", Guid.NewGuid(), Guid.NewGuid()),
			null, new(category, taxonomy, [category, root]), Guid.NewGuid(), Guid.NewGuid(),
			new(1, "approved-exact-mapping", mapping, taxonomy, fingerprint));
		ItemRequest second = first with
		{
			Source = first.Source with
			{
				ItemId = Guid.NewGuid(),
				ItemRevision = Guid.NewGuid(),
				RawDescription = fault == "source-group" ? "other" : first.Source.RawDescription
			},
			ApplyOperationId = Guid.NewGuid(),
			UndoOperationId = Guid.NewGuid()
		};
		ManifestRequest result = new(1, manifest, first.Actor, first.PolicyVersion, taxonomy, captured, memberCount == 1 ? [first] : [first, second]);
		await using ApplicationDbContext db = fixture.CreateDbContext();
		await using var tx = await db.Database.BeginTransactionAsync();
		db.TaxonomyIdentities.AddRange(new TaxonomyIdentityEntity { Id = root }, new TaxonomyIdentityEntity { Id = category });
		db.TaxonomyRevisions.Add(new()
		{
			Id = taxonomy,
			Sequence = 1,
			PreviousRevisionId = TaxonomyConfiguration.GenesisId,
			Actor = "test",
			Reason = "relational prototype",
			OperationId = Guid.NewGuid(),
			RecordedAt = captured
		});
		await db.SaveChangesAsync();
		db.TaxonomyNodes.AddRange(new TaxonomyNodeEntity { RevisionId = taxonomy, CategoryId = root, Name = "Root", Archived = fault == "archived-ancestor" },
			new TaxonomyNodeEntity { RevisionId = taxonomy, CategoryId = category, ParentId = root, Name = "Leaf" });
		await db.SaveChangesAsync();
		// These rows assume the existing ordinary mapping validator has already accepted their hashes/semantics.
		db.CategoryMappingRevisions.Add(new()
		{
			Id = mapping,
			SourceFingerprint = fingerprint,
			Location = fault == "mapping-source" ? "other" : "café",
			Description = "Item 😀",
			ItemCode = null,
			CategoryId = fault == "mapping-category" ? root : category,
			TaxonomyRevisionId = taxonomy,
			Actor = "test",
			Reason = "fixture",
			OperationId = Guid.NewGuid(),
			RequestHash = new string('A', 64),
			SemanticFingerprint = new string('B', 64),
			RecordedAt = captured
		});
		db.Add(new OrganizationEffectManifestEntity
		{
			Id = manifest,
			Actor = first.Actor,
			PolicyVersion = first.PolicyVersion,
			TaxonomyRevisionId = taxonomy,
			CapturedAt = captured,
			RequestHash = "not-used-by-projection"
		});
		await db.SaveChangesAsync();
		foreach (ItemRequest member in result.Members.Reverse())
		{
			MappingEvidence decision = member.Decision with
			{
				Version = fault == "decision-version" ? 2 : 1,
				Kind = fault == "decision-kind" ? "other" : "approved-exact-mapping",
				MappingRevisionId = fault == "mapping-missing" ? Guid.NewGuid() : mapping,
				ApprovalTaxonomyRevisionId = fault == "mapping-approval" ? TaxonomyConfiguration.GenesisId : taxonomy,
				SourceFingerprint = fault == "mapping-fingerprint" ? new string('F', 64) : fingerprint
			};
			string json = JsonSerializer.Serialize(decision);
			if (fault == "decision-extra") { json = json[..^1] + ",\"extra\":1}"; }
			db.Add(new OrganizationEffectItemEntity
			{
				ManifestId = manifest,
				ItemId = member.Source.ItemId,
				ReceiptId = member.Source.ReceiptId,
				Store = member.Source.Store,
				RawDescription = member.Source.RawDescription,
				ItemCode = member.Source.ItemCode,
				ReceiptRevision = member.Source.ReceiptRevision,
				ItemRevision = member.Source.ItemRevision,
				TaxonomyRevisionId = taxonomy,
				TargetCategoryId = category,
				ApplyOperationId = member.ApplyOperationId,
				UndoOperationId = member.UndoOperationId,
				DecisionEvidenceJson = json
			});
		}
		await db.SaveChangesAsync();
		await tx.CommitAsync();
		return result;
	}

	private static async Task SetupAsync(NpgsqlConnection connection)
	{
		await using NpgsqlCommand setup = new(OrganizationEffectPostgresStringPrototypeTests.PrototypeSql
			+ OrganizationEffectPostgresManifestPrototypeTests.ProjectionSql + RelationalSql, connection);
		await setup.ExecuteNonQueryAsync();
	}

	private static async Task<byte[]> ProjectAsync(NpgsqlConnection connection, Guid manifest, Guid[] ids)
	{
		await using NpgsqlCommand command = new("SELECT pg_temp.effect_v1_relational(@manifest,@ids)", connection);
		command.Parameters.AddWithValue("manifest", manifest);
		command.Parameters.AddWithValue("ids", ids);
		return (byte[])(await command.ExecuteScalarAsync())!;
	}

	private static async Task AssertProjectionAsync(NpgsqlConnection connection, ManifestRequest expected)
	{
		byte[] bytes = await ProjectAsync(connection, expected.ManifestId, expected.Members.Select(m => m.Source.ItemId).ToArray());
		bytes.Should().Equal(Encoding.UTF8.GetBytes(WriteManifestRequest(expected)));
		await using NpgsqlCommand hash = new("SELECT upper(encode(sha256(convert_to('receipts.organization-effect.manifest-request.v1','UTF8') || decode('00','hex') || @bytes),'hex'))", connection);
		hash.Parameters.AddWithValue("bytes", bytes);
		(await hash.ExecuteScalarAsync()).Should().Be(ManifestRequestHash(expected));
	}

	private const string RelationalSql = """
        -- char.IsWhiteSpace's complete BMP set; verified against the running .NET codec.
        -- Supplementary scalars are never whitespace in .NET and survive translate.
        CREATE FUNCTION pg_temp.effect_v1_metadata_text(value text) RETURNS boolean LANGUAGE SQL IMMUTABLE STRICT AS $$
            SELECT translate(value, U&'\0009\000A\000B\000C\000D\0020\0085\00A0\1680\2000\2001\2002\2003\2004\2005\2006\2007\2008\2009\200A\2028\2029\202F\205F\3000', '') <> ''
        $$;
        CREATE FUNCTION pg_temp.effect_v1_relational(manifest uuid, ids uuid[]) RETURNS bytea LANGUAGE plpgsql AS $$
        DECLARE h taxonomy."OrganizationEffectManifests"%ROWTYPE;
            i taxonomy."OrganizationEffectItems"%ROWTYPE;
            mapping taxonomy."CategoryMappingRevisions"%ROWTYPE;
            node taxonomy."Nodes"%ROWTYPE;
            d jsonb; selected uuid; path uuid[]; n integer := 0; zero uuid := '00000000-0000-0000-0000-000000000000';
        BEGIN
            SELECT * INTO h FROM taxonomy."OrganizationEffectManifests" WHERE "Id" = manifest;
            IF NOT FOUND OR ids IS NULL OR cardinality(ids) < 1 OR array_ndims(ids) <> 1
                OR cardinality(ids) <> (SELECT count(DISTINCT id) FROM unnest(ids) id)
                OR EXISTS (SELECT unnest(ids) EXCEPT SELECT "ItemId" FROM taxonomy."OrganizationEffectItems" WHERE "ManifestId" = manifest)
                OR EXISTS (SELECT "ItemId" FROM taxonomy."OrganizationEffectItems" WHERE "ManifestId" = manifest EXCEPT SELECT unnest(ids))
            THEN RAISE EXCEPTION 'invalid complete ordered item vector' USING ERRCODE = '22023'; END IF;
            IF h."Id" = zero OR h."TaxonomyRevisionId" = zero OR NOT pg_temp.effect_v1_metadata_text(h."Actor") OR NOT pg_temp.effect_v1_metadata_text(h."PolicyVersion")
                OR NOT isfinite(h."CapturedAt") OR extract(year FROM h."CapturedAt" AT TIME ZONE 'UTC') NOT BETWEEN 1 AND 9999
            THEN RAISE EXCEPTION 'invalid manifest context' USING ERRCODE = '22023'; END IF;
            IF (SELECT count(DISTINCT (btrim("Store"), btrim("RawDescription"), nullif(btrim("ItemCode"),'')))
                FROM taxonomy."OrganizationEffectItems" WHERE "ManifestId" = manifest) <> 1
            THEN RAISE EXCEPTION 'multiple source groups' USING ERRCODE = '22023'; END IF;
            TRUNCATE pg_temp.prototype_header, pg_temp.prototype_member;
            INSERT INTO pg_temp.prototype_header VALUES (1,h."Id",h."Actor",h."PolicyVersion",h."TaxonomyRevisionId",h."CapturedAt");
            FOREACH selected IN ARRAY ids LOOP
                SELECT * INTO STRICT i FROM taxonomy."OrganizationEffectItems" WHERE "ManifestId" = manifest AND "ItemId" = selected;
                d := i."DecisionEvidenceJson";
                IF jsonb_typeof(d) <> 'object' OR (SELECT count(*) FROM jsonb_object_keys(d)) <> 5
                    OR NOT d ?& ARRAY['Version','Kind','MappingRevisionId','ApprovalTaxonomyRevisionId','SourceFingerprint']
                    OR d->'Version' IS DISTINCT FROM '1'::jsonb OR d->>'Kind' IS DISTINCT FROM 'approved-exact-mapping'
                    OR jsonb_typeof(d->'MappingRevisionId') <> 'string' OR jsonb_typeof(d->'ApprovalTaxonomyRevisionId') <> 'string'
                    OR jsonb_typeof(d->'SourceFingerprint') <> 'string' OR d->>'SourceFingerprint' !~ '^[0-9A-F]{64}$'
                THEN RAISE EXCEPTION 'invalid decision contract' USING ERRCODE = '22023'; END IF;
                SELECT * INTO mapping FROM taxonomy."CategoryMappingRevisions" WHERE "Id" = (d->>'MappingRevisionId')::uuid;
                IF NOT FOUND OR mapping."Id" = zero OR mapping."CategoryId" IS DISTINCT FROM i."TargetCategoryId"
                    OR mapping."TaxonomyRevisionId" <> (d->>'ApprovalTaxonomyRevisionId')::uuid
                    OR mapping."SourceFingerprint" <> d->>'SourceFingerprint'
                    OR (mapping."Location",mapping."Description",mapping."ItemCode") IS DISTINCT FROM
                        (btrim(i."Store"),btrim(i."RawDescription"),nullif(btrim(i."ItemCode"),''))
                THEN RAISE EXCEPTION 'mapping decision context differs' USING ERRCODE = '22023'; END IF;
                IF i."TaxonomyRevisionId" <> h."TaxonomyRevisionId" OR zero IN (i."ItemId",i."ReceiptId",i."ItemRevision",i."ReceiptRevision",i."ApplyOperationId",i."UndoOperationId")
                    OR i."ExpectedAssignmentId" = zero OR i."ApplyOperationId" = i."UndoOperationId"
                    OR NOT EXISTS (SELECT 1 FROM taxonomy."OrganizationEffectOperations" o WHERE o."OperationId" = i."ApplyOperationId" AND o."ManifestId" = manifest AND o."ItemId" = selected AND o."Phase" = 'apply')
                    OR NOT EXISTS (SELECT 1 FROM taxonomy."OrganizationEffectOperations" o WHERE o."OperationId" = i."UndoOperationId" AND o."ManifestId" = manifest AND o."ItemId" = selected AND o."Phase" = 'undo')
                THEN RAISE EXCEPTION 'invalid item context or operations' USING ERRCODE = '22023'; END IF;
                path := ARRAY[]::uuid[]; selected := i."TargetCategoryId";
                WHILE selected IS NOT NULL LOOP
                    IF selected = zero OR selected = ANY(path) THEN RAISE EXCEPTION 'invalid ancestry' USING ERRCODE = '22023'; END IF;
                    SELECT * INTO node FROM taxonomy."Nodes" WHERE "RevisionId" = h."TaxonomyRevisionId" AND "CategoryId" = selected;
                    IF NOT FOUND OR node."Archived" THEN RAISE EXCEPTION 'missing or archived ancestor' USING ERRCODE = '22023'; END IF;
                    path := array_append(path,selected); selected := node."ParentId";
                END LOOP;
                INSERT INTO pg_temp.prototype_member VALUES (n,1,i."ReceiptId",i."ItemId",i."Store",i."RawDescription",i."ItemCode",
                    i."ReceiptRevision",i."ItemRevision",i."ExpectedAssignmentId",i."TargetCategoryId",path,
                    i."ApplyOperationId",i."UndoOperationId",1,'approved-exact-mapping',mapping."Id",mapping."TaxonomyRevisionId",mapping."SourceFingerprint");
                n := n + 1;
            END LOOP;
            RETURN pg_temp.effect_v1_manifest();
        EXCEPTION WHEN invalid_text_representation THEN
            RAISE EXCEPTION 'invalid decision identity' USING ERRCODE = '22023';
        END $$;
        """;
}
