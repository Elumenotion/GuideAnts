using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageAttachmentPaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MessageAttachments_MessageId_NotebookFileId",
                table: "MessageAttachments");

            migrationBuilder.AlterColumn<Guid>(
                name: "NotebookFileId",
                table: "MessageAttachments",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<string>(
                name: "RelativePath",
                table: "MessageAttachments",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UploadType",
                table: "MessageAttachments",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageAttachments_MessageId_NotebookFileId",
                table: "MessageAttachments",
                columns: new[] { "MessageId", "NotebookFileId" },
                unique: true,
                filter: "[NotebookFileId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MessageAttachments_MessageId_NotebookFileId",
                table: "MessageAttachments");

            migrationBuilder.DropColumn(
                name: "RelativePath",
                table: "MessageAttachments");

            migrationBuilder.DropColumn(
                name: "UploadType",
                table: "MessageAttachments");

            migrationBuilder.AlterColumn<Guid>(
                name: "NotebookFileId",
                table: "MessageAttachments",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageAttachments_MessageId_NotebookFileId",
                table: "MessageAttachments",
                columns: new[] { "MessageId", "NotebookFileId" },
                unique: true);
        }
    }
}
