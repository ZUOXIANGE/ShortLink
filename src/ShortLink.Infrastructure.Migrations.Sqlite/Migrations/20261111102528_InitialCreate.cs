using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShortLink.Infrastructure.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "click_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    link_id = table.Column<long>(type: "INTEGER", nullable: false),
                    domain_id = table.Column<long>(type: "INTEGER", nullable: false),
                    code = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    clicked_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    click_date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    visitor_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ip = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    user_agent = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    referer = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    country = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    province = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    device = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    browser = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    operating_system = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    is_bot = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_click_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "short_domains",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    host = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    scheme = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    is_default = table.Column<bool>(type: "INTEGER", nullable: false),
                    is_enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_short_domains", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "short_links",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    domain_id = table.Column<long>(type: "INTEGER", nullable: false),
                    code = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    target_url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    redirect_type = table.Column<int>(type: "INTEGER", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    password_hash = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    remark = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    click_count = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    last_accessed_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_by_user_id = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_short_links", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    user_name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    password_hash = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    role = table.Column<int>(type: "INTEGER", nullable: false),
                    is_enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    last_login_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_click_events_click_date",
                table: "click_events",
                column: "click_date");

            migrationBuilder.CreateIndex(
                name: "ix_click_events_date_visitor",
                table: "click_events",
                columns: new[] { "click_date", "visitor_hash" });

            migrationBuilder.CreateIndex(
                name: "ix_click_events_link_date",
                table: "click_events",
                columns: new[] { "link_id", "click_date" });

            migrationBuilder.CreateIndex(
                name: "ux_short_domains_host",
                table: "short_domains",
                column: "host",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_short_links_click_count",
                table: "short_links",
                column: "click_count");

            migrationBuilder.CreateIndex(
                name: "ix_short_links_code",
                table: "short_links",
                column: "code");

            migrationBuilder.CreateIndex(
                name: "ix_short_links_expires_at",
                table: "short_links",
                column: "expires_at_utc");

            migrationBuilder.CreateIndex(
                name: "ux_short_links_domain_code",
                table: "short_links",
                columns: new[] { "domain_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_users_user_name",
                table: "users",
                column: "user_name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "click_events");

            migrationBuilder.DropTable(
                name: "short_domains");

            migrationBuilder.DropTable(
                name: "short_links");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
