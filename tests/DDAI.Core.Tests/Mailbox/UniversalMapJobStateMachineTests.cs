using DDAI.Core.Mailbox;
using DDAI.Core.MapPlans;
using System.Text.Json.Nodes;

namespace DDAI.Core.Tests.Mailbox;

public sealed class UniversalMapJobStateMachineTests
{
    [Theory]
    [InlineData("terrain-stroke")]
    [InlineData("pattern-region")]
    [InlineData("colorable-pattern-region")]
    [InlineData("cave-region")]
    [InlineData("roof-region")]
    public void SurfaceOperationObservationFailure_ReversesOnlyItsBoundEvidence(string operationId)
    {
        using var sandbox = new JobSandbox("surface-" + operationId);
        var runtime = new RecordingRuntime { ObservationResult = false };
        using var machine = sandbox.Create(runtime);
        var submission = sandbox.Submission with { OperationIds = [operationId] };

        Assert.Equal(MapJobSubmissionResult.Accepted, machine.Submit(submission));
        RunToNoWork(machine, submission.RequestId);

        Assert.Equal([0], runtime.Applied);
        Assert.Equal([0], runtime.Observed);
        Assert.Equal([0], runtime.Reversed);
        Assert.Contains("reversed", File.ReadAllText(sandbox.ResponsePath), StringComparison.Ordinal);
    }

    [Fact]
    public void Advance_PerformsExactlyOneBoundaryAndConvergesToCleanup()
    {
        using var sandbox = new JobSandbox();
        var runtime = new RecordingRuntime();
        var machine = sandbox.Create(runtime);

        Assert.Equal(MapJobSubmissionResult.Accepted, machine.Submit(sandbox.Submission));
        Assert.Equal(MapJobTransition.Prepared, machine.Advance(sandbox.Submission.RequestId));
        Assert.Equal(MapJobState.Prepared, machine.ReadJournal(sandbox.Submission.RequestId)!.State);

        Assert.Equal(MapJobTransition.NativeOperationCalled, machine.Advance(sandbox.Submission.RequestId));
        Assert.Single(runtime.Applied);
        Assert.Equal(MapJobState.Prepared, machine.ReadJournal(sandbox.Submission.RequestId)!.State);

        Assert.Equal(MapJobTransition.OperationApplied, machine.Advance(sandbox.Submission.RequestId));
        Assert.Equal(MapJobState.OperationApplied, machine.ReadJournal(sandbox.Submission.RequestId)!.State);
        Assert.Equal([101L], machine.ReadJournal(sandbox.Submission.RequestId)!.CurrentOperationNodeIds);
        Assert.Empty(machine.ReadJournal(sandbox.Submission.RequestId)!.ObservedNativeNodeIds);

        Assert.Equal(MapJobTransition.NativeOperationObserved, machine.Advance(sandbox.Submission.RequestId));
        Assert.Single(runtime.Observed);
        Assert.Equal(MapJobState.OperationApplied, machine.ReadJournal(sandbox.Submission.RequestId)!.State);

        Assert.Equal(MapJobTransition.OperationObserved, machine.Advance(sandbox.Submission.RequestId));
        Assert.Equal(MapJobState.OperationObserved, machine.ReadJournal(sandbox.Submission.RequestId)!.State);
        Assert.Equal(1, machine.ReadJournal(sandbox.Submission.RequestId)!.NextOperationIndex);

        Assert.Equal(MapJobTransition.Prepared, machine.Advance(sandbox.Submission.RequestId));
        Assert.Equal(MapJobTransition.NativeOperationCalled, machine.Advance(sandbox.Submission.RequestId));
        Assert.Equal(MapJobTransition.OperationApplied, machine.Advance(sandbox.Submission.RequestId));
        Assert.Equal(MapJobTransition.NativeOperationObserved, machine.Advance(sandbox.Submission.RequestId));
        Assert.Equal(MapJobTransition.OperationObserved, machine.Advance(sandbox.Submission.RequestId));
        Assert.Equal(MapJobTransition.Committed, machine.Advance(sandbox.Submission.RequestId));
        Assert.Equal(MapJobTransition.ResponsePublished, machine.Advance(sandbox.Submission.RequestId));
        Assert.True(File.Exists(sandbox.ResponsePath));
        Assert.Equal(MapJobTransition.CompletionRecorded, machine.Advance(sandbox.Submission.RequestId));
        Assert.Equal(MapJobTransition.ClaimDeleted, machine.Advance(sandbox.Submission.RequestId));
        Assert.Equal(MapJobTransition.JournalDeleted, machine.Advance(sandbox.Submission.RequestId));
        Assert.Null(machine.ReadJournal(sandbox.Submission.RequestId));
        Assert.False(File.Exists(sandbox.ClaimPath));
        Assert.Equal(MapJobTransition.NoWork, machine.Advance(sandbox.Submission.RequestId));
    }

    [Fact]
    public void Restart_FromPreparedBecomesOutcomeUnknownWithoutReplaying()
    {
        using var sandbox = new JobSandbox();
        var firstRuntime = new RecordingRuntime();
        var first = sandbox.Create(firstRuntime);
        first.Submit(sandbox.Submission);
        Assert.Equal(MapJobTransition.Prepared, first.Advance(sandbox.Submission.RequestId));
        Assert.Equal(MapJobTransition.NativeOperationCalled, first.Advance(sandbox.Submission.RequestId));

        var restartedRuntime = new RecordingRuntime();
        first.Dispose();
        var restarted = sandbox.Create(restartedRuntime);
        Assert.Equal(MapJobTransition.OutcomeUnknown, restarted.Advance(sandbox.Submission.RequestId));

        Assert.Empty(restartedRuntime.Applied);
        Assert.Equal(MapJobState.OutcomeUnknown, restarted.ReadJournal(sandbox.Submission.RequestId)!.State);
        Assert.Equal(MapJobTransition.Committed, restarted.Advance(sandbox.Submission.RequestId));
        Assert.Contains("outcome_unknown", restarted.ReadJournal(sandbox.Submission.RequestId)!.CanonicalResponse, StringComparison.Ordinal);
    }

    [Fact]
    public void Restart_AtEveryDurableBoundaryNeverRepeatsObservedOperations()
    {
        var boundaryCount = CountSuccessfulBoundaries();
        for (var completedAdvances = 0; completedAdvances <= boundaryCount; completedAdvances++)
        {
            using var sandbox = new JobSandbox("boundary-" + completedAdvances);
            var runtime = new RecordingRuntime();
            var machine = sandbox.Create(runtime);
            machine.Submit(sandbox.Submission);
            for (var index = 0; index < completedAdvances; index++)
            {
                machine.Advance(sandbox.Submission.RequestId);
            }

            var callsBeforeRestart = runtime.Applied.Count;
            var restartedRuntime = new RecordingRuntime();
            machine.Dispose();
            var restarted = sandbox.Create(restartedRuntime);
            RunToNoWork(restarted, sandbox.Submission.RequestId);

            Assert.True(callsBeforeRestart + restartedRuntime.Applied.Count <= 2);
            Assert.All(
                runtime.Applied.Concat(restartedRuntime.Applied).GroupBy(index => index),
                group => Assert.Single(group));
        }
    }

    [Fact]
    public void Restart_AtEveryReversalBoundaryNeverRepeatsApplyOrReverse()
    {
        var boundaryCount = CountReversalBoundaries();
        for (var completedAdvances = 0; completedAdvances <= boundaryCount; completedAdvances++)
        {
            using var sandbox = new JobSandbox("reversal-boundary-" + completedAdvances);
            var runtime = new RecordingRuntime { FailObservationAt = 1 };
            var machine = sandbox.Create(runtime);
            machine.Submit(sandbox.Submission);
            for (var index = 0; index < completedAdvances; index++)
            {
                machine.Advance(sandbox.Submission.RequestId);
            }

            var restartedRuntime = new RecordingRuntime { FailObservationAt = 1 };
            machine.Dispose();
            RunToNoWork(sandbox.Create(restartedRuntime), sandbox.Submission.RequestId);
            Assert.All(
                runtime.Applied.Concat(restartedRuntime.Applied).GroupBy(index => index),
                group => Assert.Single(group));
            Assert.All(
                runtime.Reversed.Concat(restartedRuntime.Reversed).GroupBy(index => index),
                group => Assert.Single(group));
        }
    }

    [Fact]
    public void DuplicateRequest_ReusesIdentityAndRejectsConflictingPlan()
    {
        using var sandbox = new JobSandbox();
        var machine = sandbox.Create(new RecordingRuntime());

        Assert.Equal(MapJobSubmissionResult.Accepted, machine.Submit(sandbox.Submission));
        Assert.Equal(MapJobSubmissionResult.AlreadyAccepted, machine.Submit(sandbox.Submission));
        Assert.Equal(
            MapJobSubmissionResult.Conflict,
            machine.Submit(sandbox.Submission with { PlanFingerprint = new string('b', 64) }));
    }

    [Fact]
    public void CompletedDuplicate_RequiresUntamperedPublishedResponse()
    {
        using var sandbox = new JobSandbox();
        var machine = sandbox.Create(new RecordingRuntime());
        machine.Submit(sandbox.Submission);
        AdvanceUntil(machine, sandbox.Submission.RequestId, MapJobTransition.JournalDeleted);

        Assert.Equal(MapJobSubmissionResult.AlreadyAccepted, machine.Submit(sandbox.Submission));
        File.WriteAllText(sandbox.ResponsePath, "{\"tampered\":true}");
        Assert.Equal(MapJobSubmissionResult.Blocked, machine.Submit(sandbox.Submission));
    }

    [Fact]
    public void MissingActiveClaim_BlocksInsteadOfExecutingFromJournalAlone()
    {
        using var sandbox = new JobSandbox();
        var runtime = new RecordingRuntime();
        var machine = sandbox.Create(runtime);
        machine.Submit(sandbox.Submission);
        machine.Advance(sandbox.Submission.RequestId);
        File.Delete(sandbox.ClaimPath);

        Assert.Equal(MapJobTransition.Blocked, machine.Advance(sandbox.Submission.RequestId));
        Assert.Empty(runtime.Applied);
    }

    [Fact]
    public async Task ConcurrentDuplicate_HasExactlyOneAcceptedWriter()
    {
        using var sandbox = new JobSandbox();
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            sandbox.Create(new RecordingRuntime()).Submit(sandbox.Submission))));

        Assert.Single(results, result => result == MapJobSubmissionResult.Accepted);
        Assert.Equal(15, results.Count(result => result == MapJobSubmissionResult.AlreadyAccepted));
    }

    [Fact]
    public async Task ConcurrentAdvance_CallsNativeOperationExactlyOnce()
    {
        using var sandbox = new JobSandbox();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var runtime = new RecordingRuntime { ApplyEntered = entered, ApplyRelease = release };
        var machine = sandbox.Create(runtime);
        var submission = sandbox.Submission with { OperationIds = ["op-a"] };
        machine.Submit(submission);
        Assert.Equal(MapJobTransition.Prepared, machine.Advance(submission.RequestId));

        var first = Task.Run(() => machine.Advance(submission.RequestId));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var others = Enumerable.Range(0, 11).Select(_ => Task.Run(() => machine.Advance(submission.RequestId))).ToArray();
        await Task.Delay(100);
        release.Set();
        var transitions = await Task.WhenAll(others.Prepend(first));

        Assert.Single(runtime.Applied);
        Assert.Single(transitions, transition => transition == MapJobTransition.NativeOperationCalled);
        Assert.DoesNotContain(MapJobTransition.OutcomeUnknown, transitions);
    }

    [Fact]
    public async Task SecondMachineForSameRoot_IsBlockedWhileOwnerIsActive()
    {
        using var sandbox = new JobSandbox();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var runtime = new RecordingRuntime { ApplyEntered = entered, ApplyRelease = release };
        var owner = sandbox.Create(runtime);
        var competitor = sandbox.Create(runtime);
        owner.Submit(sandbox.Submission);
        owner.Advance(sandbox.Submission.RequestId);

        var applying = Task.Run(() => owner.Advance(sandbox.Submission.RequestId));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var competing = Task.Run(() => competitor.Advance(sandbox.Submission.RequestId));
        release.Set();

        Assert.Equal(MapJobTransition.NativeOperationCalled, await applying);
        Assert.Equal(MapJobTransition.Blocked, await competing);
        Assert.Single(runtime.Applied);
        Assert.Equal(MapJobState.Prepared, owner.ReadJournal(sandbox.Submission.RequestId)!.State);
    }

    [Fact]
    public void TamperedRecoveredClaim_IsRejectedBeforeJournalOrNativeCall()
    {
        using var sandbox = new JobSandbox();
        var runtime = new RecordingRuntime();
        var first = sandbox.Create(runtime);
        first.Submit(sandbox.Submission);
        first.Dispose();
        var claim = JsonNode.Parse(File.ReadAllText(sandbox.ClaimPath))!.AsObject();
        claim["map_id"] = "../unsafe";
        File.WriteAllText(sandbox.ClaimPath, claim.ToJsonString());

        var recovered = sandbox.Create(runtime);
        Assert.Equal(MapJobTransition.Blocked, recovered.Advance(sandbox.Submission.RequestId));
        Assert.Empty(runtime.Applied);
        Assert.False(File.Exists(sandbox.JournalPath));
    }

    [Fact]
    public void TamperedJournalResponse_IsRejectedBeforeNativeCall()
    {
        using var sandbox = new JobSandbox();
        var runtime = new RecordingRuntime();
        var machine = sandbox.Create(runtime);
        machine.Submit(sandbox.Submission);
        machine.Advance(sandbox.Submission.RequestId);
        var journal = JsonNode.Parse(File.ReadAllText(sandbox.JournalPath))!.AsObject();
        journal["canonical_success_response"] = "not json";
        File.WriteAllText(sandbox.JournalPath, journal.ToJsonString());

        Assert.Equal(MapJobTransition.Blocked, machine.Advance(sandbox.Submission.RequestId));
        Assert.Empty(runtime.Applied);
    }

    [Fact]
    public void ValidJsonCommittedResponseTamper_IsNeverPublished()
    {
        using var sandbox = new JobSandbox();
        var runtime = new RecordingRuntime();
        var machine = sandbox.Create(runtime);
        machine.Submit(sandbox.Submission);
        AdvanceUntil(machine, sandbox.Submission.RequestId, MapJobTransition.Committed);
        var journal = JsonNode.Parse(File.ReadAllText(sandbox.JournalPath))!.AsObject();
        journal["canonical_response"] = "{\"foreign\":true}";
        File.WriteAllText(sandbox.JournalPath, journal.ToJsonString());

        Assert.Equal(MapJobTransition.Blocked, machine.Advance(sandbox.Submission.RequestId));
        Assert.False(File.Exists(sandbox.ResponsePath));
    }

    [Fact]
    public void RecoveredJournalWithNegativeNodeId_IsRejectedBeforeObservation()
    {
        using var sandbox = new JobSandbox();
        var runtime = new RecordingRuntime();
        var machine = sandbox.Create(runtime);
        machine.Submit(sandbox.Submission);
        machine.Advance(sandbox.Submission.RequestId);
        machine.Advance(sandbox.Submission.RequestId);
        machine.Advance(sandbox.Submission.RequestId);
        var journal = JsonNode.Parse(File.ReadAllText(sandbox.JournalPath))!.AsObject();
        journal["current_operation_node_ids"] = new JsonArray(-1);
        File.WriteAllText(sandbox.JournalPath, journal.ToJsonString());

        Assert.Equal(MapJobTransition.Blocked, machine.Advance(sandbox.Submission.RequestId));
        Assert.Empty(runtime.Observed);
    }

    [Fact]
    public void RecoveredJournalWithNullObservation_IsRejectedWithoutThrowing()
    {
        using var sandbox = new JobSandbox();
        var runtime = new RecordingRuntime();
        var machine = sandbox.Create(runtime);
        machine.Submit(sandbox.Submission);
        AdvanceUntil(machine, sandbox.Submission.RequestId, MapJobTransition.OperationObserved);
        var journal = JsonNode.Parse(File.ReadAllText(sandbox.JournalPath))!.AsObject();
        journal["operation_observations"] = new JsonArray((JsonNode?)null);
        File.WriteAllText(sandbox.JournalPath, journal.ToJsonString());

        Assert.Equal(MapJobTransition.Blocked, machine.Advance(sandbox.Submission.RequestId));
    }

    [Fact]
    public void RecoveredJournalCannotReusePreviouslyObservedNodeAsCurrent()
    {
        using var sandbox = new JobSandbox();
        var runtime = new RecordingRuntime();
        var machine = sandbox.Create(runtime);
        machine.Submit(sandbox.Submission);
        AdvanceUntil(machine, sandbox.Submission.RequestId, MapJobTransition.OperationObserved);
        Assert.Equal(MapJobTransition.Prepared, machine.Advance(sandbox.Submission.RequestId));
        Assert.Equal(MapJobTransition.NativeOperationCalled, machine.Advance(sandbox.Submission.RequestId));
        Assert.Equal(MapJobTransition.OperationApplied, machine.Advance(sandbox.Submission.RequestId));
        var journal = JsonNode.Parse(File.ReadAllText(sandbox.JournalPath))!.AsObject();
        journal["current_operation_node_ids"] = new JsonArray(101);
        File.WriteAllText(sandbox.JournalPath, journal.ToJsonString());

        Assert.Equal(MapJobTransition.Blocked, machine.Advance(sandbox.Submission.RequestId));
        Assert.Single(runtime.Observed);
    }

    [Fact]
    public void ConflictingResponse_BlocksWithoutOverwriteOrCleanup()
    {
        using var sandbox = new JobSandbox();
        var machine = sandbox.Create(new RecordingRuntime());
        machine.Submit(sandbox.Submission);
        AdvanceUntil(machine, sandbox.Submission.RequestId, MapJobTransition.Committed);
        File.WriteAllText(sandbox.ResponsePath, "{\"foreign\":true}");

        Assert.Equal(MapJobTransition.Blocked, machine.Advance(sandbox.Submission.RequestId));
        Assert.Equal("{\"foreign\":true}", File.ReadAllText(sandbox.ResponsePath));
        Assert.NotNull(machine.ReadJournal(sandbox.Submission.RequestId));
        Assert.True(File.Exists(sandbox.ClaimPath));
    }

    [Fact]
    public void DuplicateJournalProperty_IsRejectedWithoutNativeCall()
    {
        using var sandbox = new JobSandbox();
        var runtime = new RecordingRuntime();
        var machine = sandbox.Create(runtime);
        machine.Submit(sandbox.Submission);
        machine.Advance(sandbox.Submission.RequestId);
        var text = File.ReadAllText(sandbox.JournalPath);
        File.WriteAllText(
            sandbox.JournalPath,
            text.Replace("\"state\":\"prepared\"", "\"state\":\"prepared\",\"state\":\"prepared\"", StringComparison.Ordinal));

        Assert.Equal(MapJobTransition.Blocked, machine.Advance(sandbox.Submission.RequestId));
        Assert.Empty(runtime.Applied);
        Assert.True(File.Exists(sandbox.ClaimPath));
    }

    [Fact]
    public void ConflictingJournalIdentity_BlocksWithoutNativeCall()
    {
        using var sandbox = new JobSandbox();
        var runtime = new RecordingRuntime();
        var machine = sandbox.Create(runtime);
        machine.Submit(sandbox.Submission);
        machine.Advance(sandbox.Submission.RequestId);
        var text = File.ReadAllText(sandbox.JournalPath);
        File.WriteAllText(sandbox.JournalPath, text.Replace(new string('a', 64), new string('b', 64), StringComparison.Ordinal));

        Assert.Equal(MapJobTransition.Blocked, machine.Advance(sandbox.Submission.RequestId));
        Assert.Empty(runtime.Applied);
    }

    [Fact]
    public void OversizeCanonicalResponse_IsRejectedBeforeClaimCreation()
    {
        using var sandbox = new JobSandbox();
        var machine = sandbox.Create(new RecordingRuntime());
        var submission = sandbox.Submission with
        {
            CanonicalSuccessResponse = "{\"value\":\"" + new string('x', 1024 * 1024) + "\"}",
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => machine.Submit(submission));
        Assert.False(File.Exists(sandbox.ClaimPath));
    }

    [Fact]
    public void CombinedOversizeClaim_IsRejectedBeforeCreation()
    {
        using var sandbox = new JobSandbox();
        var machine = sandbox.Create(new RecordingRuntime());
        var large = "{\"value\":\"" + new string('x', 600 * 1024) + "\"}";
        var submission = sandbox.Submission with
        {
            CanonicalSuccessResponse = large,
            CanonicalReversedResponse = large,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => machine.Submit(submission));
        Assert.False(File.Exists(sandbox.ClaimPath));
    }

    [Fact]
    public void NearLimitResponsesThatCannotFitWorstCaseReceipt_AreRejectedBeforeMutation()
    {
        using var sandbox = new JobSandbox();
        var runtime = new RecordingRuntime { NodeCount = 10_000 };
        var machine = sandbox.Create(runtime);
        var large = "{\"value\":\"" + new string('x', 350 * 1024) + "\"}";
        var submission = sandbox.Submission with
        {
            CanonicalSuccessResponse = large,
            CanonicalReversedResponse = large,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => machine.Submit(submission));
        Assert.Empty(runtime.Applied);
        Assert.False(File.Exists(sandbox.ClaimPath));
    }

    [Fact]
    public void ShorterUnicodeReversal_IsSizedBySerializedBytesBeforeMutation()
    {
        using var sandbox = new JobSandbox();
        var runtime = new RecordingRuntime();
        var machine = sandbox.Create(runtime);
        var submission = sandbox.Submission with
        {
            CanonicalSuccessResponse = "{\"value\":\"" + new string('x', 80_000) + "\"}",
            CanonicalReversedResponse = "{\"value\":\"" + new string('\u00e9', 70_000) + "\"}",
        };

        Assert.True(submission.CanonicalReversedResponse.Length < submission.CanonicalSuccessResponse.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => machine.Submit(submission));
        Assert.Empty(runtime.Applied);
        Assert.False(File.Exists(sandbox.ClaimPath));
    }

    [Fact]
    public void OversizeNativeReceipt_BlocksThenBecomesOutcomeUnknownWithoutReplay()
    {
        using var sandbox = new JobSandbox();
        var runtime = new RecordingRuntime { NodeCount = 150_000 };
        var machine = sandbox.Create(runtime);
        machine.Submit(sandbox.Submission);
        Assert.Equal(MapJobTransition.Prepared, machine.Advance(sandbox.Submission.RequestId));
        Assert.Equal(MapJobTransition.NativeOperationCalled, machine.Advance(sandbox.Submission.RequestId));

        Assert.Equal(MapJobTransition.OutcomeUnknown, machine.Advance(sandbox.Submission.RequestId));
        Assert.Single(runtime.Applied);
    }

    [Fact]
    public void LockedJournal_ReturnsBlockedAndRetainsClaim()
    {
        using var sandbox = new JobSandbox();
        var machine = sandbox.Create(new RecordingRuntime());
        machine.Submit(sandbox.Submission);
        machine.Advance(sandbox.Submission.RequestId);
        using var locked = new FileStream(sandbox.JournalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(MapJobTransition.Blocked, machine.Advance(sandbox.Submission.RequestId));
        Assert.True(File.Exists(sandbox.ClaimPath));
        Assert.True(File.Exists(sandbox.JournalPath));
    }

    [Fact]
    public void InterruptedReversal_BecomesOutcomeUnknownWithoutSecondReverseCall()
    {
        using var sandbox = new JobSandbox();
        var runtime = new RecordingRuntime { ObservationResult = false };
        var first = sandbox.Create(runtime);
        first.Submit(sandbox.Submission);
        Assert.Equal(MapJobTransition.Prepared, first.Advance(sandbox.Submission.RequestId));
        Assert.Equal(MapJobTransition.NativeOperationCalled, first.Advance(sandbox.Submission.RequestId));
        Assert.Equal(MapJobTransition.OperationApplied, first.Advance(sandbox.Submission.RequestId));
        Assert.Equal(MapJobTransition.NativeOperationObserved, first.Advance(sandbox.Submission.RequestId));
        Assert.Equal(MapJobTransition.Reversing, first.Advance(sandbox.Submission.RequestId));
        Assert.Equal(MapJobTransition.NativeReversalCalled, first.Advance(sandbox.Submission.RequestId));
        Assert.Single(runtime.Reversed);

        var restartedRuntime = new RecordingRuntime();
        first.Dispose();
        var restarted = sandbox.Create(restartedRuntime);
        Assert.Equal(MapJobTransition.OutcomeUnknown, restarted.Advance(sandbox.Submission.RequestId));
        Assert.Empty(restartedRuntime.Reversed);
    }

    [Fact]
    public void FailedSecondObservation_ReversesBothOperationsInReverseOrder()
    {
        using var sandbox = new JobSandbox();
        var runtime = new RecordingRuntime { FailObservationAt = 1 };
        var machine = sandbox.Create(runtime);
        machine.Submit(sandbox.Submission);
        AdvanceUntil(machine, sandbox.Submission.RequestId, MapJobTransition.Reversing);

        Assert.Equal(MapJobTransition.NativeReversalCalled, machine.Advance(sandbox.Submission.RequestId));
        Assert.Equal(MapJobTransition.ReversalProgressed, machine.Advance(sandbox.Submission.RequestId));
        Assert.Equal(MapJobTransition.NativeReversalCalled, machine.Advance(sandbox.Submission.RequestId));
        Assert.Equal(MapJobTransition.Reversed, machine.Advance(sandbox.Submission.RequestId));

        Assert.Equal([1, 0], runtime.Reversed);
        Assert.Equal(MapJobState.Reversed, machine.ReadJournal(sandbox.Submission.RequestId)!.State);
    }

    private static void AdvanceUntil(
        UniversalMapJobStateMachine machine,
        string requestId,
        MapJobTransition expected)
    {
        for (var index = 0; index < 30; index++)
        {
            if (machine.Advance(requestId) == expected)
            {
                return;
            }
        }

        throw new Xunit.Sdk.XunitException($"Transition {expected} was not reached.");
    }

    private static int CountSuccessfulBoundaries()
    {
        using var sandbox = new JobSandbox("successful-boundary-count");
        var machine = sandbox.Create(new RecordingRuntime());
        machine.Submit(sandbox.Submission);
        return RunToNoWork(machine, sandbox.Submission.RequestId);
    }

    private static int CountReversalBoundaries()
    {
        using var sandbox = new JobSandbox("reversal-boundary-count");
        var machine = sandbox.Create(new RecordingRuntime { FailObservationAt = 1 });
        machine.Submit(sandbox.Submission);
        return RunToNoWork(machine, sandbox.Submission.RequestId);
    }

    private static int RunToNoWork(UniversalMapJobStateMachine machine, string requestId)
    {
        for (var count = 0; count < 40; count++)
        {
            var transition = machine.Advance(requestId);
            if (transition == MapJobTransition.NoWork)
            {
                return count;
            }

            Assert.NotEqual(MapJobTransition.Blocked, transition);
        }

        throw new Xunit.Sdk.XunitException("The map job did not converge within 40 transitions.");
    }

    private sealed class RecordingRuntime : IUniversalMapJobRuntime
    {
        public bool ObservationResult { get; init; } = true;

        public int? FailObservationAt { get; init; }

        public int NodeCount { get; init; } = 1;

        public ManualResetEventSlim? ApplyEntered { get; init; }

        public ManualResetEventSlim? ApplyRelease { get; init; }

        public List<int> Applied { get; } = [];

        public List<int> Observed { get; } = [];

        public List<int> Reversed { get; } = [];

        public IReadOnlyList<long> ApplyOperation(MapJobSubmission submission, int operationIndex)
        {
            Applied.Add(operationIndex);
            ApplyEntered?.Set();
            ApplyRelease?.Wait(TimeSpan.FromSeconds(5));
            return Enumerable.Range(0, NodeCount).Select(index => 101L + operationIndex * 200_000L + index).ToArray();
        }

        public bool ObserveOperation(MapJobSubmission submission, int operationIndex, IReadOnlyList<long> nativeNodeIds)
        {
            Observed.Add(operationIndex);
            return ObservationResult && operationIndex != FailObservationAt;
        }

        public bool ReverseOperation(MapJobSubmission submission, int operationIndex, IReadOnlyList<long> nativeNodeIds)
        {
            Reversed.Add(operationIndex);
            return true;
        }
    }

    private sealed class JobSandbox : IDisposable
    {
        public JobSandbox(string? suffix = null)
        {
            Root = Path.Combine(Path.GetTempPath(), "ddai-job-" + (suffix ?? Guid.NewGuid().ToString("N")));
            Submission = new MapJobSubmission(
                "job-request-001",
                new string('a', 64),
                "map-001",
                7,
                11,
                new string('c', 64),
                ["op-a", "op-b"],
                "{\"success\":true}",
                "{\"success\":false,\"error\":\"reversed\"}");
        }

        public string Root { get; }

        public MapJobSubmission Submission { get; }

        public string Key => UniversalMapJobStateMachine.RequestKey(Submission.RequestId);

        public string ClaimPath => Path.Combine(Root, "processing", Key + ".json");

        public string JournalPath => Path.Combine(Root, "journal", Key + ".json");

        public string ResponsePath => Path.Combine(Root, "responses", Key + ".json");

        public UniversalMapJobStateMachine Create(IUniversalMapJobRuntime runtime) => new(Root, runtime);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
