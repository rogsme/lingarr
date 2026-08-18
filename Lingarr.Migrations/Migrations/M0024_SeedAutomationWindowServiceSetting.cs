using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

[Migration(24)]
public class M0024_SeedAutomationWindowServiceSetting : Migration
{
    public override void Up()
    {
        Insert.IntoTable("settings").Row(new
        {
            key = "automation_window_service_type",
            value = string.Empty
        });
    }

    public override void Down()
    {
        Delete.FromTable("settings").Row(new
        {
            key = "automation_window_service_type"
        });
    }
}
