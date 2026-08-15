using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

[Migration(21)]
public class M0021_SeedProofreadModelSetting : Migration
{
    public override void Up()
    {
        Insert.IntoTable("settings").Row(new
        {
            key = "proofread_model",
            value = ""
        });
    }

    public override void Down()
    {
        Delete.FromTable("settings").Row(new
        {
            key = "proofread_model"
        });
    }
}
