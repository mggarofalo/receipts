using FluentAssertions;
using Infrastructure.Services;

namespace Infrastructure.Tests.Services;

[Trait("Category", "Unit")]
public class EmbeddingSpaceFingerprintTests
{
	// Expected to fail on every Microsoft.ML.OnnxRuntime or Microsoft.ML.Tokenizers bump, and on any
	// model, vocabulary, token-limit, pooling or normalization change. That failure is the point: each
	// one makes every stored vector stale and starts a full rebuild (docs/semantic-vector-rebuild.md).
	// Update the string only as a deliberate decision to rebuild, and say so in the PR.
	[Fact]
	public void EmbeddingSpaceFingerprint_IsPinned()
	{
		const string expected =
			"model=bge-large-en-v1.5"
			+ "|revision=d4aa6901d3a41ba39fb536a557fa166f842b0e09"
			+ "|model-sha256=69ed3f810d3b6d13f70dff9ca89966f39c0a0e877fb88211be7bcc070df2a2ce"
			+ "|onnx-runtime=1.24.3.0"
			+ "|tokenizer=Microsoft.ML.Tokenizers@1.0.0.0"
			+ "|vocab-sha256=07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3"
			+ "|max-tokens=512"
			+ "|pooling=CLS"
			+ "|normalization=l2-v1";

		string actual = OnnxEmbeddingService.EmbeddingSpaceFingerprint;

		actual.Should().Be(expected);
	}
}
