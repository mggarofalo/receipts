# Dead-code audit and removal

Dead-code findings require a declared consumer boundary. A successful build after deleting a symbol is useful evidence, but does not establish that reflection, configuration, generated dispatch, or external consumers never use it. Tests alone also cannot establish production reachability.

## Evidence required before removal

1. Identify the symbol or disconnected cluster and its production entry points. Include API routes, client routes and lazy imports, executable tools, hosted services, generated dispatch, dependency-injection resolution, and build/deployment commands.
2. Use compiler-resolved references to distinguish actual consumers from name matches. Classify test-only references separately. Search configuration, scripts, documentation, reflection and serialization conventions for consumers outside the compiler graph.
3. Record why generated references and registration-only edges do or do not make the candidate executable. A Mediator registration does not itself construct or send a request; a hosted-service registration does execute a service.
4. Preserve persisted models, migrations and external contracts unless a separate compatibility decision authorizes their removal. An unused repository does not imply that its stored table can be dropped.
5. Remove the proven cluster and its exclusively obsolete tests together. Keep behavior tests for surviving implementations; replace ineffective mocks with assertions at the actual boundary.
6. Rebuild and validate the resulting application and generated contracts. Have a reviewer who did not implement the removal inspect both the reachability evidence and the surviving tests.

The compiler audit tools report within their documented boundaries. Unknown dynamic imports, unresolved references, or unsupported roots must be investigated rather than treated as evidence of absence. They do not automatically delete code. Broad coverage and a zero-reference text search are not substitutes for this evidence.

## This audit

RECEIPTS-989 compares main revision `3cef6d257fc424f51205a3060066dc2abc8c0142` with the integrated architecture refactor at `d71e96e85030032dd52693bfa3ea32792a942a79`. Removals are prepared independently against main and subsequently verified with the integrated refactor. The ImageSharp/PDF removal is already owned by RECEIPTS-988 / PR #750 and is not duplicated here.

The audit uses the TypeScript compiler API and Roslyn semantic references. No LSP endpoint or standalone language-server executable was available in the execution environment.

See [backend evidence](dead-code-backend.md) and [integration evidence](dead-code-integrations.md) for individual candidates, retained runtime roots, and reproduction commands. The [client evidence](dead-code-client.md) describes its compiler audit and production entry points.


