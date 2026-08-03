using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Jobs;
using SmartFuture.Domain.Jobs;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Enums.Auditing;
using SmartFuture.Shared.Enums.Jobs;
using SmartFuture.Tests.Infrastructure;

namespace SmartFuture.Tests.Jobs;

// Permanent source deletion. The risk here is blast radius: a delete
// that reaches beyond the source — into another source's jobs, into
// hand-curated rows, or into subscriber data — is unrecoverable in
// production, so each boundary gets its own test.
public class JobSourceDeleteTests
{
    private readonly List<CreateAuditLogRequestDto> _audits = new();

    private JobSourceService BuildService(SqliteTestDbFixture fixture)
    {
        var audit = new Mock<IAuditService>();
        audit.Setup(a => a.LogAsync(It.IsAny<CreateAuditLogRequestDto>(), It.IsAny<CancellationToken>()))
            .Callback<CreateAuditLogRequestDto, CancellationToken>((dto, _) => _audits.Add(dto))
            .Returns(Task.CompletedTask);

        return new JobSourceService(fixture.AppDbContext, audit.Object, Mock.Of<ICurrentUserService>(),
            NullLogger<JobSourceService>.Instance);
    }

    private static JobSource NewSource(string name, string url) => new()
    {
        Id = Guid.NewGuid(),
        SourceName = name,
        SourceUrl = url,
        SourceType = JobSourceType.HtmlPage,
        IsActive = true,
        MaxJobsPerRun = 20,
        MaxPagesPerRun = 1
    };

    private static JobOpportunity NewJob(JobSource source, string title, JobOpportunityStatus status, bool manuallyEdited = false) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Slug = title.ToLowerInvariant().Replace(' ', '-') + "-" + Guid.NewGuid().ToString("N")[..6],
        Fingerprint = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
        SourceId = source.Id,
        SourceName = source.SourceName,
        SourceUrl = source.SourceUrl,
        Status = status,
        IsManuallyEdited = manuallyEdited,
        ImportedAtUtc = DateTime.UtcNow,
        LastSeenAtUtc = DateTime.UtcNow
    };

    // Two sources, so every test also proves the neighbour is untouched.
    private static async Task<(JobSource Target, JobSource Other)> SeedAsync(SqliteTestDbFixture fixture)
    {
        var target = NewSource("WorkJobs2", "https://workjob.co.za");
        var other = NewSource("WorkJob Full", "https://workjob.co.za/");

        fixture.DbContext.JobSources.AddRange(target, other);

        fixture.DbContext.JobOpportunities.AddRange(
            NewJob(target, "Draft One", JobOpportunityStatus.Draft),
            NewJob(target, "Draft Two", JobOpportunityStatus.Draft),
            NewJob(target, "Active One", JobOpportunityStatus.Active),
            NewJob(target, "Hidden One", JobOpportunityStatus.Hidden),
            NewJob(target, "Expired One", JobOpportunityStatus.Expired),
            NewJob(target, "Soft Deleted One", JobOpportunityStatus.Deleted),
            NewJob(target, "Curated One", JobOpportunityStatus.Active, manuallyEdited: true),
            NewJob(target, "Curated Two", JobOpportunityStatus.Draft, manuallyEdited: true),
            NewJob(other, "Neighbour Draft", JobOpportunityStatus.Draft),
            NewJob(other, "Neighbour Curated", JobOpportunityStatus.Active, manuallyEdited: true));

        fixture.DbContext.JobImportRuns.AddRange(
            new JobImportRun { Id = Guid.NewGuid(), SourceId = target.Id, SourceName = target.SourceName, StartedAtUtc = DateTime.UtcNow, Status = JobImportRunStatus.Succeeded },
            new JobImportRun { Id = Guid.NewGuid(), SourceId = target.Id, SourceName = target.SourceName, StartedAtUtc = DateTime.UtcNow, Status = JobImportRunStatus.Failed },
            new JobImportRun { Id = Guid.NewGuid(), SourceId = other.Id, SourceName = other.SourceName, StartedAtUtc = DateTime.UtcNow, Status = JobImportRunStatus.Succeeded });

        await fixture.DbContext.SaveChangesAsync();
        return (target, other);
    }

    // ─── 1. Preview ───────────────────────────────────────────────────

    [Fact]
    public async Task Delete_preview_reports_the_exact_blast_radius()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var (target, _) = await SeedAsync(fixture);

        var result = await BuildService(fixture).GetDeletePreviewAsync(target.Id);

        result.IsSuccess.Should().BeTrue();
        var p = result.Data!;

        p.SourceName.Should().Be("WorkJobs2");
        p.SourceUrl.Should().Be("https://workjob.co.za");
        p.DraftJobs.Should().Be(3, "two plain drafts plus one curated draft");
        p.ActiveJobs.Should().Be(2, "one plain active plus one curated active");
        p.HiddenJobs.Should().Be(1);
        p.ExpiredJobs.Should().Be(1);
        p.DeletedJobs.Should().Be(1);
        p.ManuallyEditedJobs.Should().Be(2);
        p.ImportRuns.Should().Be(2);

        p.WillDeleteJobs.Should().Be(6, "8 jobs on the source, 2 of them curated");
        p.WillPreserveManualJobs.Should().Be(2);
        p.WillDeleteImportRuns.Should().BeFalse("runs are history and always survive");
    }

    [Fact]
    public async Task Delete_preview_changes_nothing()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var (target, _) = await SeedAsync(fixture);

        await BuildService(fixture).GetDeletePreviewAsync(target.Id);

        (await fixture.DbContext.JobSources.CountAsync()).Should().Be(2);
        (await fixture.DbContext.JobOpportunities.CountAsync()).Should().Be(10);
        (await fixture.DbContext.JobImportRuns.CountAsync()).Should().Be(3);
    }

    // ─── 2 & 3. Default delete ────────────────────────────────────────

    [Fact]
    public async Task Delete_removes_imported_jobs_and_preserves_manually_edited_ones_by_default()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var (target, _) = await SeedAsync(fixture);

        var result = await BuildService(fixture).DeleteAsync(target.Id, deleteManuallyEditedJobs: false);

        result.IsSuccess.Should().BeTrue();
        result.Data!.SourceDeleted.Should().BeTrue();
        result.Data.JobsDeleted.Should().Be(6);
        result.Data.ManualJobsPreserved.Should().Be(2);

        (await fixture.DbContext.JobSources.AnyAsync(s => s.Id == target.Id)).Should().BeFalse();

        var survivors = await fixture.DbContext.JobOpportunities.AsNoTracking()
            .Where(j => j.Title.StartsWith("Curated")).ToListAsync();
        survivors.Should().HaveCount(2);
        survivors.Should().OnlyContain(j => j.SourceId == null, "preserved jobs are detached from the deleted source");
        survivors.Should().OnlyContain(j => j.SourceName == "WorkJobs2", "provenance survives via the snapshot");
        survivors.Should().OnlyContain(j => j.SourceUrl == "https://workjob.co.za");
    }

    // ─── 4. Explicit opt-in ───────────────────────────────────────────

    [Fact]
    public async Task Delete_removes_manually_edited_jobs_only_when_explicitly_requested()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var (target, _) = await SeedAsync(fixture);

        var result = await BuildService(fixture).DeleteAsync(target.Id, deleteManuallyEditedJobs: true);

        result.Data!.JobsDeleted.Should().Be(8);
        result.Data.ManualJobsPreserved.Should().Be(0);

        (await fixture.DbContext.JobOpportunities.AnyAsync(j => j.Title.StartsWith("Curated"))).Should().BeFalse();
    }

    // ─── 5. Blast radius ──────────────────────────────────────────────

    [Fact]
    public async Task Delete_never_touches_jobs_from_another_source()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var (target, other) = await SeedAsync(fixture);

        await BuildService(fixture).DeleteAsync(target.Id, deleteManuallyEditedJobs: true);

        var neighbours = await fixture.DbContext.JobOpportunities.AsNoTracking()
            .Where(j => j.SourceId == other.Id).ToListAsync();

        neighbours.Should().HaveCount(2, "the other source keeps every one of its jobs");
        neighbours.Should().Contain(j => j.Title == "Neighbour Draft");
        neighbours.Should().Contain(j => j.Title == "Neighbour Curated");
        (await fixture.DbContext.JobSources.AnyAsync(s => s.Id == other.Id)).Should().BeTrue();
    }

    // ─── 6. Audit ─────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_writes_an_audit_log_entry()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var (target, _) = await SeedAsync(fixture);

        await BuildService(fixture).DeleteAsync(target.Id, deleteManuallyEditedJobs: false);

        var entry = _audits.Should().ContainSingle(a => a.ActionType == AuditActionType.JobSourceDeleted).Subject;
        entry.EntityType.Should().Be(AuditEntityType.JobSource);
        entry.EntityId.Should().Be(target.Id);
        entry.EntityName.Should().Be("WorkJobs2");
        entry.Summary.Should().Contain("permanently deleted");
        entry.Summary.Should().Contain("6 job(s) deleted");
        entry.Summary.Should().Contain("2 manually-edited job(s) preserved");
    }

    // ─── 7. The real UAT scenario ─────────────────────────────────────

    [Fact]
    public async Task Deleting_the_duplicate_workjob_source_leaves_one_clean_set()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();

        // Both sources point at the same site and imported the same
        // five vacancies — the exact UAT duplicate.
        var keep = NewSource("WorkJob Full", "https://workjob.co.za/");
        var drop = NewSource("WorkJobs2", "https://workjob.co.za");
        fixture.DbContext.JobSources.AddRange(keep, drop);

        foreach (var title in new[] { "DSV Material Handler", "Unitrans General Worker", "24/7 Security Services", "UPLOAD CV at Woolworths", "Golden Era Careers" })
        {
            fixture.DbContext.JobOpportunities.Add(NewJob(keep, title, JobOpportunityStatus.Draft));
            fixture.DbContext.JobOpportunities.Add(NewJob(drop, title, JobOpportunityStatus.Draft));
        }
        await fixture.DbContext.SaveChangesAsync();

        var result = await BuildService(fixture).DeleteAsync(drop.Id, deleteManuallyEditedJobs: false);

        result.Data!.JobsDeleted.Should().Be(5);

        var remaining = await fixture.DbContext.JobOpportunities.AsNoTracking().ToListAsync();
        remaining.Should().HaveCount(5);
        remaining.Should().OnlyContain(j => j.SourceId == keep.Id);
        remaining.Select(j => j.Title).Should().OnlyHaveUniqueItems("the duplicate set is gone");

        (await fixture.DbContext.JobSources.CountAsync()).Should().Be(1);
        (await fixture.DbContext.JobOpportunities.CountAsync(j => j.Status == JobOpportunityStatus.Active)).Should().Be(0);
    }

    // ─── 8. Subscriber data is out of scope ───────────────────────────

    [Fact]
    public async Task Delete_never_touches_subscribers_documents_or_alert_preferences()
    {
        await using var harness = await IdentityTestHarness.CreateAsync();
        var fixture = harness.Fixture;
        var user = await harness.CreateUserAsync("subscriber@example.com", "Str0ngPassw0rd!", SystemRoles.JobSubscriber);

        var (target, _) = await SeedAsync(fixture);

        fixture.DbContext.JobSubscriberProfiles.Add(new JobSubscriberProfile { Id = Guid.NewGuid(), UserId = user.Id, CurrentCity = "Durban" });
        fixture.DbContext.JobSubscriberDocuments.Add(new JobSubscriberDocument
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            DocumentType = JobSubscriberDocumentType.Cv,
            ObjectKey = "job-documents/x/cv/a.pdf",
            FileName = "cv.pdf",
            ContentType = "application/pdf",
            SizeBytes = 1024
        });
        fixture.DbContext.JobAlertPreferences.Add(new JobAlertPreference { Id = Guid.NewGuid(), UserId = user.Id, Frequency = JobAlertFrequency.Daily });
        await fixture.DbContext.SaveChangesAsync();

        await BuildService(fixture).DeleteAsync(target.Id, deleteManuallyEditedJobs: true);

        (await fixture.DbContext.JobSubscriberProfiles.CountAsync()).Should().Be(1);
        (await fixture.DbContext.JobSubscriberDocuments.CountAsync()).Should().Be(1);
        (await fixture.DbContext.JobAlertPreferences.CountAsync()).Should().Be(1);
        (await fixture.DbContext.Users.CountAsync(u => u.Id == user.Id)).Should().Be(1);
    }

    // ─── Failure handling ─────────────────────────────────────────────

    [Fact]
    public async Task Deleting_a_source_that_does_not_exist_fails_safely()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        await SeedAsync(fixture);

        var result = await BuildService(fixture).DeleteAsync(Guid.NewGuid(), deleteManuallyEditedJobs: true);

        result.IsSuccess.Should().BeFalse();
        (await fixture.DbContext.JobSources.CountAsync()).Should().Be(2, "nothing else may be removed on a miss");
        (await fixture.DbContext.JobOpportunities.CountAsync()).Should().Be(10);
    }

    [Fact]
    public async Task Import_runs_survive_as_history_with_their_source_name()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var (target, _) = await SeedAsync(fixture);

        var result = await BuildService(fixture).DeleteAsync(target.Id, deleteManuallyEditedJobs: false);

        result.Data!.ImportRunsPreserved.Should().Be(2);
        result.Data.ImportRunsDeleted.Should().Be(0);

        var runs = await fixture.DbContext.JobImportRuns.AsNoTracking().Where(r => r.SourceName == "WorkJobs2").ToListAsync();
        runs.Should().HaveCount(2);
        runs.Should().OnlyContain(r => r.SourceId == null);
        (await fixture.DbContext.JobImportRuns.CountAsync()).Should().Be(3, "the other source's run is untouched");
    }
}
