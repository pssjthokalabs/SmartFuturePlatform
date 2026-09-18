using Moq;
using SmartFuture.Application.Jobs.Dtos;
using SmartFuture.Application.Jobs.Import;
using SmartFuture.Domain.Jobs;
using SmartFuture.Shared.Enums.Jobs;
using SmartFuture.Shared.Results;
using Xunit;

namespace SmartFuture.Tests.Jobs;

// Brief §10/§19: "When Auto Import is enabled, active configured job
// sources automatically crawl when due WITHOUT an admin clicking
// Refresh" — and the converse, AutoImportEnabled=false must NEVER
// trigger a scheduled crawl. JobImportScheduledDueTests already pins
// the "which sources are due" half (RunAllAsync's onlyDue filter);
// these tests pin the other half JobImportHostedService.RunTickAsync
// depends on — the AutoImportEnabled/JobsModuleEnabled gate itself —
// deterministically, with no real-time waiting. `RunScheduledTick`
// below mirrors RunTickAsync's exact control flow (private, inside a
// BackgroundService, so not directly callable from a test) using the
// same JobImportSchedulingGate.ShouldRunScheduledImport predicate the
// real hosted service calls.
public class JobImportAutoImportGateTests
{
    private static JobModuleSettings Settings(bool jobsModuleEnabled, bool autoImportEnabled) => new()
    {
        JobsModuleEnabled = jobsModuleEnabled,
        AutoImportEnabled = autoImportEnabled,
    };

    private static async Task RunScheduledTick(JobModuleSettings settings, IJobImportService importService)
    {
        if (!JobImportSchedulingGate.ShouldRunScheduledImport(settings)) return;
        await importService.RunAllAsync(JobImportRunTrigger.Scheduled, onlyDue: true);
    }

    [Fact]
    public async Task AutoImportEnabled_True_WithModuleEnabled_RunsTheScheduledImport()
    {
        var importService = new Mock<IJobImportService>();
        importService
            .Setup(s => s.RunAllAsync(JobImportRunTrigger.Scheduled, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<JobImportSummaryDto>.Success(new JobImportSummaryDto { SourcesAttempted = 1, SourcesSucceeded = 1 }));

        await RunScheduledTick(Settings(jobsModuleEnabled: true, autoImportEnabled: true), importService.Object);

        importService.Verify(
            s => s.RunAllAsync(JobImportRunTrigger.Scheduled, true, It.IsAny<CancellationToken>()),
            Times.Once,
            "an active, due source must be crawled automatically once AutoImportEnabled is true — no admin Refresh click required.");
    }

    [Fact]
    public async Task AutoImportEnabled_False_NeverRunsTheScheduledImport()
    {
        var importService = new Mock<IJobImportService>();

        await RunScheduledTick(Settings(jobsModuleEnabled: true, autoImportEnabled: false), importService.Object);

        importService.Verify(
            s => s.RunAllAsync(It.IsAny<JobImportRunTrigger>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "AutoImportEnabled=false must block the scheduled tick entirely — manual Refresh remains the only way to crawl.");
    }

    [Fact]
    public async Task JobsModuleEnabled_False_NeverRunsTheScheduledImport_EvenIfAutoImportIsTrue()
    {
        // The module kill-switch wins over AutoImportEnabled — disabling
        // the whole Jobs module must not leave a background crawler
        // quietly still running.
        var importService = new Mock<IJobImportService>();

        await RunScheduledTick(Settings(jobsModuleEnabled: false, autoImportEnabled: true), importService.Object);

        importService.Verify(
            s => s.RunAllAsync(It.IsAny<JobImportRunTrigger>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void ShouldRunScheduledImport_IsExactlyTheAndOfBothFlags(bool jobsModuleEnabled, bool autoImportEnabled, bool expected)
    {
        Assert.Equal(expected, JobImportSchedulingGate.ShouldRunScheduledImport(Settings(jobsModuleEnabled, autoImportEnabled)));
    }
}
