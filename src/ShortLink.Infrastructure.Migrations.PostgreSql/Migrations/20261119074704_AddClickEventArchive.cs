using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShortLink.Infrastructure.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddClickEventArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "click_events_archive",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<long>(type: "bigint", nullable: false),
                    link_id = table.Column<long>(type: "bigint", nullable: false),
                    domain_id = table.Column<long>(type: "bigint", nullable: false),
                    route_id = table.Column<long>(type: "bigint", nullable: true),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    clicked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    click_date = table.Column<DateOnly>(type: "date", nullable: false),
                    visitor_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    referer = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    country = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    province = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    device = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    browser = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    operating_system = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    is_bot = table.Column<bool>(type: "boolean", nullable: false),
                    archived_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_click_events_archive", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_click_events_archive_click_date",
                table: "click_events_archive",
                column: "click_date");

            migrationBuilder.CreateIndex(
                name: "ix_click_events_archive_clicked_id",
                table: "click_events_archive",
                columns: new[] { "clicked_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_click_events_archive_link_date",
                table: "click_events_archive",
                columns: new[] { "link_id", "click_date" });

            migrationBuilder.CreateIndex(
                name: "ix_click_events_archive_workspace_date",
                table: "click_events_archive",
                columns: new[] { "workspace_id", "click_date" });

            migrationBuilder.CreateIndex(
                name: "ux_click_events_archive_event_id",
                table: "click_events_archive",
                column: "event_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "click_events_archive");
        }
    }
}
