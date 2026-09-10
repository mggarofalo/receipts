using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using API.Middleware;
using Application.Exceptions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Presentation.API.Tests.Middleware;

public class GlobalExceptionHandlerMiddlewareTests
{
	private static readonly IProblemDetailsService ProblemDetailsService =
		new ServiceCollection()
			.AddOptions()
			.AddProblemDetails()
			.BuildServiceProvider()
			.GetRequiredService<IProblemDetailsService>();

	private readonly Mock<ILogger<GlobalExceptionHandlerMiddleware>> _loggerMock = new();

	[Fact]
	public async Task InvokeAsync_NoException_PassesThrough()
	{
		// Arrange
		DefaultHttpContext context = new();
		context.Response.Body = new MemoryStream();
		GlobalExceptionHandlerMiddleware middleware = new(
			_ => Task.CompletedTask,
			_loggerMock.Object,
			ProblemDetailsService);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		context.Response.StatusCode.Should().Be(200);
	}

	[Fact]
	public async Task InvokeAsync_UnhandledException_Returns500ProblemDetails()
	{
		// Arrange
		DefaultHttpContext context = new();
		context.Response.Body = new MemoryStream();
		context.Items[CorrelationIdMiddleware.ItemKey] = "test-correlation-id";

		GlobalExceptionHandlerMiddleware middleware = new(
			_ => throw new InvalidOperationException("Something broke"),
			_loggerMock.Object,
			ProblemDetailsService);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		context.Response.StatusCode.Should().Be(500);
		context.Response.ContentType.Should().Be("application/problem+json");

		ProblemDetails? problemDetails = await DeserializeProblemDetails(context.Response);
		problemDetails.Should().NotBeNull();
		problemDetails!.Status.Should().Be(500);
		problemDetails.Title.Should().Be("An error occurred while processing your request.");
		problemDetails.Extensions.Should().ContainKey("errorId");
		problemDetails.Extensions.Should().ContainKey("correlationId");
		problemDetails.Extensions["correlationId"]!.ToString().Should().Be("test-correlation-id");
	}

	[Fact]
	public async Task InvokeAsync_UnhandledException_DoesNotLeakExceptionMessage()
	{
		// Arrange
		string sensitiveMessage = "Connection string: Server=prod;Password=secret";
		DefaultHttpContext context = new();
		context.Response.Body = new MemoryStream();

		GlobalExceptionHandlerMiddleware middleware = new(
			_ => throw new Exception(sensitiveMessage),
			_loggerMock.Object,
			ProblemDetailsService);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		context.Response.Body.Position = 0;
		using StreamReader reader = new(context.Response.Body);
		string body = await reader.ReadToEndAsync();
		body.Should().NotContain(sensitiveMessage);
	}

	[Fact]
	public async Task InvokeAsync_DuplicateEntityException_Returns409ProblemDetails()
	{
		// Arrange
		DefaultHttpContext context = new();
		context.Response.Body = new MemoryStream();

		GlobalExceptionHandlerMiddleware middleware = new(
			_ => throw new DuplicateEntityException("Category 'Food' already exists"),
			_loggerMock.Object,
			ProblemDetailsService);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		context.Response.StatusCode.Should().Be(409);
		context.Response.ContentType.Should().Be("application/problem+json");

		ProblemDetails? problemDetails = await DeserializeProblemDetails(context.Response);
		problemDetails.Should().NotBeNull();
		problemDetails!.Status.Should().Be(409);
		problemDetails.Title.Should().Be("Conflict");
		problemDetails.Detail.Should().Be("Category 'Food' already exists");
	}

	[Fact]
	public async Task InvokeAsync_KeyNotFoundException_Returns404ProblemDetails()
	{
		// Arrange
		DefaultHttpContext context = new();
		context.Response.Body = new MemoryStream();

		GlobalExceptionHandlerMiddleware middleware = new(
			_ => throw new KeyNotFoundException("API key not found"),
			_loggerMock.Object,
			ProblemDetailsService);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		context.Response.StatusCode.Should().Be(404);
		context.Response.ContentType.Should().Be("application/problem+json");

		ProblemDetails? problemDetails = await DeserializeProblemDetails(context.Response);
		problemDetails.Should().NotBeNull();
		problemDetails!.Status.Should().Be(404);
		problemDetails.Title.Should().Be("Not Found");
		problemDetails.Detail.Should().Be("API key not found");
	}

	[Fact]
	public async Task InvokeAsync_UnhandledException_LogsError()
	{
		// Arrange
		DefaultHttpContext context = new();
		context.Response.Body = new MemoryStream();

		GlobalExceptionHandlerMiddleware middleware = new(
			_ => throw new InvalidOperationException("test error"),
			_loggerMock.Object,
			ProblemDetailsService);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		_loggerMock.Verify(
			x => x.Log(
				LogLevel.Error,
				It.IsAny<EventId>(),
				It.IsAny<It.IsAnyType>(),
				It.IsAny<Exception>(),
				It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
			Times.Once);
	}

	[Fact]
	public async Task InvokeAsync_ArgumentException_Returns400ValidationProblemDetails()
	{
		// Arrange
		DefaultHttpContext context = new();
		context.Response.Body = new MemoryStream();

		GlobalExceptionHandlerMiddleware middleware = new(
			_ => throw new ArgumentException("Name cannot be empty"),
			_loggerMock.Object,
			ProblemDetailsService);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		context.Response.StatusCode.Should().Be(400);
		context.Response.ContentType.Should().Be("application/problem+json");

		ProblemDetails? problemDetails = await DeserializeProblemDetails(context.Response);
		problemDetails.Should().NotBeNull();
		problemDetails!.Status.Should().Be(400);
		problemDetails.Title.Should().Be("Validation Error");
		ValidationProblemDetails? validationProblem = await DeserializeValidationProblemDetails(context.Response);
		validationProblem!.Detail.Should().Be("Name cannot be empty");
		validationProblem.Errors.Should().BeEmpty();
	}

	[Fact]
	public async Task InvokeAsync_ArgumentException_StripsParameterNameFromDetail()
	{
		// Arrange
		DefaultHttpContext context = new();
		context.Response.Body = new MemoryStream();

		GlobalExceptionHandlerMiddleware middleware = new(
			_ => throw new ArgumentException("Amount must be non-zero", "amount"),
			_loggerMock.Object,
			ProblemDetailsService);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		ProblemDetails? problemDetails = await DeserializeProblemDetails(context.Response);
		problemDetails.Should().NotBeNull();
		problemDetails!.Detail.Should().Be("Amount must be non-zero");
		problemDetails.Detail.Should().NotContain("Parameter");
	}

	[Fact]
	public async Task InvokeAsync_ArgumentNullException_Returns500NotCaughtAsValidation()
	{
		// Arrange
		DefaultHttpContext context = new();
		context.Response.Body = new MemoryStream();
		context.Items[CorrelationIdMiddleware.ItemKey] = "test-correlation-id";

		GlobalExceptionHandlerMiddleware middleware = new(
			_ => throw new ArgumentNullException("connectionString"),
			_loggerMock.Object,
			ProblemDetailsService);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		context.Response.StatusCode.Should().Be(500);
	}

	[Fact]
	public async Task InvokeAsync_ArgumentOutOfRangeException_Returns500NotCaughtAsValidation()
	{
		// Arrange
		DefaultHttpContext context = new();
		context.Response.Body = new MemoryStream();
		context.Items[CorrelationIdMiddleware.ItemKey] = "test-correlation-id";

		GlobalExceptionHandlerMiddleware middleware = new(
			_ => throw new ArgumentOutOfRangeException("index"),
			_loggerMock.Object,
			ProblemDetailsService);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		context.Response.StatusCode.Should().Be(500);
	}

	[Fact]
	public async Task InvokeAsync_RequestAbortedCancellation_Returns499WithoutBodyOrErrorLog()
	{
		using CancellationTokenSource aborted = new();
		aborted.Cancel();
		DefaultHttpContext context = new()
		{
			RequestAborted = aborted.Token,
		};
		context.Response.Body = new MemoryStream();
		GlobalExceptionHandlerMiddleware middleware = new(
			_ => Task.FromCanceled(aborted.Token),
			_loggerMock.Object,
			ProblemDetailsService);

		await middleware.InvokeAsync(context);

		context.Response.StatusCode.Should().Be(StatusCodes.Status499ClientClosedRequest);
		context.Response.Body.Length.Should().Be(0);
		_loggerMock.Verify(
			x => x.Log(
				LogLevel.Error,
				It.IsAny<EventId>(),
				It.IsAny<It.IsAnyType>(),
				It.IsAny<Exception>(),
				It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
			Times.Never);
	}

	[Fact]
	public async Task InvokeAsync_ExceptionAfterResponseStarted_RethrowsWithoutAppendingProblem()
	{
		StartedResponseFeature responseFeature = new();
		DefaultHttpContext context = new();
		context.Request.Method = HttpMethods.Get;
		context.Features.Set<IHttpResponseFeature>(responseFeature);
		context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseFeature.Body));
		GlobalExceptionHandlerMiddleware middleware = new(
			async httpContext =>
			{
				await httpContext.Response.WriteAsync("committed response");
				throw new InvalidOperationException("late failure");
			},
			_loggerMock.Object,
			ProblemDetailsService);

		Func<Task> act = () => middleware.InvokeAsync(context);

		await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("late failure");
		context.Response.Body.Position = 0;
		using StreamReader reader = new(context.Response.Body);
		(await reader.ReadToEndAsync()).Should().Be("committed response");
		context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
	}

	[Fact]
	public async Task ThrownEndpoint_UnsupportedAccept_StillReturnsOriginalProblemResponse()
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Services.AddProblemDetails();
		await using WebApplication app = builder.Build();
		app.UseMiddleware<GlobalExceptionHandlerMiddleware>();
		app.MapGet("/throw", (HttpContext _) => throw new InvalidOperationException("sensitive failure"));
		await app.StartAsync();
		using HttpClient client = app.GetTestClient();
		using HttpRequestMessage request = new(HttpMethod.Get, "/throw");
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));

		using HttpResponseMessage response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		body.RootElement.GetProperty("status").GetInt32().Should().Be(500);
		body.RootElement.GetProperty("title").GetString()
			.Should().Be("An error occurred while processing your request.");
		body.RootElement.TryGetProperty("errorId", out _).Should().BeTrue();
		body.RootElement.GetRawText().Should().NotContain("sensitive failure");
	}

	[Fact]
	public async Task ArgumentException_UnsupportedAccept_PreservesValidationProblemShape()
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Services.AddProblemDetails();
		await using WebApplication app = builder.Build();
		app.UseMiddleware<GlobalExceptionHandlerMiddleware>();
		app.MapGet(
			"/argument",
			(HttpContext _) => throw new ArgumentException("Name cannot be empty", "name"));
		await app.StartAsync();
		using HttpClient client = app.GetTestClient();
		using HttpRequestMessage request = new(HttpMethod.Get, "/argument");
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));

		using HttpResponseMessage response = await client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		body.RootElement.GetProperty("status").GetInt32().Should().Be(400);
		body.RootElement.GetProperty("title").GetString().Should().Be("Validation Error");
		body.RootElement.GetProperty("detail").GetString().Should().Be("Name cannot be empty");
		JsonElement errors = body.RootElement.GetProperty("errors");
		errors.ValueKind.Should().Be(JsonValueKind.Object);
		errors.EnumerateObject().Should().BeEmpty();
	}

	private static async Task<ProblemDetails?> DeserializeProblemDetails(HttpResponse response)
	{
		response.Body.Position = 0;
		return await JsonSerializer.DeserializeAsync<ProblemDetails>(
			response.Body,
			new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
	}

	private static async Task<ValidationProblemDetails?> DeserializeValidationProblemDetails(HttpResponse response)
	{
		response.Body.Position = 0;
		return await JsonSerializer.DeserializeAsync<ValidationProblemDetails>(
			response.Body,
			new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
	}

	private sealed class StartedResponseFeature : IHttpResponseFeature
	{
		public int StatusCode { get; set; } = StatusCodes.Status200OK;
		public string? ReasonPhrase { get; set; }
		public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
		public Stream Body { get; set; } = new MemoryStream();
		public bool HasStarted => true;

		public void OnStarting(Func<object, Task> callback, object state)
		{
		}

		public void OnCompleted(Func<object, Task> callback, object state)
		{
		}
	}
}
