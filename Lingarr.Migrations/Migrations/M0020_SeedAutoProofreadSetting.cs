using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

[Migration(20)]
public class M0020_SeedAutoProofreadSetting : Migration
{
    public override void Up()
    {
        Insert.IntoTable("settings").Row(new
        {
            key = "auto_proofread",
            value = "false"
        });
    }

    public override void Down()
    {
        Delete.FromTable("settings").Row(new
        {
            key = "auto_proofread"
        });
    }
}
