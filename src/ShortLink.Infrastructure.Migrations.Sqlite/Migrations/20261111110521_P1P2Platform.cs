using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShortLink.Infrastructure.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class P1P2Platform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "updated_at_utc",
                table: "users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "anti_block_enabled",
                table: "short_links",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "bot_policy",
                table: "short_links",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "conversion_secret",
                table: "short_links",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fallback_url",
                table: "short_links",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ip_allow_list",
                table: "short_links",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ip_block_list",
                table: "short_links",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "max_clicks",
                table: "short_links",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "starts_at_utc",
                table: "short_links",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "workspace_id",
                table: "short_links",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "route_id",
                table: "click_events",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "workspace_id",
                table: "click_events",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "conversion_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    workspace_id = table.Column<long>(type: "INTEGER", nullable: false),
                    link_id = table.Column<long>(type: "INTEGER", nullable: false),
                    route_id = table.Column<long>(type: "INTEGER", nullable: true),
                    event_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    metric = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    value = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    ip = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversion_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "link_routes",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    link_id = table.Column<long>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    priority = table.Column<int>(type: "INTEGER", nullable: false),
                    weight = table.Column<int>(type: "INTEGER", nullable: false),
                    target_url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    conditions_json = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    is_enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    click_count = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_link_routes", x => x.id);
                    table.ForeignKey(
                        name: "FK_link_routes_short_links_link_id",
                        column: x => x.link_id,
                        principalTable: "short_links",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "security_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    workspace_id = table.Column<long>(type: "INTEGER", nullable: false),
                    link_id = table.Column<long>(type: "INTEGER", nullable: true),
                    code = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    type = table.Column<int>(type: "INTEGER", nullable: false),
                    message = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ip = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    user_agent = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    country = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workspace_members",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    workspace_id = table.Column<long>(type: "INTEGER", nullable: false),
                    user_id = table.Column<long>(type: "INTEGER", nullable: false),
                    role = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspace_members", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workspaces",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    slug = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    is_default = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspaces", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_short_links_workspace_created",
                table: "short_links",
                columns: new[] { "workspace_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_short_links_workspace_creator",
                table: "short_links",
                columns: new[] { "workspace_id", "created_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_click_events_link_route",
                table: "click_events",
                columns: new[] { "link_id", "route_id" });

            migrationBuilder.CreateIndex(
                name: "ix_click_events_workspace_date",
                table: "click_events",
                columns: new[] { "workspace_id", "click_date" });

            migrationBuilder.CreateIndex(
                name: "ix_conversion_events_workspace_created",
                table: "conversion_events",
                columns: new[] { "workspace_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_conversion_events_link_event",
                table: "conversion_events",
                columns: new[] { "link_id", "event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_link_routes_link_priority",
                table: "link_routes",
                columns: new[] { "link_id", "priority" });

            migrationBuilder.CreateIndex(
                name: "ix_security_events_link_created",
                table: "security_events",
                columns: new[] { "link_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_security_events_workspace_created",
                table: "security_events",
                columns: new[] { "workspace_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_workspace_members_workspace_user",
                table: "workspace_members",
                columns: new[] { "workspace_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_workspaces_slug",
                table: "workspaces",
                column: "slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conversion_events");

            migrationBuilder.DropTable(
                name: "link_routes");

            migrationBuilder.DropTable(
                name: "security_events");

            migrationBuilder.DropTable(
                name: "workspace_members");

            migrationBuilder.DropTable(
                name: "workspaces");

            migrationBuilder.DropIndex(
                name: "ix_short_links_workspace_created",
                table: "short_links");

            migrationBuilder.DropIndex(
                name: "ix_short_links_workspace_creator",
                table: "short_links");

            migrationBuilder.DropIndex(
                name: "ix_click_events_link_route",
                table: "click_events");

            migrationBuilder.DropIndex(
                name: "ix_click_events_workspace_date",
                table: "click_events");

            migrationBuilder.DropColumn(
                name: "updated_at_utc",
                table: "users");

            migrationBuilder.DropColumn(
                name: "anti_block_enabled",
                table: "short_links");

            migrationBuilder.DropColumn(
                name: "bot_policy",
                table: "short_links");

            migrationBuilder.DropColumn(
                name: "conversion_secret",
                table: "short_links");

            migrationBuilder.DropColumn(
                name: "fallback_url",
                table: "short_links");

            migrationBuilder.DropColumn(
                name: "ip_allow_list",
                table: "short_links");

            migrationBuilder.DropColumn(
                name: "ip_block_list",
                table: "short_links");

            migrationBuilder.DropColumn(
                name: "max_clicks",
                table: "short_links");

            migrationBuilder.DropColumn(
                name: "starts_at_utc",
                table: "short_links");

            migrationBuilder.DropColumn(
                name: "workspace_id",
                table: "short_links");

            migrationBuilder.DropColumn(
                name: "route_id",
                table: "click_events");

            migrationBuilder.DropColumn(
                name: "workspace_id",
                table: "click_events");
        }
    }
}
