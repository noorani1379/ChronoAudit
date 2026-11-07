using System.Security.Cryptography;
using System.Text;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.EntityFrameworkCore.Tests;

public sealed class ExportJobTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    private static ExportJob NewJob(ExportFormat format = ExportFormat.Csv)
        => ExportJob.Create("u1", "scripted", format, "{}", ScopeKind.Group, "g1", T0);

    [Fact]
    public void AJob_IsClaimableWhenItWaits_OrWhenItsLeaseRanOut_AndNeverWhileTheLeaseLives()
    {
        var job = NewJob();
        Assert.True(job.IsClaimable(T0));

        job.Claim("w1", T0, Lease);
        Assert.False(job.IsClaimable(T0.AddMinutes(1)));
        Assert.False(job.IsClaimable(T0 + Lease));
        Assert.True(job.IsClaimable(T0 + Lease + TimeSpan.FromTicks(1)));
        Assert.Throws<InvalidOperationException>(() => job.Claim("w2", T0.AddMinutes(1), Lease));

        job.Claim("w2", T0.AddMinutes(5), Lease);
        Assert.Equal(("w2", 2, ExportState.Running), (job.LeaseOwner, job.Attempts, job.State));
        Assert.Equal(T0, job.StartedAtUtc);
    }

    [Fact]
    public void TheFileName_IsTheIdAndTheFormatExtension_NeverAnythingFromTheRequest()
    {
        var csv = NewJob();
        var xlsx = NewJob(ExportFormat.Xlsx);

        csv.Claim("w", T0, Lease);
        xlsx.Claim("w", T0, Lease);

        Assert.Equal(csv.Id.ToString("N") + ".csv", csv.FileName);
        Assert.Equal(xlsx.Id.ToString("N") + ".xlsx", xlsx.FileName);
    }

    [Fact]
    public void ACheckpoint_MovesTheCursorBytesAndRowsTogether_AndRenewsTheLease()
    {
        var job = NewJob();
        job.Claim("w1", T0, Lease);

        job.Checkpoint("c1", 100, 5, T0.AddSeconds(30), Lease);
        job.Checkpoint("c2", 250, 7, T0.AddSeconds(60), Lease);

        Assert.Equal(("c2", 250L, 12L), (job.CheckpointCursor, job.CheckpointBytes, job.RowCount));
        Assert.Equal(T0.AddSeconds(60) + Lease, job.LeaseUntilUtc);
    }

    [Fact]
    public void ATerminalJobCannotBeCancelled_AndTheFifthFailedAttemptEndsTheJob()
    {
        var done = NewJob();
        done.Claim("w", T0, Lease);
        done.Complete(10, new string('A', 64), T0);
        var cancelled = done.Cancel(T0);

        Assert.Equal(("Export.AlreadyFinished", ChronoAuditErrorKind.Conflict), (cancelled.Error.Code, cancelled.Error.Kind));
        Assert.Equal(ExportState.Completed, done.State);

        var flaky = NewJob();
        for (var attempt = 1; attempt <= ExportJob.MaxAttempts; attempt++)
        {
            flaky.Claim("w", T0.AddHours(attempt), Lease);
            flaky.Release("the source dropped", T0.AddHours(attempt));
            Assert.Equal(attempt < ExportJob.MaxAttempts ? ExportState.Pending : ExportState.Failed, flaky.State);
        }

        Assert.Equal("Export.Failed", flaky.ErrorCode);
        Assert.NotNull(flaky.FinishedAtUtc);
        Assert.True(flaky.Cancel(T0).IsFailure);
    }

    [Fact]
    public void EveryMutatingMethod_RenewsTheConcurrencyStamp()
    {
        var job = NewJob();
        var stamps = new List<Guid> { job.ConcurrencyStamp };

        job.Claim("w", T0, Lease);
        stamps.Add(job.ConcurrencyStamp);
        job.Checkpoint("c", 1, 1, T0, Lease);
        stamps.Add(job.ConcurrencyStamp);
        job.Release("x", T0);
        stamps.Add(job.ConcurrencyStamp);
        job.Claim("w", T0, Lease);
        job.Fail("Export.Failed", "x", T0);
        stamps.Add(job.ConcurrencyStamp);

        Assert.Equal(stamps.Count, stamps.Distinct().Count());
        var cancelled = NewJob();
        var before = cancelled.ConcurrencyStamp;
        Assert.True(cancelled.Cancel(T0).IsSuccess);
        Assert.NotEqual(before, cancelled.ConcurrencyStamp);
    }

    [Fact]
    public void AnErrorMessage_IsCutToItsColumnLength()
    {
        var job = NewJob();
        job.Claim("w", T0, Lease);

        job.Fail("Export.Failed", new string('x', 900), T0);

        Assert.Equal(ExportJob.MaxErrorLength, job.ErrorMessage!.Length);
    }
}

public sealed class ExportWorkerTests
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private static string Expected(int batches, int rowsPerBatch)
    {
        var text = new StringBuilder("n\r\n");
        for (var b = 0; b < batches; b++)
        {
            for (var r = 0; r < rowsPerBatch; r++)
            {
                text.Append($"row-{b}-{r}\r\n");
            }
        }

        return text.ToString();
    }

    [Fact]
    public async Task AJob_IsWrittenInBatches_WithTheBom_TheRowCount_AndTheSha256OfTheWholeFile_AndTheStartAndEndAreAudited()
    {
        await using var rig = await ExportRig.CreateAsync();
        var requested = await rig.RequestAsync();

        var ran = await rig.DrainAsync();

        var job = await rig.JobAsync(requested.Id);
        var bytes = await File.ReadAllBytesAsync(rig.PathOf(job));
        Assert.Equal(1, ran);
        Assert.Equal(ExportState.Completed, job.State);
        Assert.Equal(Bom, bytes[..3]);
        Assert.Equal(Expected(3, 2), Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
        Assert.Equal(6, job.RowCount);
        Assert.Equal(bytes.Length, job.FileSizeBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), job.Sha256);
        Assert.Equal(new[] { AuditActions.ExportStarted, AuditActions.ExportFinished }, await rig.AuditActionsAsync());
        Assert.Equal(new string?[] { null, "1", "2" }, rig.Script.Cursors);
    }

    [Fact]
    public async Task AFailureMidWay_ReleasesTheJobWithItsCheckpoint_AndTheNextRunContinuesWithoutDuplicates()
    {
        await using var rig = await ExportRig.CreateAsync();
        rig.Script.Batches = 4;
        rig.Script.FailOnCall = 3;
        var requested = await rig.RequestAsync();

        Assert.True(await rig.Worker.RunOnceAsync(CancellationToken.None));
        var released = await rig.JobAsync(requested.Id);
        Assert.Equal((ExportState.Pending, 4L, 1, "2"), (released.State, released.RowCount, released.Attempts, released.CheckpointCursor));
        Assert.Contains("the source dropped", released.ErrorMessage, StringComparison.Ordinal);

        await rig.DrainAsync();

        var job = await rig.JobAsync(requested.Id);
        var text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(rig.PathOf(job)), 3, (int)job.FileSizeBytes! - 3);
        Assert.Equal(ExportState.Completed, job.State);
        Assert.Equal(Expected(4, 2), text);
        Assert.Equal(8, job.RowCount);
        Assert.Single(await rig.AuditActionsAsync(), AuditActions.ExportStarted);
    }

    [Fact]
    public async Task AHalfWrittenBatchAfterACrash_IsCutAwayAtTheCheckpoint_NotKept()
    {
        await using var rig = await ExportRig.CreateAsync();
        rig.Script.Batches = 3;
        rig.Script.FailOnCall = 2;
        var requested = await rig.RequestAsync();
        await rig.Worker.RunOnceAsync(CancellationToken.None);
        var path = rig.PathOf(await rig.JobAsync(requested.Id));
        await File.AppendAllTextAsync(path, "row-1-GARBAGE-half-written-batch");

        await rig.DrainAsync();

        var job = await rig.JobAsync(requested.Id);
        var text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path), 3, (int)job.FileSizeBytes! - 3);
        Assert.DoesNotContain("GARBAGE", text, StringComparison.Ordinal);
        Assert.Equal(Expected(3, 2), text);
    }

    [Fact]
    public async Task AJobWhoseLastBatchIsWritten_IsFinishedOnResume_NeverReadingThatBatchTwice()
    {
        await using var rig = await ExportRig.CreateAsync();
        var requested = await rig.RequestAsync();
        await rig.Worker.RunOnceAsync(CancellationToken.None);


        // A crash after the last checkpoint and before the completion: the job is Running again with a dead lease and its done-cursor.
        await rig.ForceRunningWithDeadLeaseAsync(requested.Id);
        rig.Script.Cursors.Clear();

        await rig.DrainAsync();

        var job = await rig.JobAsync(requested.Id);
        Assert.Equal(ExportState.Completed, job.State);
        Assert.Empty(rig.Script.Cursors);
        Assert.Equal(6, job.RowCount);
        Assert.Equal(Expected(3, 2), Encoding.UTF8.GetString(await File.ReadAllBytesAsync(rig.PathOf(job)), 3, (int)job.FileSizeBytes! - 3));
    }

    [Fact]
    public async Task ARunningJobWithALiveLease_IsLeftAlone_AndOneWhoseLeaseRanOutIsTakenOver()
    {
        await using var rig = await ExportRig.CreateAsync();
        var requested = await rig.RequestAsync();
        await rig.ChangeJobAsync(requested.Id, j => j.Claim("other-worker", rig.Host.Clock.GetUtcNow(), TimeSpan.FromMinutes(2)));

        var touched = await rig.Worker.RunOnceAsync(CancellationToken.None);
        var stillOther = await rig.JobAsync(requested.Id);

        Assert.False(touched);
        Assert.Equal(("other-worker", ExportState.Running), (stillOther.LeaseOwner, stillOther.State));

        rig.Host.Clock.Advance(TimeSpan.FromMinutes(3));
        Assert.True(await rig.Worker.RunOnceAsync(CancellationToken.None));
        var taken = await rig.JobAsync(requested.Id);
        Assert.Equal(ExportState.Completed, taken.State);
        Assert.Equal(2, taken.Attempts);
    }

    [Fact]
    public async Task AJobOverTheRowCap_FailsWithExportTooLarge_AndLeavesNoFile()
    {
        await using var rig = await ExportRig.CreateAsync(o => o.MaxRows = 5);
        var requested = await rig.RequestAsync();

        await rig.DrainAsync();

        var job = await rig.JobAsync(requested.Id);
        Assert.Equal((ExportState.Failed, "Export.TooLarge"), (job.State, job.ErrorCode));
        Assert.False(File.Exists(rig.PathOf(job)));
        Assert.Equal(new[] { AuditActions.ExportStarted, AuditActions.ExportFinished }, await rig.AuditActionsAsync());
    }

    [Fact]
    public async Task ACancelledJob_IsNeverRun()
    {
        await using var rig = await ExportRig.CreateAsync();
        var requested = await rig.RequestAsync();
        Assert.True((await rig.Service.CancelAsync(requested.Id, ExportRig.Me, CancellationToken.None)).IsSuccess);

        var ran = await rig.Worker.RunOnceAsync(CancellationToken.None);

        Assert.False(ran);
        Assert.Equal(0, rig.Script.Calls);
        Assert.Equal(ExportState.Cancelled, (await rig.JobAsync(requested.Id)).State);
    }

    [Fact]
    public async Task ACancelWhileTheJobRuns_StopsTheWorker_AndRemovesTheHalfWrittenFile()
    {
        await using var rig = await ExportRig.CreateAsync();
        rig.Script.Batches = 5;
        var requested = await rig.RequestAsync();
        rig.Script.OnBatch = async index =>
        {
            if (index == 2)
            {
                Assert.True((await rig.Service.CancelAsync(requested.Id, ExportRig.Me, CancellationToken.None)).IsSuccess);
            }
        };

        await rig.Worker.RunOnceAsync(CancellationToken.None);

        var job = await rig.JobAsync(requested.Id);
        Assert.Equal(ExportState.Cancelled, job.State);
        Assert.False(File.Exists(rig.PathOf(job)));
        Assert.Equal(3, rig.Script.Calls);
        Assert.DoesNotContain(AuditActions.ExportFinished, await rig.AuditActionsAsync());
    }

    [Fact]
    public async Task AProducerThatAlwaysFails_IsRetriedFiveTimes_ThenTheJobFails_NeverAFile()
    {
        await using var rig = await ExportRig.CreateAsync();
        rig.Script.AlwaysFail = true;
        var requested = await rig.RequestAsync();

        var ran = await rig.DrainAsync();

        var job = await rig.JobAsync(requested.Id);
        Assert.Equal(ExportJob.MaxAttempts, ran);
        Assert.Equal((ExportState.Failed, "Export.Failed", ExportJob.MaxAttempts), (job.State, job.ErrorCode, job.Attempts));
        Assert.Equal(new[] { AuditActions.ExportStarted, AuditActions.ExportFinished }, await rig.AuditActionsAsync());
        Assert.Null(job.Sha256);
    }

    [Fact]
    public async Task AHalfWrittenFileThatIsShorterThanItsCheckpoint_FailsWithFileCorrupt()
    {
        await using var rig = await ExportRig.CreateAsync();
        rig.Script.FailOnCall = 2;
        var requested = await rig.RequestAsync();
        await rig.Worker.RunOnceAsync(CancellationToken.None);
        File.Delete(rig.PathOf(await rig.JobAsync(requested.Id)));

        await rig.DrainAsync();

        var job = await rig.JobAsync(requested.Id);
        Assert.Equal((ExportState.Failed, "Export.FileCorrupt"), (job.State, job.ErrorCode));
    }

    [Fact]
    public async Task AProducerThatWasRemoved_FailsTheJob_AtRunTime()
    {
        await using var rig = await ExportRig.CreateAsync();
        var requested = await rig.RequestAsync();
        await rig.SetProducerAsync(requested.Id, "gone");

        await rig.DrainAsync();

        Assert.Equal(("Export.Failed", ExportState.Failed), ((await rig.JobAsync(requested.Id)).ErrorCode, (await rig.JobAsync(requested.Id)).State));
    }

    [Fact]
    public async Task TheJobs_AreRunOldestFirst()
    {
        await using var rig = await ExportRig.CreateAsync(o => o.MaxActivePerUser = 5);
        var first = await rig.RequestAsync();
        rig.Host.Clock.Advance(TimeSpan.FromSeconds(1));
        var second = await rig.RequestAsync(scope: ChronoAuditScope.OwnOf("u2"));

        await rig.Worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(ExportState.Completed, (await rig.JobAsync(first.Id)).State);
        Assert.Equal(ExportState.Pending, (await rig.JobAsync(second.Id)).State);
    }
}
