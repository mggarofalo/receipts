using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;

namespace Infrastructure.Services;

internal sealed class EmbeddingInferenceQueue : IDisposable
{
	internal const string MeterName = "Receipts.Embeddings";
	private const int RequestBurstLimit = 4;
	private static readonly Meter Meter = new(MeterName);
	private static readonly UpDownCounter<long> QueueDepth = Meter.CreateUpDownCounter<long>(
		"receipts.embedding.queue.depth",
		description: "Embedding work waiting for the single inference session.");
	private static readonly Histogram<double> QueueWait = Meter.CreateHistogram<double>(
		"receipts.embedding.queue.wait",
		unit: "ms",
		description: "Time embedding work waits before inference starts.");
	private static readonly Histogram<double> InferenceDuration = Meter.CreateHistogram<double>(
		"receipts.embedding.inference.duration",
		unit: "ms",
		description: "Synchronous ONNX inference duration.");
	private static readonly Counter<long> Cancelled = Meter.CreateCounter<long>(
		"receipts.embedding.queue.cancelled",
		description: "Queued embedding work cancelled before inference.");
	private static readonly Counter<long> Rejected = Meter.CreateCounter<long>(
		"receipts.embedding.queue.rejected",
		description: "Embedding work rejected because its bounded lane is full.");

	private readonly Channel<WorkItem> _requests;
	private readonly Channel<WorkItem> _background;
	private readonly Func<string, float[]> _infer;
	private readonly CancellationTokenSource _shutdown = new();
	private readonly SemaphoreSlim _available = new(0);
	private readonly object _lifecycleLock = new();
	private readonly Task _processor;
	private int _disposed;

	public EmbeddingInferenceQueue(
		Func<string, float[]> infer,
		int requestCapacity,
		int backgroundCapacity)
	{
		_infer = infer;
		_requests = CreateChannel(requestCapacity);
		_background = CreateChannel(backgroundCapacity);
		_processor = Task.Factory.StartNew(
			Process,
			CancellationToken.None,
			TaskCreationOptions.LongRunning,
			TaskScheduler.Default);
	}

	public Task<float[]> EnqueueRequestAsync(string text, CancellationToken cancellationToken) =>
		EnqueueAsync(_requests, text, "request", cancellationToken);

	public Task<float[]> EnqueueBackgroundAsync(string text, CancellationToken cancellationToken) =>
		EnqueueAsync(_background, text, "background", cancellationToken);

	private static Channel<WorkItem> CreateChannel(int capacity) => Channel.CreateBounded<WorkItem>(
		new BoundedChannelOptions(Math.Max(1, capacity))
		{
			SingleReader = true,
			SingleWriter = false,
			FullMode = BoundedChannelFullMode.Wait,
			AllowSynchronousContinuations = false,
		});

	private Task<float[]> EnqueueAsync(
		Channel<WorkItem> channel,
		string text,
		string priority,
		CancellationToken cancellationToken)
	{
		lock (_lifecycleLock)
		{
			ObjectDisposedException.ThrowIf(_disposed != 0, this);
			cancellationToken.ThrowIfCancellationRequested();

			WorkItem item = new(text, priority, cancellationToken);
			KeyValuePair<string, object?> priorityTag = new("priority", priority);
			QueueDepth.Add(1, priorityTag);
			if (!channel.Writer.TryWrite(item))
			{
				QueueDepth.Add(-1, priorityTag);
				item.Dispose();
				if (cancellationToken.IsCancellationRequested)
				{
					return Task.FromCanceled<float[]>(cancellationToken);
				}

				Rejected.Add(1, priorityTag);
				return Task.FromException<float[]>(new EmbeddingQueueFullException(priority));
			}

			_available.Release();
			return item.Completion.Task;
		}
	}

	private void Process()
	{
		int requestBurst = 0;
		try
		{
			while (!_shutdown.IsCancellationRequested)
			{
				_available.Wait(_shutdown.Token);

				WorkItem? item = null;
				if (requestBurst < RequestBurstLimit && _requests.Reader.TryRead(out item))
				{
					requestBurst++;
				}
				else if (_background.Reader.TryRead(out item))
				{
					requestBurst = 0;
				}
				else if (_requests.Reader.TryRead(out item))
				{
					requestBurst = 1;
				}

				Debug.Assert(item is not null, "The availability semaphore must correspond to a queued item.");

				QueueDepth.Add(-1, new KeyValuePair<string, object?>("priority", item.Priority));
				if (item.CancellationToken.IsCancellationRequested || item.Completion.Task.IsCanceled)
				{
					Cancelled.Add(1, new KeyValuePair<string, object?>("priority", item.Priority));
					item.Completion.TrySetCanceled(item.CancellationToken);
					item.Dispose();
					continue;
				}

				QueueWait.Record(
					Stopwatch.GetElapsedTime(item.EnqueuedTimestamp).TotalMilliseconds,
					new KeyValuePair<string, object?>("priority", item.Priority));
				long started = Stopwatch.GetTimestamp();
				try
				{
					float[] result = _infer(item.Text);
					item.Completion.TrySetResult(result);
				}
				catch (Exception ex)
				{
					item.Completion.TrySetException(ex);
				}
				finally
				{
					InferenceDuration.Record(
						Stopwatch.GetElapsedTime(started).TotalMilliseconds,
						new KeyValuePair<string, object?>("priority", item.Priority));
					item.Dispose();
				}
			}
		}
		catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
		{
		}
		finally
		{
			CancelPending(_requests.Reader);
			CancelPending(_background.Reader);
		}
	}

	private static void CancelPending(ChannelReader<WorkItem> reader)
	{
		while (reader.TryRead(out WorkItem? item))
		{
			QueueDepth.Add(-1, new KeyValuePair<string, object?>("priority", item.Priority));
			item.Completion.TrySetCanceled();
			item.Dispose();
		}
	}

	public void Dispose()
	{
		lock (_lifecycleLock)
		{
			if (_disposed != 0)
			{
				return;
			}

			_disposed = 1;
			_requests.Writer.TryComplete();
			_background.Writer.TryComplete();
			_shutdown.Cancel();
		}

		try
		{
			_processor.GetAwaiter().GetResult();
		}
		catch (OperationCanceledException)
		{
		}

		_shutdown.Dispose();
		_available.Dispose();
	}

	private sealed class WorkItem : IDisposable
	{
		private readonly CancellationTokenRegistration _cancellationRegistration;

		public WorkItem(string text, string priority, CancellationToken cancellationToken)
		{
			Text = text;
			Priority = priority;
			CancellationToken = cancellationToken;
			EnqueuedTimestamp = Stopwatch.GetTimestamp();
			Completion = new TaskCompletionSource<float[]>(TaskCreationOptions.RunContinuationsAsynchronously);
			_cancellationRegistration = cancellationToken.Register(
				static state =>
				{
					WorkItem item = (WorkItem)state!;
					item.Completion.TrySetCanceled(item.CancellationToken);
				},
				this);
		}

		public string Text { get; }
		public string Priority { get; }
		public CancellationToken CancellationToken { get; }
		public long EnqueuedTimestamp { get; }
		public TaskCompletionSource<float[]> Completion { get; }
		public void Dispose() => _cancellationRegistration.Dispose();
	}
}

internal sealed class EmbeddingQueueFullException(string priority)
	: InvalidOperationException($"The bounded embedding {priority} queue is full.");
