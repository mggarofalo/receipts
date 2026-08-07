using Application.Interfaces.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Infrastructure.Services;

public sealed class OnnxEmbeddingService : IEmbeddingService, IDisposable
{
	public const string ModelName = "bge-large-en-v1.5";
	public const int EmbeddingDimension = 1024;

	// BGE models are trained with CLS-token pooling (see 1_Pooling/config.json in the
	// BAAI/bge-large-en-v1.5 repo). Mean-pooling the token embeddings gives noticeably
	// worse discrimination than using the first token's output.
	public const string PoolingStrategyName = "CLS";

	private const int MaxTokens = 512;

	private readonly string _modelPath;
	private readonly string _vocabPath;
	private readonly ILogger<OnnxEmbeddingService> _logger;
	private readonly object _inferLock = new();

	private LoadedModel? _loaded;
	private bool _disposed;

	public OnnxEmbeddingService(IOptions<EmbeddingModelOptions> options, ILogger<OnnxEmbeddingService> logger)
	{
		_logger = logger;

		string directory = options.Value.ResolveModelDirectory();
		_modelPath = Path.Combine(directory, EmbeddingModelOptions.ModelFileName);
		_vocabPath = Path.Combine(directory, EmbeddingModelOptions.VocabFileName);
	}

	/// <summary>
	/// True once the model has loaded. The model is provisioned onto a volume at runtime
	/// rather than shipped in the image (RECEIPTS-929), so on a fresh deployment this is
	/// false until <see cref="EmbeddingModelProvisioningService"/> finishes the download.
	/// Every caller already guards on this and degrades gracefully.
	/// </summary>
	public bool IsConfigured
	{
		get
		{
			lock (_inferLock)
			{
				return TryLoad() is not null;
			}
		}
	}

	public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken)
	{
		float[] embedding = GenerateEmbedding(text);
		return Task.FromResult(embedding);
	}

	public Task<List<float[]>> GenerateEmbeddingsAsync(List<string> texts, CancellationToken cancellationToken)
	{
		List<float[]> results = new(texts.Count);
		foreach (string text in texts)
		{
			cancellationToken.ThrowIfCancellationRequested();
			results.Add(GenerateEmbedding(text));
		}

		return Task.FromResult(results);
	}

	private float[] GenerateEmbedding(string text)
	{
		// Lock to guarantee thread safety: BertTokenizer's thread-safety is undocumented,
		// and this singleton may be called concurrently from the background service and request pipeline.
		// InferenceSession.Run is thread-safe per ONNX Runtime docs, but we lock the whole method
		// to keep it simple — embedding generation is I/O-bound, not a hot path.
		lock (_inferLock)
		{
			LoadedModel model = TryLoad()
				?? throw new InvalidOperationException(
					$"The ONNX embedding model is not available at {Path.GetDirectoryName(_modelPath)}. " +
					$"Check {nameof(IEmbeddingService)}.{nameof(IsConfigured)} before generating embeddings.");

			return GenerateEmbeddingCore(model, text);
		}
	}

	/// <summary>
	/// Loads the session on first use. Callers must hold <see cref="_inferLock"/>.
	///
	/// A failed load is not latched: the files may simply not have finished downloading yet,
	/// so a later call retries and the app recovers without a restart.
	/// </summary>
	private LoadedModel? TryLoad()
	{
		if (_loaded is not null)
		{
			return _loaded;
		}

		if (_disposed || !File.Exists(_modelPath) || !File.Exists(_vocabPath))
		{
			return null;
		}

		InferenceSession? session = null;
		try
		{
			SessionOptions sessionOptions = new()
			{
				GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
			};

			session = new InferenceSession(_modelPath, sessionOptions);

			using FileStream vocabStream = File.OpenRead(_vocabPath);
			BertTokenizer tokenizer = BertTokenizer.Create(vocabStream);

			_loaded = new LoadedModel(session, tokenizer);
			_logger.LogInformation("Loaded ONNX embedding model {ModelName} from {ModelPath}", ModelName, _modelPath);

			return _loaded;
		}
		catch (Exception ex)
		{
			session?.Dispose();
			_logger.LogError(ex, "Failed to load the ONNX embedding model from {ModelPath}", _modelPath);
			return null;
		}
	}

	private static float[] GenerateEmbeddingCore(LoadedModel model, string text)
	{
		// Tokenize: EncodeToIds with addSpecialTokens=true adds [CLS] and [SEP]
		IReadOnlyList<int> tokenIds = model.Tokenizer.EncodeToIds(text, MaxTokens, out _, out _);

		int seqLen = tokenIds.Count;

		// Build tensors: input_ids, attention_mask (all 1s), token_type_ids (all 0s for single sentence)
		DenseTensor<long> inputIdsTensor = new([1, seqLen]);
		DenseTensor<long> attentionMaskTensor = new([1, seqLen]);
		DenseTensor<long> tokenTypeIdsTensor = new([1, seqLen]);

		for (int i = 0; i < seqLen; i++)
		{
			inputIdsTensor[0, i] = tokenIds[i];
			attentionMaskTensor[0, i] = 1;
			tokenTypeIdsTensor[0, i] = 0;
		}

		List<NamedOnnxValue> inputs =
		[
			NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
			NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
			NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor),
		];

		using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = model.Session.Run(inputs);

		// BGE's ONNX export returns last_hidden_state with shape [1, seq_len, 1024].
		// CLS pooling: take only the [CLS] token (index 0), which is the model's
		// dedicated sentence-representation output.
		DisposableNamedOnnxValue tokenEmbeddings = results.First();
		float[] data = tokenEmbeddings.AsEnumerable<float>().ToArray();

		float[] pooled = new float[EmbeddingDimension];
		Array.Copy(data, 0, pooled, 0, EmbeddingDimension);

		// L2 normalize — required for cosine similarity via dot product.
		float norm = 0;
		for (int j = 0; j < EmbeddingDimension; j++)
		{
			norm += pooled[j] * pooled[j];
		}

		norm = MathF.Sqrt(norm);
		if (norm > 0)
		{
			for (int j = 0; j < EmbeddingDimension; j++)
			{
				pooled[j] /= norm;
			}
		}

		return pooled;
	}

	public void Dispose()
	{
		lock (_inferLock)
		{
			if (_disposed)
			{
				return;
			}

			_loaded?.Session.Dispose();
			_loaded = null;
			_disposed = true;
		}
	}

	private sealed record LoadedModel(InferenceSession Session, BertTokenizer Tokenizer);
}
