using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

[Migration(22)]
public class M0022_SeedBatchOverlapSizeSetting : Migration
{
    public override void Up()
    {
        Insert.IntoTable("settings").Row(new
        {
            key = "batch_overlap_size",
            value = "0"
        });
    }

    public override void Down()
    {
        Delete.FromTable("settings").Row(new
        {
            key = "batch_overlap_size"
        });
    }
}
