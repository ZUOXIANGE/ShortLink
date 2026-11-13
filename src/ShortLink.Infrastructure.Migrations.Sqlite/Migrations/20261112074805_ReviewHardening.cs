using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShortLink.Infrastructure.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class ReviewHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "token_version",
                table: "users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "concurrency_token",
                table: "short_links",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "default_slot",
                table: "short_domains",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "event_id",
                table: "click_events",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "click_retention_leases",
                columns: table => new
                {
                    name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    owner_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    lease_until_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    last_completed_date = table.Column<DateOnly>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_click_retention_leases", x => x.name);
                });

            // 历史数据回填必须在唯一索引之前：保留最早默认域名，重名路由改名并停用，点击补独立 EventId。
            migrationBuilder.Sql("""
                UPDATE short_domains
                SET is_default = 0
                WHERE is_default = 1
                  AND id NOT IN (SELECT MIN(id) FROM short_domains WHERE is_default = 1);
                UPDATE short_domains SET default_slot = 1 WHERE is_default = 1;
                UPDATE link_routes
                SET name = substr(name, 1, 20) || '-legacy-' || id || '-' || lower(hex(randomblob(4))),
                    is_enabled = 0
                WHERE id NOT IN (SELECT MIN(id) FROM link_routes GROUP BY link_id, name);
                UPDATE click_events
                SET event_id = lower(hex(randomblob(16)))
                WHERE event_id = '00000000-0000-0000-0000-000000000000' OR event_id = '';
                """);

            migrationBuilder.CreateIndex(
                name: "ux_short_domains_default_slot",
                table: "short_domains",
                column: "default_slot",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_link_routes_link_name",
                table: "link_routes",
                columns: new[] { "link_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_click_events_clicked_id",
                table: "click_events",
                columns: new[] { "clicked_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_click_events_link_id",
                table: "click_events",
                columns: new[] { "link_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_click_events_event_id",
                table: "click_events",
                column: "event_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "click_retention_leases");

            migrationBuilder.DropIndex(
                name: "ux_short_domains_default_slot",
                table: "short_domains");

            migrationBuilder.DropIndex(
                name: "ux_link_routes_link_name",
                table: "link_routes");

            migrationBuilder.DropIndex(
                name: "ix_click_events_clicked_id",
                table: "click_events");

            migrationBuilder.DropIndex(
                name: "ix_click_events_link_id",
                table: "click_events");

            migrationBuilder.DropIndex(
                name: "ux_click_events_event_id",
                table: "click_events");

            migrationBuilder.DropColumn(
                name: "token_version",
                table: "users");

            migrationBuilder.DropColumn(
                name: "concurrency_token",
                table: "short_links");

            migrationBuilder.DropColumn(
                name: "default_slot",
                table: "short_domains");

            migrationBuilder.DropColumn(
                name: "event_id",
                table: "click_events");
        }
    }
}
