using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFuture.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenserveFulfilmentIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OpenserveSubscriberReferenceNumber",
                table: "NetworkAccounts",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PackageOpenserveMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServicePackageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OpenserveProductName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Sku = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Capacity = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CapacityUom = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    OpenserveProductOfferingId = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    OpenserveProductSpecificationId = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageOpenserveMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PackageOpenserveMappings_ServicePackages_ServicePackageId",
                        column: x => x.ServicePackageId,
                        principalTable: "ServicePackages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OpenserveOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalReferenceNumber = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    LastMessageId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    OpenserveOrderId = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    OpenserveOrderName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    SubscriberReferenceNumber = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    OrderType = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    RawState = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    NormalizedStatus = table.Column<int>(type: "int", nullable: false),
                    SubmittedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastOpenserveUpdateAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSuccessfulSyncAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    LastFailureCode = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    LastFailureMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PackageOpenserveMappingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsTerminal = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenserveOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenserveOrders_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OpenserveOrders_PackageOpenserveMappings_PackageOpenserveMappingId",
                        column: x => x.PackageOpenserveMappingId,
                        principalTable: "PackageOpenserveMappings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OpenserveIntegrationLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OpenserveOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Direction = table.Column<int>(type: "int", nullable: false),
                    OperationType = table.Column<int>(type: "int", nullable: false),
                    MessageId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    HttpMethod = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    Endpoint = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RequestHeadersJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    RequestBodyJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResponseStatusCode = table.Column<int>(type: "int", nullable: true),
                    ResponseBodyJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsSuccess = table.Column<bool>(type: "bit", nullable: false),
                    ErrorSummary = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenserveIntegrationLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenserveIntegrationLogs_OpenserveOrders_OpenserveOrderId",
                        column: x => x.OpenserveOrderId,
                        principalTable: "OpenserveOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OpenserveOrderStatusHistories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OpenserveOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OpenserveEventId = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    EventType = table.Column<int>(type: "int", nullable: false),
                    PreviousRawState = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    NewRawState = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    NormalizedStatus = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    InstallationStatus = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AppointmentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReceivedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EventOccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IntegrationLogId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProcessingResult = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    NotificationTriggered = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenserveOrderStatusHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenserveOrderStatusHistories_OpenserveIntegrationLogs_IntegrationLogId",
                        column: x => x.IntegrationLogId,
                        principalTable: "OpenserveIntegrationLogs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OpenserveOrderStatusHistories_OpenserveOrders_OpenserveOrderId",
                        column: x => x.OpenserveOrderId,
                        principalTable: "OpenserveOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NetworkAccounts_OpenserveSubscriberReferenceNumber",
                table: "NetworkAccounts",
                column: "OpenserveSubscriberReferenceNumber",
                unique: true,
                filter: "[OpenserveSubscriberReferenceNumber] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OpenserveIntegrationLogs_MessageId",
                table: "OpenserveIntegrationLogs",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenserveIntegrationLogs_OccurredAtUtc",
                table: "OpenserveIntegrationLogs",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_OpenserveIntegrationLogs_OpenserveOrderId",
                table: "OpenserveIntegrationLogs",
                column: "OpenserveOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenserveIntegrationLogs_OperationType",
                table: "OpenserveIntegrationLogs",
                column: "OperationType");

            migrationBuilder.CreateIndex(
                name: "IX_OpenserveOrders_ExternalReferenceNumber",
                table: "OpenserveOrders",
                column: "ExternalReferenceNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpenserveOrders_IsTerminal",
                table: "OpenserveOrders",
                column: "IsTerminal");

            migrationBuilder.CreateIndex(
                name: "IX_OpenserveOrders_NormalizedStatus",
                table: "OpenserveOrders",
                column: "NormalizedStatus");

            migrationBuilder.CreateIndex(
                name: "IX_OpenserveOrders_OpenserveOrderId",
                table: "OpenserveOrders",
                column: "OpenserveOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenserveOrders_OrderId",
                table: "OpenserveOrders",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenserveOrders_PackageOpenserveMappingId",
                table: "OpenserveOrders",
                column: "PackageOpenserveMappingId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenserveOrderStatusHistories_IntegrationLogId",
                table: "OpenserveOrderStatusHistories",
                column: "IntegrationLogId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenserveOrderStatusHistories_OpenserveEventId",
                table: "OpenserveOrderStatusHistories",
                column: "OpenserveEventId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenserveOrderStatusHistories_OpenserveOrderId",
                table: "OpenserveOrderStatusHistories",
                column: "OpenserveOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenserveOrderStatusHistories_OrderId",
                table: "OpenserveOrderStatusHistories",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenserveOrderStatusHistories_ReceivedAtUtc",
                table: "OpenserveOrderStatusHistories",
                column: "ReceivedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PackageOpenserveMappings_IsEnabled",
                table: "PackageOpenserveMappings",
                column: "IsEnabled");

            migrationBuilder.CreateIndex(
                name: "IX_PackageOpenserveMappings_ServicePackageId",
                table: "PackageOpenserveMappings",
                column: "ServicePackageId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OpenserveOrderStatusHistories");

            migrationBuilder.DropTable(
                name: "OpenserveIntegrationLogs");

            migrationBuilder.DropTable(
                name: "OpenserveOrders");

            migrationBuilder.DropTable(
                name: "PackageOpenserveMappings");

            migrationBuilder.DropIndex(
                name: "IX_NetworkAccounts_OpenserveSubscriberReferenceNumber",
                table: "NetworkAccounts");

            migrationBuilder.DropColumn(
                name: "OpenserveSubscriberReferenceNumber",
                table: "NetworkAccounts");
        }
    }
}
