using System.Data;
using System.Text.Json;
using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

[Migration(19)]
public class M0019_SeedOpenRouterAndOllamaSettings : Migration
{
    private static readonly string[] Keys =
    [
        "openrouter_model",
        "openrouter_api_key",
        "openrouter_request_template",
        "ollama_model",
        "ollama_endpoint",
        "ollama_api_key",
        "ollama_chat_request_template",
        "ollama_generate_request_template"
    ];

    public override void Up()
    {
        foreach (var key in Keys)
        {
            IfDatabase("sqlite").Execute.Sql(
                $"INSERT OR IGNORE INTO settings (\"key\", value) VALUES ('{key}', '')");
            IfDatabase("mysql").Execute.Sql(
                $"INSERT IGNORE INTO settings (`key`, value) VALUES ('{key}', '')");
            IfDatabase("postgresql").Execute.Sql(
                $"INSERT INTO settings (\"key\", value) VALUES ('{key}', '') ON CONFLICT (\"key\") DO NOTHING");
        }
    }

    public override void Down()
    {
        Execute.WithConnection(RemoveUnsupportedSelections);

        foreach (var key in Keys)
        {
            IfDatabase("sqlite", "postgresql").Execute.Sql(
                $"DELETE FROM settings WHERE \"key\" = '{key}' AND provider IS NULL");
            IfDatabase("mysql").Execute.Sql(
                $"DELETE FROM settings WHERE `key` = '{key}' AND provider IS NULL");
        }
    }

    private static void RemoveUnsupportedSelections(IDbConnection connection, IDbTransaction transaction)
    {
        var keyColumn = connection.GetType().Name.Contains("MySql", StringComparison.OrdinalIgnoreCase)
            ? "`key`"
            : "\"key\"";
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = $"SELECT value FROM settings WHERE {keyColumn} = 'service_type'";
        var value = read.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(value)) return;

        List<string> services;
        try
        {
            services = value.TrimStart().StartsWith('[')
                ? JsonSerializer.Deserialize<List<string>>(value) ?? []
                : [value];
        }
        catch (JsonException)
        {
            return;
        }

        if (services.RemoveAll(service =>
                service.Equals("openrouter", StringComparison.OrdinalIgnoreCase) ||
                service.Equals("ollama", StringComparison.OrdinalIgnoreCase)) == 0)
        {
            return;
        }
        if (services.Count == 0) services.Add("libretranslate");

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = $"UPDATE settings SET value = @value WHERE {keyColumn} = 'service_type'";
        var parameter = update.CreateParameter();
        parameter.ParameterName = "@value";
        parameter.Value = JsonSerializer.Serialize(services);
        update.Parameters.Add(parameter);
        update.ExecuteNonQuery();
    }
}
