using GuideAntsApi.DataModel;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GuideAntsApi.Database;

public static class SqlServerDatabaseInitializer
{
    /// <summary>
    /// Ensures the SQL Server catalog exists, then applies EF Core migrations (vector types require UseVectorSearch).
    /// Must run before <see cref="Microsoft.AspNetCore.Builder.WebApplicationBuilder.Build"/> so DB-backed configuration can load.
    /// </summary>
    public static void EnsureCatalogAndMigrate(string connectionString, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!string.IsNullOrWhiteSpace(builder.AttachDBFilename))
        {
            throw new InvalidOperationException(
                "Automatic database creation is not supported when AttachDbFilename is set.");
        }

        var catalog = builder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(catalog))
        {
            throw new InvalidOperationException(
                "Connection string must specify Initial Catalog for automatic database setup.");
        }

        if (catalog.Length > 128)
        {
            throw new InvalidOperationException("Database name exceeds maximum length (128).");
        }

        var createdCatalog = false;
        builder.InitialCatalog = "master";
        using (var connection = new SqlConnection(builder.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DECLARE @created bit = 0;

                IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = @dbName)
                BEGIN
                    DECLARE @createSql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@dbName);
                    EXEC (@createSql);

                    -- New installs default to SIMPLE so transaction logs auto-truncate and do not require log backups.
                    DECLARE @recoverySql nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@dbName) + N' SET RECOVERY SIMPLE WITH NO_WAIT';
                    EXEC (@recoverySql);

                    SET @created = 1;
                END

                SELECT @created;
                """;
            var p = command.CreateParameter();
            p.ParameterName = "@dbName";
            p.Value = catalog;
            command.Parameters.Add(p);
            var scalarResult = command.ExecuteScalar();
            createdCatalog = Convert.ToInt32(scalarResult) == 1;
        }

        if (createdCatalog)
        {
            logger.LogInformation("Created SQL Server catalog '{Catalog}' and set recovery model to SIMPLE.", catalog);
        }
        else
        {
            logger.LogInformation("SQL Server catalog '{Catalog}' already exists; keeping current recovery model.", catalog);
        }

        logger.LogInformation("SQL Server catalog '{Catalog}' is ready; applying EF Core migrations.", catalog);

        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        optionsBuilder.UseSqlServer(connectionString, sqlOptions =>
        {
            sqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery);
            sqlOptions.UseVectorSearch();
            sqlOptions.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null);
            sqlOptions.CommandTimeout(60);
        });

        using var context = new ApplicationDbContext(optionsBuilder.Options);
        context.Database.Migrate();
    }
}
