global using Xunit;

// Mirrors src/Presentation/API/GlobalUsings.cs: the tests name the same framework
// ProblemDetails the controllers return, and would otherwise hit the same ambiguity
// with the generated DTO of that name.
global using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;
