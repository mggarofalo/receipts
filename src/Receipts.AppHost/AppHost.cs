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
	.WithEnvironment("AdminSeed__LastName", "User")
	// Local dev gets a large, realistic sample dataset (accounts, receipts, transactions)
	// so dashboards and reports are populated. Applied once; see SampleDataSeederService.
	.WithEnvironment("SampleData__Enabled", "true");

// API: starts after seeder completes
IResourceBuilder<ProjectResource> api = builder.AddProject<Projects.API>("api")
	.WithReference(db)
	.WaitForCompletion(seeder);

// AddViteApp already creates the "http" endpoint and passes its target port to
// Vite via --port. Pin the host port on that existing endpoint rather than adding
// a second one: a separate endpoint gets its own DCP proxy with nothing listening
// behind it, so requests to it hang forever (RECEIPTS-882).
builder.AddViteApp("frontend", "../client")
	.WithReference(api)
	.WithEndpoint("http", endpoint => endpoint.Port = 5173)
	.WithExternalHttpEndpoints();

await builder.Build().RunAsync();
