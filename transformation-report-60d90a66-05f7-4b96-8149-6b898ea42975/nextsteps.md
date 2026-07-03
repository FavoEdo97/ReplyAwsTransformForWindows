# Next Steps

## Issues resolved
- Transformed Bookstore.Cdk.csproj to net8.0
- Transformed Bookstore.Domain.csproj to net8.0
- Transformed Bookstore.Data.csproj to net8.0
- Transformed Bookstore.Web.csproj to net8.0

## Summary

The transformation appears to have completed successfully. No build errors were detected across any of the projects in the solution:

- `Bookstore.Cdk`
- `Bookstore.Common`
- `Bookstore.Web`
- `Bookstore.Data`
- `Bookstore.Domain`

## Validation and Testing

### 1. Restore Dependencies

Run the following command from the solution root to ensure all NuGet packages are restored correctly:

```bash
dotnet restore
```

Review the output for any warnings related to package compatibility or deprecated packages targeting older frameworks.

### 2. Build the Solution

Perform a full solution build to confirm there are no runtime or compile-time issues beyond what was captured in the initial error report:

```bash
dotnet build --configuration Release
```

Address any warnings that surface, particularly those related to nullable reference types, obsolete APIs, or platform compatibility.

### 3. Run Unit Tests

If the solution contains test projects, execute them to verify that existing functionality has not regressed:

```bash
dotnet test --configuration Release --logger "console;verbosity=detailed"
```

Review any failing tests and determine whether they are caused by behavioral differences in the new target framework.

### 4. Verify Runtime Behavior

Run the web application locally to confirm it starts and operates as expected:

```bash
dotnet run --project app/Bookstore.Web/Bookstore.Web.csproj --configuration Release
```

Manually exercise the core application flows, particularly those that interact with `Bookstore.Data` and `Bookstore.Domain`, as data access layers are common sources of runtime issues after migration.

### 5. Check Database Compatibility

If `Bookstore.Data` uses Entity Framework or another ORM, verify that:

- The database connection strings are correctly configured for the new environment.
- Any pending migrations are applied:

```bash
dotnet ef database update --project app/Bookstore.Data/Bookstore.Data.csproj --startup-project app/Bookstore.Web/Bookstore.Web.csproj
```

- EF Core tooling is installed if not already present:

```bash
dotnet tool install --global dotnet-ef
```

### 6. Review Configuration Files

Confirm that any configuration previously held in `Web.config` or `App.config` has been correctly migrated to `appsettings.json` or environment variables. Pay particular attention to:

- Connection strings
- Application-specific settings consumed by `Bookstore.Common`
- Any AWS-related configuration used by `Bookstore.Cdk`

### 7. Validate CDK Project

For the `Bookstore.Cdk` project, ensure the AWS CDK CLI is installed and that the synthesized output is correct:

```bash
cdk synth
```

Review the synthesized CloudFormation template for any discrepancies compared to the previous infrastructure definition.

### 8. Inspect for Platform-Specific Code

Search the solution for any remaining Windows-specific APIs or packages that may not surface as build errors but could cause issues at runtime on non-Windows platforms:

```bash
grep -r "System.Windows" app/
grep -r "Microsoft.Win32" app/
```

Replace or conditionally compile any identified platform-specific code as needed.