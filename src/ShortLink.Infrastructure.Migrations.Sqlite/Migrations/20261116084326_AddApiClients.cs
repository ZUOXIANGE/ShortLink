using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShortLink.Infrastructure.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddApiClients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_clients",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    workspace_id = table.Column<long>(type: "INTEGER", nullable: false),
                    user_id = table.Column<long>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    key_id = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    secret_cipher = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    secret_hint = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    role = table.Column<int>(type: "INTEGER", nullable: false),
                    scopes = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    rate_limit_per_minute = table.Column<int>(type: "INTEGER", nullable: true),
                    allowed_ips = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    is_enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    last_used_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_by_user_id = table.Column<long>(type: "INTEGER", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    revoked_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_clients", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_api_clients_user",
                table: "api_clients",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_api_clients_workspace_enabled",
                table: "api_clients",
                columns: new[] { "workspace_id", "is_enabled" });

            migrationBuilder.CreateIndex(
                name: "ux_api_clients_key_id",
                table: "api_clients",
                column: "key_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_clients");
        }
    }
}
