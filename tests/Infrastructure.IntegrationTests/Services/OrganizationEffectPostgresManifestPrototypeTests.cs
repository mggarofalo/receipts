using System.Text;
using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Npgsql;
using NpgsqlTypes;
using static Infrastructure.Services.OrganizationEffectContractV1;

namespace Infrastructure.IntegrationTests.Services;

// Offline, session-local projection experiment; not a capture witness or production contract.
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class OrganizationEffectPostgresManifestPrototypeTests(PostgresFixture server)
{
	[Fact]
	public async Task StoredTypedRowsMatchFullV1BytesAndDomainSeparatedHash()
	{
		await using NpgsqlConnection connection = new(server.ConnectionString);
		await connection.OpenAsync();
		await using (NpgsqlCommand setup = new(OrganizationEffectPostgresStringPrototypeTests.PrototypeSql + ProjectionSql, connection))
		{
			await setup.ExecuteNonQueryAsync();
		}
		ManifestRequest baseline = Example();
		List<ManifestRequest> variants = [baseline, baseline with { Members = [] }, baseline with { Members = baseline.Members.Reverse().ToArray() }];
		// Changes remain valid v1 requests, and independently exercise each projected field.
		variants.Add(Context(baseline, actor: "other actor"));
		variants.Add(Context(baseline, policy: "policy/2"));
		variants.Add(Context(baseline, manifest: Guid.NewGuid()));
		variants.Add(Context(baseline, taxonomy: Guid.NewGuid()));
		variants.Add(Context(baseline, captured: baseline.CapturedAt.AddTicks(10)));
		variants.Add(Context(baseline, captured: DateTimeOffset.MinValue));
		variants.Add(Context(baseline, captured: new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(9999990)));
		ItemRequest item = baseline.Members[0];
		ItemRequest[] mutations =
		[
			item with { Source = item.Source with { ReceiptId = Guid.NewGuid() } },
			item with { Source = item.Source with { ItemId = Guid.NewGuid() } },
			item with { Source = item.Source with { Store = "different store" } },
			item with { Source = item.Source with { RawDescription = "changed description" } },
			item with { Source = item.Source with { ItemCode = "" } },
			item with { Source = item.Source with { ReceiptRevision = Guid.NewGuid() } },
			item with { Source = item.Source with { ItemRevision = Guid.NewGuid() } },
			item with { ExpectedAssignmentId = Guid.NewGuid() },
			item with { Target = item.Target with { Path = [.. item.Target.Path, Guid.NewGuid()] } },
			item with { ApplyOperationId = Guid.NewGuid() },
			item with { UndoOperationId = Guid.NewGuid() },
			item with { Decision = item.Decision with { MappingRevisionId = Guid.NewGuid() } },
			item with { Decision = item.Decision with { ApprovalTaxonomyRevisionId = Guid.NewGuid() } },
			item with { Decision = item.Decision with { SourceFingerprint = new string('B', 64) } }
		];
		Guid changedCategory = Guid.NewGuid();
		variants.Add(baseline with { Members = [item with { Target = item.Target with { CategoryId = changedCategory, Path = [changedCategory] } }, baseline.Members[1]] });
		variants.AddRange(mutations.Select(changed => baseline with { Members = [changed, baseline.Members[1]] }));
		foreach (ManifestRequest request in variants)
		{
			await StoreAsync(connection, request);
			await AssertProjectionAsync(connection, request);
			if (request != baseline)
			{
				ManifestRequestHash(request).Should().NotBe(ManifestRequestHash(baseline));
			}
		}
		// A database-side edit must change the projection even when no new request/digest is supplied.
		await StoreAsync(connection, baseline);
		await using (NpgsqlCommand edit = new("UPDATE pg_temp.prototype_member SET store = 'database mutation' WHERE ordinal = 0", connection))
		{
			await edit.ExecuteNonQueryAsync();
		}
		await AssertProjectionAsync(connection, baseline with { Members = [item with { Source = item.Source with { Store = "database mutation" } }, baseline.Members[1]] });
		await StoreAsync(connection, baseline);
		await using (NpgsqlCommand reorder = new("UPDATE pg_temp.prototype_member SET ordinal = ordinal + 10; UPDATE pg_temp.prototype_member SET ordinal = 11 - ordinal", connection))
		{
			await reorder.ExecuteNonQueryAsync();
		}
		await AssertProjectionAsync(connection, baseline with { Members = baseline.Members.Reverse().ToArray() });
		await using (NpgsqlCommand remove = new("DELETE FROM pg_temp.prototype_member WHERE ordinal = 0", connection))
		{
			await remove.ExecuteNonQueryAsync();
		}
		// Exact bytes for the rows that remain say nothing about whether a member was omitted.
		await AssertProjectionAsync(connection, baseline with { Members = [item] });
	}

	private static async Task AssertProjectionAsync(NpgsqlConnection connection, ManifestRequest expected)
	{
		await using NpgsqlCommand command = new("SELECT bytes, upper(encode(sha256(convert_to('receipts.organization-effect.manifest-request.v1', 'UTF8') || decode('00','hex') || bytes), 'hex')) FROM (SELECT pg_temp.effect_v1_manifest() AS bytes) p", connection);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		(await reader.ReadAsync()).Should().BeTrue();
		reader.GetFieldValue<byte[]>(0).Should().Equal(Encoding.UTF8.GetBytes(WriteManifestRequest(expected)));
		reader.GetString(1).Should().Be(ManifestRequestHash(expected));
		(await reader.ReadAsync()).Should().BeFalse();
	}

	private static ManifestRequest Context(ManifestRequest value, string? actor = null, string? policy = null,
		Guid? manifest = null, Guid? taxonomy = null, DateTimeOffset? captured = null)
	{
		ManifestRequest result = value with
		{
			Actor = actor ?? value.Actor,
			PolicyVersion = policy ?? value.PolicyVersion,
			ManifestId = manifest ?? value.ManifestId,
			TaxonomyRevisionId = taxonomy ?? value.TaxonomyRevisionId,
			CapturedAt = captured ?? value.CapturedAt
		};
		return result with
		{
			Members = result.Members.Select(m => m with
			{
				Actor = result.Actor,
				PolicyVersion = result.PolicyVersion,
				ManifestId = result.ManifestId,
				CapturedAt = result.CapturedAt,
				Target = m.Target with { TaxonomyRevisionId = result.TaxonomyRevisionId }
			}).ToArray()
		};
	}

	private static ManifestRequest Example()
	{
		Guid manifest = Guid.NewGuid(), taxonomy = Guid.NewGuid(), category = Guid.NewGuid(), root = Guid.NewGuid();
		DateTimeOffset timestamp = new DateTimeOffset(2026, 10, 28, 12, 34, 56, TimeSpan.Zero).AddTicks(1234560);
		ItemRequest first = new(1, manifest, "operator <&> é 😀", "inactive/v1", timestamp,
			new(Guid.NewGuid(), Guid.NewGuid(), "café e\u0301", "\"\\\b\t\n\f\r\u0001\u001f\u007f 中文 😀\u2028\uffff", null, Guid.NewGuid(), Guid.NewGuid()),
			null, new(category, taxonomy, [category, root]), Guid.NewGuid(), Guid.NewGuid(),
			new(1, "approved-exact-mapping", Guid.NewGuid(), Guid.NewGuid(), new string('A', 64)));
		ItemRequest second = first with
		{
			Source = first.Source with { ReceiptId = Guid.NewGuid(), ItemId = Guid.NewGuid(), Store = "", RawDescription = "second", ItemCode = "零+'`" },
			ExpectedAssignmentId = Guid.NewGuid(),
			ApplyOperationId = Guid.NewGuid(),
			UndoOperationId = Guid.NewGuid(),
			Target = new(root, taxonomy, [root])
		};
		return new(1, manifest, first.Actor, first.PolicyVersion, taxonomy, timestamp, [first, second]);
	}

	private static async Task StoreAsync(NpgsqlConnection connection, ManifestRequest request)
	{
		await using (NpgsqlCommand clear = new("TRUNCATE pg_temp.prototype_header, pg_temp.prototype_member", connection)) { await clear.ExecuteNonQueryAsync(); }
		await using (NpgsqlCommand header = new("INSERT INTO pg_temp.prototype_header VALUES (@version,@id,@actor,@policy,@taxonomy,@captured::timestamptz)", connection))
		{
			header.Parameters.AddWithValue("version", request.Version);
			header.Parameters.AddWithValue("id", request.ManifestId);
			header.Parameters.AddWithValue("actor", request.Actor);
			header.Parameters.AddWithValue("policy", request.PolicyVersion);
			header.Parameters.AddWithValue("taxonomy", request.TaxonomyRevisionId);
			// Explicit finite input avoids Npgsql's MinValue => -infinity sentinel conversion.
			header.Parameters.AddWithValue("captured", request.CapturedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", System.Globalization.CultureInfo.InvariantCulture));
			await header.ExecuteNonQueryAsync();
		}
		// Deliberately insert in reverse physical order; only persisted ordinal defines vector order.
		for (int ordinal = request.Members.Length - 1; ordinal >= 0; ordinal--)
		{
			ItemRequest m = request.Members[ordinal];
			await using NpgsqlCommand insert = new("INSERT INTO pg_temp.prototype_member VALUES (@ordinal,@version,@receipt,@item,@store,@raw,@code,@receipt_rev,@item_rev,@expected,@category,@path,@apply,@undo,@decision_version,@kind,@mapping,@approval,@fingerprint)", connection);
			insert.Parameters.AddWithValue("ordinal", ordinal);
			insert.Parameters.AddWithValue("version", m.Version);
			insert.Parameters.AddWithValue("receipt", m.Source.ReceiptId);
			insert.Parameters.AddWithValue("item", m.Source.ItemId);
			insert.Parameters.AddWithValue("store", m.Source.Store);
			insert.Parameters.AddWithValue("raw", m.Source.RawDescription);
			insert.Parameters.AddWithValue("code", NpgsqlDbType.Text, (object?)m.Source.ItemCode ?? DBNull.Value);
			insert.Parameters.AddWithValue("receipt_rev", m.Source.ReceiptRevision);
			insert.Parameters.AddWithValue("item_rev", m.Source.ItemRevision);
			insert.Parameters.AddWithValue("expected", NpgsqlDbType.Uuid, (object?)m.ExpectedAssignmentId ?? DBNull.Value);
			insert.Parameters.AddWithValue("category", m.Target.CategoryId!.Value);
			insert.Parameters.AddWithValue("path", m.Target.Path);
			insert.Parameters.AddWithValue("apply", m.ApplyOperationId);
			insert.Parameters.AddWithValue("undo", m.UndoOperationId);
			insert.Parameters.AddWithValue("decision_version", m.Decision.Version);
			insert.Parameters.AddWithValue("kind", m.Decision.Kind);
			insert.Parameters.AddWithValue("mapping", m.Decision.MappingRevisionId);
			insert.Parameters.AddWithValue("approval", m.Decision.ApprovalTaxonomyRevisionId);
			insert.Parameters.AddWithValue("fingerprint", m.Decision.SourceFingerprint);
			await insert.ExecuteNonQueryAsync();
		}
	}

	internal const string ProjectionSql = """
		SET TIME ZONE 'Pacific/Honolulu';
		CREATE TEMP TABLE prototype_header(version integer NOT NULL, id uuid PRIMARY KEY, actor text NOT NULL,
			policy text NOT NULL, taxonomy uuid NOT NULL, captured timestamptz NOT NULL);
		CREATE TEMP TABLE prototype_member(ordinal integer PRIMARY KEY, version integer NOT NULL,
			receipt uuid NOT NULL, item uuid NOT NULL, store text NOT NULL, raw text NOT NULL, code text,
			receipt_rev uuid NOT NULL, item_rev uuid NOT NULL, expected uuid, category uuid NOT NULL, path uuid[] NOT NULL,
			apply uuid NOT NULL, undo uuid NOT NULL, decision_version integer NOT NULL, kind text NOT NULL,
			mapping uuid NOT NULL, approval uuid NOT NULL, fingerprint text NOT NULL);
		CREATE FUNCTION pg_temp.q(value text) RETURNS text LANGUAGE SQL IMMUTABLE AS $$
			SELECT coalesce(convert_from(pg_temp.effect_v1_string(value), 'UTF8'), 'null')
		$$;
		CREATE FUNCTION pg_temp.obj(VARIADIC pairs text[]) RETURNS text LANGUAGE SQL IMMUTABLE AS $$
			SELECT '{' || string_agg(pg_temp.q(pairs[i]) || ':' || pairs[i+1], ',' ORDER BY pairs[i] COLLATE "C") || '}'
			FROM generate_series(1, cardinality(pairs), 2) i
		$$;
		CREATE FUNCTION pg_temp.effect_v1_manifest() RETURNS bytea LANGUAGE SQL STABLE AS $$
			SELECT convert_to(pg_temp.obj(
				'Version', h.version::text, 'ManifestId', pg_temp.q(h.id::text), 'Actor', pg_temp.q(h.actor),
				'PolicyVersion', pg_temp.q(h.policy), 'TaxonomyRevisionId', pg_temp.q(h.taxonomy::text),
				'CapturedAt', pg_temp.q(to_char(h.captured AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"')),
				'Members', (SELECT '[' || coalesce(string_agg(pg_temp.obj(
					'Version', m.version::text, 'ManifestId', pg_temp.q(h.id::text), 'Actor', pg_temp.q(h.actor),
					'PolicyVersion', pg_temp.q(h.policy),
					'CapturedAt', pg_temp.q(to_char(h.captured AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"')),
					'Source', pg_temp.obj('ReceiptId', pg_temp.q(m.receipt::text), 'ItemId', pg_temp.q(m.item::text),
						'Store', pg_temp.q(m.store), 'RawDescription', pg_temp.q(m.raw), 'ItemCode', pg_temp.q(m.code),
						'ReceiptRevision', pg_temp.q(m.receipt_rev::text), 'ItemRevision', pg_temp.q(m.item_rev::text)),
					'ExpectedAssignmentId', pg_temp.q(m.expected::text),
					'Target', pg_temp.obj('CategoryId', pg_temp.q(m.category::text), 'TaxonomyRevisionId', pg_temp.q(h.taxonomy::text),
						'Path', (SELECT '[' || coalesce(string_agg(pg_temp.q(p::text), ',' ORDER BY n), '') || ']' FROM unnest(m.path) WITH ORDINALITY a(p,n))),
					'ApplyOperationId', pg_temp.q(m.apply::text), 'UndoOperationId', pg_temp.q(m.undo::text),
					'Decision', pg_temp.obj('Version', m.decision_version::text, 'Kind', pg_temp.q(m.kind),
						'MappingRevisionId', pg_temp.q(m.mapping::text), 'ApprovalTaxonomyRevisionId', pg_temp.q(m.approval::text),
						'SourceFingerprint', pg_temp.q(m.fingerprint))
				), ',' ORDER BY m.ordinal), '') || ']' FROM pg_temp.prototype_member m)
			), 'UTF8') FROM pg_temp.prototype_header h
		$$;
		""";
}
