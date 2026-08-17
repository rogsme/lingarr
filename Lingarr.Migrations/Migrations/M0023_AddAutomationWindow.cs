using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

[Migration(23)]
public class M0023_AddAutomationWindow : Migration
{
    public override void Up()
    {
        Insert.IntoTable("settings").Row(new
        {
            key = "automation_window_enabled",
            value = "false"
        });
        Insert.IntoTable("settings").Row(new
        {
            key = "automation_window_start",
            value = "00:00"
        });
        Insert.IntoTable("settings").Row(new
        {
            key = "automation_window_end",
            value = "08:00"
        });
        Insert.IntoTable("settings").Row(new
        {
            key = "automation_window_timezone",
            value = "UTC"
        });

        if (!Schema.Table("translation_requests").Column("is_automated").Exists())
        {
            Alter.Table("translation_requests")
                .AddColumn("is_automated").AsBoolean().NotNullable().WithDefaultValue(false);
        }
    }

    public override void Down()
    {
        Delete.FromTable("settings").Row(new
        {
            key = "automation_window_enabled"
        });
        Delete.FromTable("settings").Row(new
        {
            key = "automation_window_start"
        });
        Delete.FromTable("settings").Row(new
        {
            key = "automation_window_end"
        });
        Delete.FromTable("settings").Row(new
        {
            key = "automation_window_timezone"
        });

        Delete.Column("is_automated").FromTable("translation_requests");
    }
}
