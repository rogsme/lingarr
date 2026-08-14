using FluentMigrator.Runner;
using Lingarr.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Npgsql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Lingarr.Migrations.Tests;

[Trait("Category", "Integration")]
public class MigrationTests
{
    private static void RunMigrations(string connectionString, string dbType)
    {
        var services = new ServiceCollection();
        services.AddFluentMigrator(connectionString, dbType);

        var serviceProvider = services.BuildServiceProvider();
        MigrationConfiguration.RunMigrations(serviceProvider);
    }

    [Fact]
    public async Task Sqlite_MigrationsRunSuccessfully()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lingarr_test_{Guid.NewGuid()}.db");
        try
        {
            var connectionString = $"Data Source={dbPath}";
            RunMigrations(connectionString, "sqlite");

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task Sqlite_MigrationsRollBackSuccessfully()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lingarr_test_{Guid.NewGuid()}.db");
        try
        {
            var connectionString = $"Data Source={dbPath}";
            var services = new ServiceCollection();
            services.AddFluentMigrator(connectionString, "sqlite");

            var serviceProvider = services.BuildServiceProvider();
            MigrationConfiguration.RunMigrations(serviceProvider);

            using var scope = serviceProvider.CreateScope();
            scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateDown(7);

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task Sqlite_MigrationsReapplyAfterRollback()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lingarr_test_{Guid.NewGuid()}.db");
        try
        {
            var connectionString = $"Data Source={dbPath}";
            var services = new ServiceCollection();
            services.AddFluentMigrator(connectionString, "sqlite");

            var serviceProvider = services.BuildServiceProvider();
            MigrationConfiguration.RunMigrations(serviceProvider);

            using var scope = serviceProvider.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
            runner.MigrateDown(2);
            runner.MigrateUp();

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM settings WHERE key = 'navigate_to_details_on_request'";
            Assert.Equal(1L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task Sqlite_OpenRouterAndOllamaMigration_PreservesPluginRowsAndCleansSelectionOnRollback()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lingarr_test_{Guid.NewGuid()}.db");
        try
        {
            var connectionString = $"Data Source={dbPath}";
            var services = new ServiceCollection();
            services.AddFluentMigrator(connectionString, "sqlite");
            var serviceProvider = services.BuildServiceProvider();

            using var scope = serviceProvider.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
            runner.MigrateUp(18);

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText =
                    "INSERT INTO settings (key, value, provider) VALUES ('openrouter_api_key', 'plugin-value', 'external-openrouter')";
                await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            runner.MigrateUp();
            await using (var select = connection.CreateCommand())
            {
                select.CommandText = "SELECT value FROM settings WHERE key = 'openrouter_api_key'";
                Assert.Equal("plugin-value", await select.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            }

            await using (var selectProvider = connection.CreateCommand())
            {
                selectProvider.CommandText =
                    "UPDATE settings SET value = '[\"openrouter\",\"ollama\"]' WHERE key = 'service_type'";
                await selectProvider.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            runner.MigrateDown(18);
            await using (var selectService = connection.CreateCommand())
            {
                selectService.CommandText = "SELECT value FROM settings WHERE key = 'service_type'";
                Assert.Equal("[\"libretranslate\"]", await selectService.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            }
            await using (var selectPlugin = connection.CreateCommand())
            {
                selectPlugin.CommandText = "SELECT value FROM settings WHERE key = 'openrouter_api_key'";
                Assert.Equal("plugin-value", await selectPlugin.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task MySql_MigrationsRunSuccessfully()
    {
        await using var container = new MySqlBuilder("mysql:latest")
            .Build();
        await container.StartAsync(TestContext.Current.CancellationToken);

        var connectionString = container.GetConnectionString();
        RunMigrations(connectionString, "mysql");

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task Postgres_MigrationsRunSuccessfully()
    {
        await using var container = new PostgreSqlBuilder("postgres:latest")
            .Build();
        await container.StartAsync(TestContext.Current.CancellationToken);

        var connectionString = container.GetConnectionString();
        RunMigrations(connectionString, "postgres");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }
}
