using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260819150000_AddMessageModelContextEviction")]
    public partial class AddMessageModelContextEviction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ModelContextEviction",
                table: "NotebookConversationMessages",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Message_ModelContextEviction",
                table: "NotebookConversationMessages",
                sql: "[ModelContextEviction] IS NULL OR [ModelContextEviction] IN ('message', 'thinking')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Message_ModelContextEviction",
                table: "NotebookConversationMessages");

            migrationBuilder.DropColumn(
                name: "ModelContextEviction",
                table: "NotebookConversationMessages");
        }
    }
}
