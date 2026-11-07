using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChronoAudit.Sample.WebApi.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "chronoaudit");

            migrationBuilder.EnsureSchema(
                name: "library");

            migrationBuilder.CreateTable(
                name: "ApiMutationIntents",
                schema: "chronoaudit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ActAsUserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SessionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Method = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    Path = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    EndpointKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    BodySha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BodyLength = table.Column<long>(type: "bigint", nullable: false),
                    PreviousHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PayloadHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ChainIndex = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiMutationIntents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiRequestLogs",
                schema: "chronoaudit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ActAsUserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SessionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CalendarYear = table.Column<short>(type: "smallint", nullable: true),
                    CalendarMonth = table.Column<short>(type: "smallint", nullable: true),
                    CalendarDay = table.Column<short>(type: "smallint", nullable: true),
                    CalendarQuarter = table.Column<short>(type: "smallint", nullable: true),
                    HttpMethod = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    Path = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    EndpointKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    QueryString = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    StatusCode = table.Column<short>(type: "smallint", nullable: false),
                    ErrorCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    HasBody = table.Column<bool>(type: "bit", nullable: false),
                    PreviousHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PayloadHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ChainIndex = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiRequestLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditLogEntries",
                schema: "library",
                columns: table => new
                {
                    SequenceNumber = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorUserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ResourceId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    OldValues = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NewValues = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Metadata = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CalendarDate = table.Column<int>(type: "int", nullable: false),
                    PreviousHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PayloadHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ChainIndex = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogEntries", x => x.SequenceNumber);
                });

            migrationBuilder.CreateTable(
                name: "Books",
                schema: "library",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Title = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Isbn = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Author = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedByName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiedByName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeletedByName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Books", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExportJobs",
                schema: "chronoaudit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedByUserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Producer = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Format = table.Column<short>(type: "smallint", nullable: false),
                    ParametersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ScopeKind = table.Column<int>(type: "int", nullable: false),
                    ScopeGroupId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    State = table.Column<short>(type: "smallint", nullable: false),
                    CheckpointCursor = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    CheckpointBytes = table.Column<long>(type: "bigint", nullable: false),
                    RowCount = table.Column<long>(type: "bigint", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LeaseOwner = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    LeaseUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    ErrorCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FinishedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExportJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Loans",
                schema: "library",
                columns: table => new
                {
                    BookId = table.Column<int>(type: "int", nullable: false),
                    MemberId = table.Column<int>(type: "int", nullable: false),
                    LoanedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DueAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReturnedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedByName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiedByName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeletedByName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Loans", x => new { x.BookId, x.MemberId, x.LoanedAtUtc });
                });

            migrationBuilder.CreateTable(
                name: "Members",
                schema: "library",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FullName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    JoinedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedByName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiedByName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeletedByName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Members", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SavedReports",
                schema: "chronoaudit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DefinitionVersion = table.Column<int>(type: "int", nullable: false),
                    SharedWithGroupId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedReports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TimelineEvents",
                schema: "chronoaudit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SchemaVersion = table.Column<short>(type: "smallint", nullable: false),
                    SubjectType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SubjectId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    GroupKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CalendarDate = table.Column<int>(type: "int", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ActorUserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ActorDisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ActorRole = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    OnBehalfOfUserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AccessGroupId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsBackfilled = table.Column<bool>(type: "bit", nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimelineEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TimelineParticipants",
                schema: "chronoaudit",
                columns: table => new
                {
                    GroupKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FirstSeenAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimelineParticipants", x => new { x.GroupKey, x.UserId, x.Role });
                });

            migrationBuilder.CreateTable(
                name: "ApiRequestBodies",
                schema: "chronoaudit",
                columns: table => new
                {
                    ApiRequestLogId = table.Column<long>(type: "bigint", nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsTruncated = table.Column<bool>(type: "bit", nullable: false),
                    FileMetadata = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiRequestBodies", x => x.ApiRequestLogId);
                    table.ForeignKey(
                        name: "FK_ApiRequestBodies_ApiRequestLogs_ApiRequestLogId",
                        column: x => x.ApiRequestLogId,
                        principalSchema: "chronoaudit",
                        principalTable: "ApiRequestLogs",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "MemberContacts",
                schema: "library",
                columns: table => new
                {
                    MemberId = table.Column<int>(type: "int", nullable: false),
                    PhoneNumber = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedByName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiedByName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeletedByName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemberContacts", x => x.MemberId);
                    table.ForeignKey(
                        name: "FK_MemberContacts_Members_MemberId",
                        column: x => x.MemberId,
                        principalSchema: "library",
                        principalTable: "Members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiMutationIntents_StartedAtUtc",
                schema: "chronoaudit",
                table: "ApiMutationIntents",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ApiMutationIntents_Unhashed",
                schema: "chronoaudit",
                table: "ApiMutationIntents",
                column: "Id",
                filter: "[PayloadHash] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ApiMutationIntents_User",
                schema: "chronoaudit",
                table: "ApiMutationIntents",
                columns: new[] { "UserId", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_ApiMutationIntents_ChainIndex",
                schema: "chronoaudit",
                table: "ApiMutationIntents",
                column: "ChainIndex",
                unique: true,
                filter: "[ChainIndex] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_ApiMutationIntents_RequestId",
                schema: "chronoaudit",
                table: "ApiMutationIntents",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiRequestLogs_Correlation",
                schema: "chronoaudit",
                table: "ApiRequestLogs",
                column: "CorrelationId",
                filter: "[CorrelationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ApiRequestLogs_Endpoint",
                schema: "chronoaudit",
                table: "ApiRequestLogs",
                columns: new[] { "EndpointKey", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiRequestLogs_OccurredAtUtc",
                schema: "chronoaudit",
                table: "ApiRequestLogs",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ApiRequestLogs_Session",
                schema: "chronoaudit",
                table: "ApiRequestLogs",
                columns: new[] { "SessionId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiRequestLogs_Unhashed",
                schema: "chronoaudit",
                table: "ApiRequestLogs",
                column: "Id",
                filter: "[PayloadHash] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ApiRequestLogs_User",
                schema: "chronoaudit",
                table: "ApiRequestLogs",
                columns: new[] { "UserId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_ApiRequestLogs_ChainIndex",
                schema: "chronoaudit",
                table: "ApiRequestLogs",
                column: "ChainIndex",
                unique: true,
                filter: "[ChainIndex] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_ApiRequestLogs_RequestId",
                schema: "chronoaudit",
                table: "ApiRequestLogs",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogEntries_Actor",
                schema: "library",
                table: "AuditLogEntries",
                columns: new[] { "ActorUserId", "OccurredAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogEntries_CalendarDate",
                schema: "library",
                table: "AuditLogEntries",
                columns: new[] { "CalendarDate", "SequenceNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogEntries_Occurred",
                schema: "library",
                table: "AuditLogEntries",
                columns: new[] { "OccurredAtUtc", "SequenceNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogEntries_Resource",
                schema: "library",
                table: "AuditLogEntries",
                columns: new[] { "ResourceType", "ResourceId", "SequenceNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogEntries_Unhashed",
                schema: "library",
                table: "AuditLogEntries",
                column: "SequenceNumber",
                filter: "[PayloadHash] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_AuditLogEntries_ChainIndex",
                schema: "library",
                table: "AuditLogEntries",
                column: "ChainIndex",
                unique: true,
                filter: "[ChainIndex] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_AuditLogEntries_Id",
                schema: "library",
                table: "AuditLogEntries",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExportJobs_State",
                schema: "chronoaudit",
                table: "ExportJobs",
                columns: new[] { "State", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ExportJobs_User",
                schema: "chronoaudit",
                table: "ExportJobs",
                columns: new[] { "RequestedByUserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SavedReports_Owner",
                schema: "chronoaudit",
                table: "SavedReports",
                columns: new[] { "OwnerUserId", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SavedReports_SharedGroup",
                schema: "chronoaudit",
                table: "SavedReports",
                columns: new[] { "SharedWithGroupId", "UpdatedAtUtc" },
                filter: "[SharedWithGroupId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TimelineEvents_AccessGroup",
                schema: "chronoaudit",
                table: "TimelineEvents",
                columns: new[] { "AccessGroupId", "OccurredAtUtc" },
                filter: "[AccessGroupId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TimelineEvents_Actor",
                schema: "chronoaudit",
                table: "TimelineEvents",
                columns: new[] { "ActorUserId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TimelineEvents_Correlation",
                schema: "chronoaudit",
                table: "TimelineEvents",
                column: "CorrelationId",
                filter: "[CorrelationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TimelineEvents_Group",
                schema: "chronoaudit",
                table: "TimelineEvents",
                columns: new[] { "GroupKey", "OccurredAtUtc", "Id" },
                filter: "[GroupKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TimelineEvents_OnBehalfOf",
                schema: "chronoaudit",
                table: "TimelineEvents",
                columns: new[] { "OnBehalfOfUserId", "OccurredAtUtc" },
                filter: "[OnBehalfOfUserId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TimelineEvents_Subject",
                schema: "chronoaudit",
                table: "TimelineEvents",
                columns: new[] { "SubjectType", "SubjectId", "OccurredAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TimelineEvents_Type",
                schema: "chronoaudit",
                table: "TimelineEvents",
                columns: new[] { "EventType", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_TimelineEvents_SourceEventId",
                schema: "chronoaudit",
                table: "TimelineEvents",
                column: "SourceEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TimelineParticipants_User",
                schema: "chronoaudit",
                table: "TimelineParticipants",
                columns: new[] { "UserId", "GroupKey" });

            // The tamper-resistant triggers are SQL, not model: the model snapshot does not know them.
            migrationBuilder.CreateChronoAuditAuditGuard("library");
            migrationBuilder.CreateChronoAuditStoreGuards("chronoaudit");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The guard triggers are SQL, not model: drop them first, so the tables can go.
            migrationBuilder.DropChronoAuditStoreGuards("chronoaudit");
            migrationBuilder.DropChronoAuditAuditGuard("library");

            migrationBuilder.DropTable(
                name: "ApiMutationIntents",
                schema: "chronoaudit");

            migrationBuilder.DropTable(
                name: "ApiRequestBodies",
                schema: "chronoaudit");

            migrationBuilder.DropTable(
                name: "AuditLogEntries",
                schema: "library");

            migrationBuilder.DropTable(
                name: "Books",
                schema: "library");

            migrationBuilder.DropTable(
                name: "ExportJobs",
                schema: "chronoaudit");

            migrationBuilder.DropTable(
                name: "Loans",
                schema: "library");

            migrationBuilder.DropTable(
                name: "MemberContacts",
                schema: "library");

            migrationBuilder.DropTable(
                name: "SavedReports",
                schema: "chronoaudit");

            migrationBuilder.DropTable(
                name: "TimelineEvents",
                schema: "chronoaudit");

            migrationBuilder.DropTable(
                name: "TimelineParticipants",
                schema: "chronoaudit");

            migrationBuilder.DropTable(
                name: "ApiRequestLogs",
                schema: "chronoaudit");

            migrationBuilder.DropTable(
                name: "Members",
                schema: "library");
        }
    }
}
