using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuideAntsApi.DataModel.Migrations
{
    /// <inheritdoc />
    public partial class AddHostFolderMounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HostFolderMounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotebookId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Scope = table.Column<int>(type: "int", nullable: false),
                    SourceKind = table.Column<int>(type: "int", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    LeafName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    MountKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SourceSpec = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContainerSourcePath = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    NetworkDevice = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    NetworkOptions = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    CredentialRef = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    RemovedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostFolderMounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HostFolderMounts_Notebooks_NotebookId",
                        column: x => x.NotebookId,
                        principalTable: "Notebooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HostFolderMounts_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HostFolderMounts_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HostFolderMountLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HostFolderMountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotebookId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LinkRelativePath = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    LinkPhysicalPath = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    LastLinkedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastCheckedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostFolderMountLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HostFolderMountLinks_HostFolderMounts_HostFolderMountId",
                        column: x => x.HostFolderMountId,
                        principalTable: "HostFolderMounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HostFolderMountLinks_Notebooks_NotebookId",
                        column: x => x.NotebookId,
                        principalTable: "Notebooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HostFolderMountLinks_HostFolderMountId_NotebookId",
                table: "HostFolderMountLinks",
                columns: new[] { "HostFolderMountId", "NotebookId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HostFolderMountLinks_NotebookId_LinkRelativePath_ActiveUnique",
                table: "HostFolderMountLinks",
                columns: new[] { "NotebookId", "LinkRelativePath" },
                unique: true,
                filter: "[Status] IN (0, 1, 2)");

            migrationBuilder.CreateIndex(
                name: "IX_HostFolderMounts_CreatedByUserId",
                table: "HostFolderMounts",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_HostFolderMounts_MountKey",
                table: "HostFolderMounts",
                column: "MountKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HostFolderMounts_NotebookId",
                table: "HostFolderMounts",
                column: "NotebookId");

            migrationBuilder.CreateIndex(
                name: "IX_HostFolderMounts_ProjectId_Status",
                table: "HostFolderMounts",
                columns: new[] { "ProjectId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HostFolderMountLinks");

            migrationBuilder.DropTable(
                name: "HostFolderMounts");
        }
    }
}
