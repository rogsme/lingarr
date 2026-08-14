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
            Insert.IntoTable("settings").Row(new { key, value = "" });
        }
    }

    public override void Down()
    {
        foreach (var key in Keys)
        {
            Delete.FromTable("settings").Row(new { key });
        }
    }
}
