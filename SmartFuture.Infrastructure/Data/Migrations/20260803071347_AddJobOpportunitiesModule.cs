using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJobOpportunitiesModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JobAlertDeliveryLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Frequency = table.Column<int>(type: "int", nullable: false),
                    WindowStartUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    WindowEndUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    JobCount = table.Column<int>(type: "int", nullable: false),
                    RecipientEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    FailureMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NotificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobAlertDeliveryLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobAlertDeliveryLogs_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobAlertPreferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsSubscribed = table.Column<bool>(type: "bit", nullable: false),
                    Frequency = table.Column<int>(type: "int", nullable: false),
                    CategoriesJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    LocationsJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    KeywordsJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    LastSentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UnsubscribedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UnsubscribeToken = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobAlertPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobAlertPreferences_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobModuleSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobDetailsSubscribersOnly = table.Column<bool>(type: "bit", nullable: false),
                    JobAlertsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    JobsModuleEnabled = table.Column<bool>(type: "bit", nullable: false),
                    AutoImportEnabled = table.Column<bool>(type: "bit", nullable: false),
                    ImportIntervalMinutes = table.Column<int>(type: "int", nullable: false),
                    ExpiredJobRetentionDays = table.Column<int>(type: "int", nullable: false),
                    PublicDisclaimer = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobModuleSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    SourceUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    SourceType = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DefaultCategory = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DefaultLocation = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CrawlFrequencyMinutes = table.Column<int>(type: "int", nullable: true),
                    AutoPublish = table.Column<bool>(type: "bit", nullable: false),
                    MaxJobsPerRun = table.Column<int>(type: "int", nullable: false),
                    MaxPagesPerRun = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    LastCheckedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSuccessAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastFailureAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastFailureMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ConsecutiveFailureCount = table.Column<int>(type: "int", nullable: false),
                    TotalJobsImported = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobSources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobSubscriberDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentType = table.Column<int>(type: "int", nullable: false),
                    ObjectKey = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    FileUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    FileName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false),
                    UploadedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobSubscriberDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobSubscriberDocuments_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobSubscriberProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PreferredFirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ContactPhone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    ContactPhoneNormalized = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    CurrentCity = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CurrentProvince = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CurrentCountry = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LinkedInProfileUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    WebsiteUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SalaryExpectations = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    HighestQualification = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    YearsOfExperience = table.Column<int>(type: "int", nullable: true),
                    PreferredCategoriesJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PreferredLocationsJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CvObjectKey = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CvFileUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CvFileName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CvContentType = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CvSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    CvUploadedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CoverLetterObjectKey = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CoverLetterFileUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CoverLetterFileName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CoverLetterContentType = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CoverLetterSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    CoverLetterUploadedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobSubscriberProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobSubscriberProfiles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobImportRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Trigger = table.Column<int>(type: "int", nullable: false),
                    TriggeredByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    JobsFound = table.Column<int>(type: "int", nullable: false),
                    JobsCreated = table.Column<int>(type: "int", nullable: false),
                    JobsUpdated = table.Column<int>(type: "int", nullable: false),
                    JobsSkipped = table.Column<int>(type: "int", nullable: false),
                    JobsExpired = table.Column<int>(type: "int", nullable: false),
                    IsSuccess = table.Column<bool>(type: "bit", nullable: false),
                    FailureMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    HttpStatusCode = table.Column<int>(type: "int", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobImportRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobImportRuns_JobSources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "JobSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "JobOpportunities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Location = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Country = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Province = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Category = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    WorkplaceType = table.Column<int>(type: "int", nullable: false),
                    EmploymentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SalaryText = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Summary = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DescriptionHtml = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DescriptionText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RequirementsText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApplicationInstructions = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ApplyUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ApplyEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    SourceUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SourceName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExternalId = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PostedDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClosingDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ImportedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    IsFeatured = table.Column<bool>(type: "bit", nullable: false),
                    IsManuallyEdited = table.Column<bool>(type: "bit", nullable: false),
                    LogoUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    TagsJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RawContentSnapshot = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ViewCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobOpportunities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobOpportunities_JobSources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "JobSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobAlertDeliveryLogs_CreatedAtUtc",
                table: "JobAlertDeliveryLogs",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_JobAlertDeliveryLogs_Status",
                table: "JobAlertDeliveryLogs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_JobAlertDeliveryLogs_UserId_WindowStartUtc",
                table: "JobAlertDeliveryLogs",
                columns: new[] { "UserId", "WindowStartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_JobAlertPreferences_IsSubscribed_Frequency",
                table: "JobAlertPreferences",
                columns: new[] { "IsSubscribed", "Frequency" });

            migrationBuilder.CreateIndex(
                name: "IX_JobAlertPreferences_UnsubscribeToken",
                table: "JobAlertPreferences",
                column: "UnsubscribeToken",
                unique: true,
                filter: "[UnsubscribeToken] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_JobAlertPreferences_UserId",
                table: "JobAlertPreferences",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobImportRuns_SourceId",
                table: "JobImportRuns",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_JobImportRuns_StartedAtUtc",
                table: "JobImportRuns",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_JobImportRuns_Status",
                table: "JobImportRuns",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_JobOpportunities_Category",
                table: "JobOpportunities",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_JobOpportunities_City",
                table: "JobOpportunities",
                column: "City");

            migrationBuilder.CreateIndex(
                name: "IX_JobOpportunities_ClosingDateUtc",
                table: "JobOpportunities",
                column: "ClosingDateUtc");

            migrationBuilder.CreateIndex(
                name: "IX_JobOpportunities_CreatedAtUtc",
                table: "JobOpportunities",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_JobOpportunities_Fingerprint",
                table: "JobOpportunities",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobOpportunities_IsFeatured",
                table: "JobOpportunities",
                column: "IsFeatured");

            migrationBuilder.CreateIndex(
                name: "IX_JobOpportunities_PostedDateUtc",
                table: "JobOpportunities",
                column: "PostedDateUtc");

            migrationBuilder.CreateIndex(
                name: "IX_JobOpportunities_Province",
                table: "JobOpportunities",
                column: "Province");

            migrationBuilder.CreateIndex(
                name: "IX_JobOpportunities_Slug",
                table: "JobOpportunities",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobOpportunities_SourceId",
                table: "JobOpportunities",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_JobOpportunities_Status",
                table: "JobOpportunities",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_JobOpportunities_Status_ClosingDateUtc_PostedDateUtc",
                table: "JobOpportunities",
                columns: new[] { "Status", "ClosingDateUtc", "PostedDateUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_JobOpportunities_WorkplaceType",
                table: "JobOpportunities",
                column: "WorkplaceType");

            migrationBuilder.CreateIndex(
                name: "IX_JobSources_IsActive",
                table: "JobSources",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_JobSources_LastCheckedAtUtc",
                table: "JobSources",
                column: "LastCheckedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_JobSources_SourceType",
                table: "JobSources",
                column: "SourceType");

            migrationBuilder.CreateIndex(
                name: "IX_JobSources_SourceUrl",
                table: "JobSources",
                column: "SourceUrl",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobSubscriberDocuments_UploadedAtUtc",
                table: "JobSubscriberDocuments",
                column: "UploadedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_JobSubscriberDocuments_UserId_DocumentType_IsCurrent",
                table: "JobSubscriberDocuments",
                columns: new[] { "UserId", "DocumentType", "IsCurrent" });

            migrationBuilder.CreateIndex(
                name: "IX_JobSubscriberProfiles_CompletedAtUtc",
                table: "JobSubscriberProfiles",
                column: "CompletedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_JobSubscriberProfiles_UserId",
                table: "JobSubscriberProfiles",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobAlertDeliveryLogs");

            migrationBuilder.DropTable(
                name: "JobAlertPreferences");

            migrationBuilder.DropTable(
                name: "JobImportRuns");

            migrationBuilder.DropTable(
                name: "JobModuleSettings");

            migrationBuilder.DropTable(
                name: "JobOpportunities");

            migrationBuilder.DropTable(
                name: "JobSubscriberDocuments");

            migrationBuilder.DropTable(
                name: "JobSubscriberProfiles");

            migrationBuilder.DropTable(
                name: "JobSources");
        }
    }
}
