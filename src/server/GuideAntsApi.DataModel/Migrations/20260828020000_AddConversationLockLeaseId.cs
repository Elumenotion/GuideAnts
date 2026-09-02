using System;
using GuideAntsApi.DataModel;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260828020000_AddConversationLockLeaseId")]
    public partial class AddConversationLockLeaseId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LeaseId",
                table: "ConversationLocks",
                type: "uniqueidentifier",
                nullable: false,
                defaultValueSql: "NEWSEQUENTIALID()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LeaseId",
                table: "ConversationLocks");
        }
    }
}
