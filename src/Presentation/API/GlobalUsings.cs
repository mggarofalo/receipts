// ApiProblem is used by every controller that rejects a request with a reason
// (RECEIPTS-886). A global using keeps 20 controllers from each carrying the
// same import purely to name it.
global using API.Http;

// The spec now declares a ProblemDetails schema, so the DTO generator emits
// API.Generated.Dtos.ProblemDetails alongside ASP.NET's — and every controller
// imports both namespaces, making the bare name ambiguous. Controllers always
// mean the framework type: it is what TypedResults serialises and what carries
// the extension members. The generated DTO exists only so the client's
// TypeScript types know the shape.
global using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;
