using API.Http;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Presentation.API.Tests.Http;

public class ApiValidationProblemTests
{
	[Fact]
	public void FromModelState_DoesNotExposeExceptionDetails_AndPreservesProblemContract()
	{
		ModelStateDictionary state = new();
		state.SetModelValue("TaxAmount", "invalid", "invalid");
		state["TaxAmount"]!.Errors.Add(new InvalidOperationException("secret connection details"));

		JsonResult result = ApiValidationProblem.FromModelState(state);

		result.StatusCode.Should().Be(400);
		result.ContentType.Should().Be("application/problem+json");
		ValidationProblemDetails problem = result.Value.Should().BeOfType<ValidationProblemDetails>().Subject;
		problem.Errors["TaxAmount"].Should().Equal("The supplied value is invalid.");
		problem.Detail.Should().Be("The supplied value is invalid.");
	}

	[Fact]
	public void Create_KeepsIndexedFieldErrors_AndProvidesDistinctHumanReasons()
	{
		Dictionary<string, string[]> errors = new()
		{
			["[0].Location"] = ["Location is required."],
			["[1].Location"] = ["Location is required."],
			["[1].Date"] = ["Date must not be in the future."],
		};

		ValidationProblemDetails result = ApiValidationProblem.Create(errors);

		result.Status.Should().Be(400);
		result.Errors.Should().BeEquivalentTo(errors);
		result.Detail.Should().Be("Location is required. Date must not be in the future.");
	}
}
