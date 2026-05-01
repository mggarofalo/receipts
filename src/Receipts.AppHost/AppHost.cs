using Infrastructure.Services;

var builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<PostgresServerResource> postgres = builder.AddPostgres("postgres")
	.WithImage("pgvector/pgvector", "pg17")
	.WithDataVolume()
	.WithPgAdmin(pgAdmin => pgAdmin.WithImageTag("9.13"));

IResourceBuilder<PostgresDatabaseResource> db = postgres.AddDatabase("receiptsdb");

// DbMigrator: applies EF Core migrations, then exits
IResourceBuilder<ProjectResource> migrator = builder.AddProject<Projects.DbMigrator>("db-migrator")
	.WithReference(db)
	.WaitFor(db);

// DbSeeder: seeds roles and admin user, then exits
IResourceBuilder<ProjectResource> seeder = builder.AddProject<Projects.DbSeeder>("db-seeder")
	.WithReference(db)
	.WaitForCompletion(migrator)
	// These override DbSeeder/appsettings.Development.json when running under Aspire.
	// Keep both in sync, or remove appsettings.Development.json AdminSeed section if
	// all local dev runs go through Aspire.
	.WithEnvironment("AdminSeed__Email", "admin@receipts.local")
	.WithEnvironment("AdminSeed__Password", "Admin123!@#")
	.WithEnvironment("AdminSeed__FirstName", "Admin")
	.WithEnvironment("AdminSeed__LastName", "User");

// VLM model tag — single source of truth across Aspire / docker-compose / appsettings (RECEIPTS-635).
// Read from the VLM_MODEL env var; fall back to VlmOcrOptions.DefaultModel. The same value flows to
// the pull sidecar (so it pulls the right tag) and to API + VlmEval as Ocr__Vlm__Model (so they
// query Ollama for that tag).
string vlmModel = Environment.GetEnvironmentVariable("VLM_MODEL") ?? VlmOcrOptions.DefaultModel;

// VLM OCR: Ollama container serving the configured VLM (RECEIPTS-616 epic).
// Named volume persists the model cache across restarts so the first-run ~3 GB pull happens once.
// Host port is left unset so Aspire picks a free one — Ollama's default 11434 is frequently
// already bound on developer machines running the native Ollama daemon, which would wedge Aspire
// startup since the API below does .WaitFor(vlmOcr).
//
// .WithHttpHealthCheck("/api/tags") gates WaitFor() on Ollama actually responding rather than
// just the container being up — without this, dependents (the pull sidecar, the API smoke test)
// would race the Ollama startup. /api/tags is the lightest Ollama endpoint that returns 200
// once the server is ready (RECEIPTS-636).
IResourceBuilder<ContainerResource> vlmOcr = builder.AddContainer("vlm-ocr", "ollama/ollama", "latest")
	.WithVolume("vlm-ocr-models", "/root/.ollama")
	.WithHttpEndpoint(targetPort: 11434, name: "http")
	.WithHttpHealthCheck("/api/tags");

// One-shot sidecar that pulls the configured VLM model if it is not already cached in the shared
// volume, then exits. Idempotent — subsequent runs find the model present and skip the download.
//
// The API and VlmEval gate on this sidecar via .WaitForCompletion (RECEIPTS-636), so a
// non-zero exit here permanently blocks dependents. Retry up to 5 times with backoff
// to tolerate transient network failures during the ~3 GB cold-start pull. Mirrors the
// docker-compose vlm-ocr-pull retry pattern.
//
// $VLM_MODEL is sourced from the OS env (set by .WithEnvironment below) so a single edit point
// (VlmOcrOptions.DefaultModel or the VLM_MODEL env var) controls everything (RECEIPTS-635).
const string vlmOcrPullCommand = """
	for i in 1 2 3 4 5; do
	  if ollama list | grep -qF "$VLM_MODEL"; then
	    echo "$VLM_MODEL already present; skipping pull"
	    exit 0
	  fi
	  echo "Pulling $VLM_MODEL (attempt $i/5)..."
	  if ollama pull "$VLM_MODEL"; then
	    exit 0
	  fi
	  echo "Pull failed; sleeping before retry"
	  sleep 10
	done
	echo "All pull attempts failed" >&2
	exit 1
	""";
// Normalize CRLF → LF so the bash heredoc parses correctly on Linux. Without this, a Windows
// .editorconfig that mandates CRLF for *.cs files causes /bin/sh -c to see `do\r` as a non-keyword
// and fail with "Syntax error: word unexpected (expecting \"do\")".
string vlmOcrPullCommandLf = vlmOcrPullCommand.Replace("\r\n", "\n");
IResourceBuilder<ContainerResource> vlmOcrPull = builder.AddContainer("vlm-ocr-pull", "ollama/ollama", "latest")
	.WithEntrypoint("/bin/sh")
	.WithArgs("-c", vlmOcrPullCommandLf)
	.WithEnvironment("OLLAMA_HOST", "http://vlm-ocr:11434")
	.WithEnvironment("VLM_MODEL", vlmModel)
	.WaitFor(vlmOcr);

// API: starts after seeder completes; Ollama URL injected for the smoke test and future extraction service.
// .WaitForCompletion(vlmOcrPull) ensures the model is fully pulled (cold first-run can be ~3 GB)
// before the API boots so the smoke test in InfrastructureService never catches Ollama mid-pull
// (RECEIPTS-636).
//
// RECEIPTS-652: ANTHROPIC_API_KEY is forwarded from the host environment when present so a
// developer can flip Ocr:Vlm:Provider=anthropic for a single run without separately exporting
// the key for the API project. The variable name uses the standard config-binder mapping
// (Anthropic:ApiKey -> Anthropic__ApiKey) so the existing IConfiguration binding picks it up.
string anthropicApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? string.Empty;
string vlmProvider = Environment.GetEnvironmentVariable("Ocr__Vlm__Provider")
	?? Environment.GetEnvironmentVariable("OCR__VLM__PROVIDER")
	?? "ollama";

IResourceBuilder<ProjectResource> api = builder.AddProject<Projects.API>("api")
	.WithReference(db)
	.WithEnvironment("Ollama__BaseUrl", vlmOcr.GetEndpoint("http"))
	.WithEnvironment("Ocr__Vlm__Model", vlmModel)
	.WithEnvironment("Ocr__Vlm__Provider", vlmProvider)
	.WithEnvironment("Anthropic__ApiKey", anthropicApiKey)
	.WaitForCompletion(seeder)
	.WaitFor(vlmOcr)
	.WaitForCompletion(vlmOcrPull);

// VlmEval: dev-only sidecar that runs the local VLM receipt-extraction pipeline against a
// gitignored directory of real receipt fixtures and logs a scorecard. Parked on startup —
// trigger from the Aspire dashboard. See src/Tools/VlmEval/README.md.
string repoRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", ".."));
string vlmEvalFixturesPath = Path.Combine(repoRoot, "fixtures", "vlm-eval");
// Ensure the (gitignored) fixtures directory exists so a fresh dev box doesn't trip the
// "missing fixtures dir" hard-error path on the very first start. Idempotent.
Directory.CreateDirectory(vlmEvalFixturesPath);

builder.AddProject<Projects.VlmEval>("vlm-eval")
	.WithEnvironment("Ollama__BaseUrl", vlmOcr.GetEndpoint("http"))
	.WithEnvironment("Ocr__Vlm__Model", vlmModel)
	.WithEnvironment("VlmEval__FixturesPath", vlmEvalFixturesPath)
	// RECEIPTS-652: forward the Anthropic API key so VlmEval can run with --provider anthropic
	// (or VlmEval__Provider=anthropic) without a separate export. Empty is fine; Anthropic
	// option binding only fires when the provider is selected.
	.WithEnvironment("Anthropic__ApiKey", anthropicApiKey)
	// Dev convenience: an empty fixtures directory is a warning, not a hard error.
	// CI sets FailOnAnyFixtureFailure=true via env override to make accuracy regressions
	// fail the pipeline once real fixtures exist.
	.WithEnvironment("VlmEval__FailOnAnyFixtureFailure", "false")
	.WaitFor(vlmOcr)
	.WaitForCompletion(vlmOcrPull)
	.WithExplicitStart();

builder.AddViteApp("frontend", "../client")
	.WithReference(api)
	.WithHttpEndpoint(port: 5173, name: "vite", env: "PORT")
	.WithExternalHttpEndpoints();

await builder.Build().RunAsync();
