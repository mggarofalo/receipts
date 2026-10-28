using System.Text;
using System.Text.Json;
using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

// Deliberately session-local research: no migration, persisted function or production caller.
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class OrganizationEffectPostgresStringPrototypeTests(PostgresFixture server)
{
	[Fact]
	public async Task EveryPostgresUnicodeScalarMatchesActualDefaultUtf8JsonWriterBytes()
	{
		await using NpgsqlConnection connection = await OpenPrototypeAsync();
		await using NpgsqlCommand command = new("""
			SELECT codepoint, pg_temp.effect_v1_string(chr(codepoint))
			FROM generate_series(1, 1114111) AS codepoint
			WHERE codepoint NOT BETWEEN 55296 AND 57343
			ORDER BY codepoint
			""", connection) { CommandTimeout = 120 };
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		int count = 0;
		while (await reader.ReadAsync())
		{
			int scalar = reader.GetInt32(0);
			byte[] expected = WriteString(char.ConvertFromUtf32(scalar));
			byte[] actual = reader.GetFieldValue<byte[]>(1);
			Assert.True(expected.AsSpan().SequenceEqual(actual), $"Encoding differs at U+{scalar:X}");
			count++;
		}
		count.Should().Be(1114111 - 2048);
	}

	[Theory]
	[InlineData("")]
	[InlineData("plain ASCII and spaces")]
	[InlineData("\"\\\b\t\n\f\r\u0001\u001f\u007f")]
	[InlineData("<script> & ' + ` = / >")]
	[InlineData("caf\u00e9 e\u0301 \u2028\u2029 \u4e2d \ufffe\uffff")]
	[InlineData("a\U0001F600\U00010000\U0010FFFFz")]
	public async Task MixedStringsMatchWriterAndTheActualV1Canonicalizer(string value)
	{
		await using NpgsqlConnection connection = await OpenPrototypeAsync();
		await using NpgsqlCommand command = new("SELECT pg_temp.effect_v1_string(@value)", connection);
		command.Parameters.AddWithValue("value", value);
		byte[] actual = (byte[])(await command.ExecuteScalarAsync())!;
		actual.Should().Equal(WriteString(value));
		string json = "{\"value\":" + Encoding.UTF8.GetString(actual) + "}";
		PortableTaxonomyArchive.CanonicalEffectDecision(json).Should().Be(json);
		using JsonDocument document = JsonDocument.Parse(json);
		document.RootElement.GetProperty("value").GetString().Should().Be(value);
	}

	[Fact]
	public async Task SqlNullRemainsDistinctFromAnEmptyString()
	{
		await using NpgsqlConnection connection = await OpenPrototypeAsync();
		await using NpgsqlCommand command = new("SELECT pg_temp.effect_v1_string(NULL)", connection);
		(await command.ExecuteScalarAsync()).Should().Be(DBNull.Value);
	}

	[Theory]
	[InlineData("SELECT pg_temp.effect_v1_string(chr(0))", "54000")]
	[InlineData("SELECT pg_temp.effect_v1_string(convert_from(decode('00', 'hex'), 'UTF8'))", "22021")]
	[InlineData("SELECT pg_temp.effect_v1_string(convert_from(decode('eda080', 'hex'), 'UTF8'))", "22021")]
	[InlineData("SELECT pg_temp.effect_v1_string(convert_from(decode('edb080', 'hex'), 'UTF8'))", "22021")]
	[InlineData("SELECT pg_temp.effect_v1_string(convert_from(decode('f4908080', 'hex'), 'UTF8'))", "22021")]
	[InlineData("SELECT pg_temp.effect_v1_string(convert_from(decode('c080', 'hex'), 'UTF8'))", "22021")]
	public async Task UnrepresentableTextFailsBeforeSqlEncoding(string sql, string sqlState)
	{
		await using NpgsqlConnection connection = await OpenPrototypeAsync();
		await using NpgsqlCommand command = new(sql, connection);
		Func<Task> act = async () => await command.ExecuteScalarAsync();
		(await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(sqlState);
	}

	[Fact]
	public void V1RejectsManagedTextThatPostgresCannotRepresent()
	{
		foreach (string invalid in new[] { "\0", "\uD800", "\uDC00", "prefix\uD800suffix" })
		{
			Guid category = Guid.NewGuid();
			OrganizationEffectContractV1.ItemRequest request = new(1, Guid.NewGuid(), "actor", "policy",
				new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero),
				new(Guid.NewGuid(), Guid.NewGuid(), "store", invalid, null, Guid.NewGuid(), Guid.NewGuid()),
				null, new(category, Guid.NewGuid(), [category]), Guid.NewGuid(), Guid.NewGuid(),
				new(1, "approved-exact-mapping", Guid.NewGuid(), Guid.NewGuid(), new string('A', 64)));
			FluentActions.Invoking(() => OrganizationEffectContractV1.WriteRequest(request))
				.Should().Throw<InvalidDataException>();
		}
	}

	private static byte[] WriteString(string value)
	{
		using MemoryStream output = new();
		using (Utf8JsonWriter writer = new(output)) { writer.WriteStringValue(value); }
		return output.ToArray();
	}

	private async Task<NpgsqlConnection> OpenPrototypeAsync()
	{
		NpgsqlConnection connection = new(server.ConnectionString);
		try
		{
			await connection.OpenAsync();
			await using NpgsqlCommand command = new(PrototypeSql, connection);
			await command.ExecuteNonQueryAsync();
			return connection;
		}
		catch { await connection.DisposeAsync(); throw; }
	}

	// UTF-8 PostgreSQL text contains Unicode scalars, never NUL or isolated surrogates.
	// Default .NET encoding emits only ASCII, with uppercase UTF-16 escapes outside its allowlist.
	// SQL NULL deliberately stays NULL; the enclosing JSON projection must choose its null token.
	internal const string PrototypeSql = """
		CREATE OR REPLACE FUNCTION pg_temp.effect_v1_string(value text) RETURNS bytea
		LANGUAGE plpgsql IMMUTABLE STRICT AS $body$
		DECLARE
		    result text := '"';
		    character text;
		    codepoint integer;
		    unit integer;
		BEGIN
		    IF current_setting('server_encoding') <> 'UTF8' THEN
		        RAISE EXCEPTION 'effect v1 prototype requires UTF8' USING ERRCODE = '22023';
		    END IF;
		    FOR character IN SELECT regexp_split_to_table(value, '') LOOP
		        CONTINUE WHEN character = '';
		        codepoint := ascii(character);
		        result := result || CASE codepoint
		            WHEN 8 THEN chr(92) || 'b'
		            WHEN 9 THEN chr(92) || 't'
		            WHEN 10 THEN chr(92) || 'n'
		            WHEN 12 THEN chr(92) || 'f'
		            WHEN 13 THEN chr(92) || 'r'
		            WHEN 92 THEN chr(92) || chr(92)
		            ELSE CASE
		                WHEN codepoint BETWEEN 32 AND 126 AND codepoint NOT IN (34, 38, 39, 43, 60, 62, 96)
		                    THEN character
		                WHEN codepoint <= 65535
		                    THEN chr(92) || 'u' || upper(lpad(to_hex(codepoint), 4, '0'))
		                ELSE '' END
		            END;
		        IF codepoint > 65535 THEN
		            unit := codepoint - 65536;
		            result := result || chr(92) || 'u' || upper(to_hex(55296 + (unit / 1024)))
		                             || chr(92) || 'u' || upper(to_hex(56320 + (unit % 1024)));
		        END IF;
		    END LOOP;
		    RETURN convert_to(result || '"', 'UTF8');
		END;
		$body$;
		""";
}
