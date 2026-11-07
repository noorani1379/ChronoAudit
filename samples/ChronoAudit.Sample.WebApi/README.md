# ChronoAudit sample host

A small library application (books, members, loans) that uses every part of ChronoAudit on SQL Server or PostgreSQL. It is the reference for the guides in `docs/guide`, and `requests.http` has one request for each endpoint group.

## Run it

1. Pick the provider and give a connection string (a database that does not exist yet is fine for the migration step below):

   ```bash
   # SQL Server (the default)
   export ConnectionStrings__LibraryDb="Server=localhost,1433;Database=ChronoAuditLibrary;User Id=sa;Password=<your password>;TrustServerCertificate=True"

   # PostgreSQL
   export ChronoAudit__Provider=PostgreSql
   export CHRONOAUDIT_SAMPLE_PROVIDER=PostgreSql
   export ConnectionStrings__LibraryDb="Host=localhost;Database=chronoaudit_library;Username=postgres;Password=<your password>"
   ```

2. Create the database objects. The sample project keeps one migration folder per provider; `ProviderMigrationsAssembly` shows EF Core only the folder of the provider in use. The project declares `TargetFrameworks`, so name the framework for `dotnet ef`:

   ```bash
   dotnet ef database update --project samples/ChronoAudit.Sample.WebApi --framework net10.0 --context LibraryDbContext --connection "$ConnectionStrings__LibraryDb"
   ```

   The migration also creates the guard triggers (`CreateChronoAuditAuditGuard`, `CreateChronoAuditStoreGuards`) that make the audit tables append-only.

3. Start the host (Development, so no HMAC key is needed; any other environment needs `ChronoAudit__HmacKey`, 32 bytes or more):

   ```bash
   dotnet run --project samples/ChronoAudit.Sample.WebApi --urls http://localhost:5170
   ```

4. Open `requests.http` in Visual Studio, Rider or VS Code (REST Client) and send the requests. The demo sign-in is the header `X-Demo-User`; every demo user gets the All scope.

## What to look at

- `Program.cs`: the registration chain, the middleware order (activity log before authentication), CORS exposing the ChronoAudit headers, the personal-data reveal policy, and the demo endpoints that record timeline events.
- `Model/LibraryModel.cs`: the audited context and entities (`[SensitiveData]` on a phone number, a composite key on `Loan`).
- `Migrations/SqlServer`, `Migrations/PostgreSql`: the migrations, with the guard triggers.
- `appsettings.json`: the non-secret options. The connection string and the HMAC key are not in the file.

An application with one database provider needs no `ProviderMigrationsAssembly`: it is only here because the sample ships both.
