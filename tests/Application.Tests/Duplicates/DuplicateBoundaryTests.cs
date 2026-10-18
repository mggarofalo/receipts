using Application.Commands.Reports;
using Application.Interfaces.Services;
using FluentAssertions;

namespace Application.Tests.Duplicates;

public class DuplicateBoundaryTests
{
	[Fact]
	public void DuplicateCommands_CannotForwardWritesToReportReaders()
	{
		Type[] handlers = [typeof(AcceptDuplicateGroupCommandHandler), typeof(UnacceptDuplicateGroupCommandHandler)];
		foreach (Type handler in handlers)
		{
			Type[] dependencies = handler.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType).ToArray();
			dependencies.Should().Contain(typeof(IDuplicateAcceptanceStore));
			dependencies.Should().NotContain(typeof(IReportService)).And.NotContain(typeof(IDuplicateReportReader));
		}
		typeof(IReportService).GetMethods().Should().OnlyContain(m => m.Name.StartsWith("Get"));
		typeof(IDuplicateReportReader).GetMethods().Should().OnlyContain(m => m.Name.StartsWith("Get"));
	}
}
