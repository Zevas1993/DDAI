using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using DDAI.Core.Mailbox;
using DDAI.Core.MapPlans;

namespace DDAI.Core.Tests;

public sealed class DungeondraftBridgeStateMachineTests
{
    private static readonly DateTimeOffset RequestTimestamp = DateTimeOffset.Parse("2026-08-09T12:00:00Z");
    private static readonly DateTimeOffset FirstResponseTimestamp = DateTimeOffset.Parse("2026-08-09T12:00:01Z");
    private static readonly DateTimeOffset RetryResponseTimestamp = DateTimeOffset.Parse("2026-08-09T12:05:00Z");

    [Fact]
    public void ApplyPlan_CreatesDurableIntentBeforeExecutingExactlyOnce()
    {
        using var sandbox = new MutationBridgeSandbox("mutation-order-001");
        var executeCount = 0;
        var bridge = sandbox.CreateBridge(request =>
        {
            executeCount++;
            return MutationSuccessResponse(request, sandbox.Plan);
        });

        Assert.Equal(BridgeTransition.MutationIntentCreated, bridge.AdvanceClaim(sandbox.FileName));
        Assert.Equal(0, executeCount);
        Assert.True(File.Exists(sandbox.PreparedIntentPath));
        Assert.False(File.Exists(sandbox.ConfirmedIntentPath));

        Assert.Equal(BridgeTransition.MutationConfirmed, bridge.AdvanceClaim(sandbox.FileName));
        Assert.Equal(1, executeCount);
        Assert.True(File.Exists(sandbox.PreparedIntentPath));
        Assert.True(File.Exists(sandbox.ConfirmedIntentPath));
        Assert.False(File.Exists(sandbox.ResponsePath));
    }

    [Fact]
    public void Recovery_PreparedOnlyBecomesAmbiguousWithoutReplayingMutation()
    {
        using var sandbox = new MutationBridgeSandbox("mutation-prepared-restart-001");
        var first = sandbox.CreateBridge(request => MutationSuccessResponse(request, sandbox.Plan));
        Assert.Equal(BridgeTransition.MutationIntentCreated, first.AdvanceClaim(sandbox.FileName));
        var restartedExecuteCount = 0;
        var restarted = sandbox.CreateBridge(_ =>
        {
            restartedExecuteCount++;
            throw new InvalidOperationException("Recovery must never execute a prepared mutation.");
        });

        restarted.RecoverAll();

        Assert.Equal(0, restartedExecuteCount);
        Assert.False(File.Exists(sandbox.ProcessingPath));
        Assert.True(File.Exists(sandbox.PreparedIntentPath));
        Assert.True(File.Exists(sandbox.AmbiguousIntentPath));
        Assert.False(File.Exists(sandbox.ConfirmedIntentPath));
        Assert.False(File.Exists(sandbox.JournalPath));
        var response = ReadResponse(sandbox.ResponsePath);
        Assert.False(response.Success);
        Assert.Equal("mutation_outcome_unknown", response.Error!.Code);
        Assert.Equal(MapPlanJson.Fingerprint(sandbox.Plan), response.Payload.GetProperty("plan_fingerprint").GetString());
        Assert.True(response.Payload.GetProperty("outcome_unknown").GetBoolean());
        Assert.Single(Directory.EnumerateFiles(sandbox.FailedDirectory, "*.mutation-outcome-unknown.json"));
    }

    [Theory]
    [InlineData(2)] // confirmed intent
    [InlineData(3)] // response journal
    [InlineData(4)] // response publication
    [InlineData(5)] // processing claim deletion
    [InlineData(6)] // response journal deletion
    [InlineData(7)] // confirmed intent deletion
    public void Recovery_ConfirmedMutationConvergesWithoutExecutingAgain(int completedTransitions)
    {
        using var sandbox = new MutationBridgeSandbox("mutation-confirmed-boundary-" + completedTransitions);
        var firstExecuteCount = 0;
        var first = sandbox.CreateBridge(request =>
        {
            firstExecuteCount++;
            return MutationSuccessResponse(request, sandbox.Plan);
        });
        for (var index = 0; index < completedTransitions; index++)
        {
            Assert.NotEqual(BridgeTransition.Blocked, first.AdvanceClaim(sandbox.FileName));
        }

        var restartedExecuteCount = 0;
        var restarted = sandbox.CreateBridge(_ =>
        {
            restartedExecuteCount++;
            throw new InvalidOperationException("Confirmed recovery must not execute again.");
        });
        restarted.RecoverAll();

        Assert.Equal(1, firstExecuteCount);
        Assert.Equal(0, restartedExecuteCount);
        Assert.False(File.Exists(sandbox.ProcessingPath));
        Assert.False(File.Exists(sandbox.JournalPath));
        Assert.False(File.Exists(sandbox.PreparedIntentPath));
        Assert.False(File.Exists(sandbox.ConfirmedIntentPath));
        Assert.False(File.Exists(sandbox.AmbiguousIntentPath));
        var response = ReadResponse(sandbox.ResponsePath);
        Assert.True(response.Success);
        Assert.Equal(MapPlanJson.Fingerprint(sandbox.Plan), response.Payload.GetProperty("plan_fingerprint").GetString());
    }

    [Fact]
    public void Mutation_PreparedWriteFailureRetainsClaimAndDoesNotExecute()
    {
        using var sandbox = new MutationBridgeSandbox("mutation-prepared-write-fault");
        Directory.CreateDirectory(sandbox.PreparedIntentPath);
        var executeCount = 0;
        var bridge = sandbox.CreateBridge(request =>
        {
            executeCount++;
            return MutationSuccessResponse(request, sandbox.Plan);
        });

        var transition = bridge.AdvanceClaim(sandbox.FileName);

        Assert.Equal(BridgeTransition.Blocked, transition);
        Assert.Equal(0, executeCount);
        Assert.True(File.Exists(sandbox.ProcessingPath));
        Assert.True(Directory.Exists(sandbox.PreparedIntentPath));
        Assert.False(File.Exists(sandbox.ConfirmedIntentPath));
    }

    [Fact]
    public void Mutation_InvalidExecutedResponseNeverExecutesTwiceAndBecomesAmbiguous()
    {
        using var sandbox = new MutationBridgeSandbox("mutation-response-validation-fault");
        var executeCount = 0;
        var bridge = sandbox.CreateBridge(request =>
        {
            executeCount++;
            return MutationSuccessResponse(request, sandbox.Plan) with
            {
                Payload = JsonSerializer.SerializeToElement(new
                {
                    plan_fingerprint = new string('0', 64),
                }),
            };
        });
        Assert.Equal(BridgeTransition.MutationIntentCreated, bridge.AdvanceClaim(sandbox.FileName));

        Assert.Equal(BridgeTransition.Blocked, bridge.AdvanceClaim(sandbox.FileName));
        Assert.Equal(1, executeCount);
        Assert.Equal(BridgeTransition.MutationAmbiguityRecorded, bridge.AdvanceClaim(sandbox.FileName));
        Assert.Equal(1, executeCount);
        Assert.True(File.Exists(sandbox.PreparedIntentPath));
        Assert.True(File.Exists(sandbox.AmbiguousIntentPath));
    }

    [Theory]
    [InlineData("confirmed")]
    [InlineData("prepared")]
    public void Mutation_LockedIntentCleanupReturnsBlockedAndRetainsSource(string lockedState)
    {
        using var sandbox = new MutationBridgeSandbox("mutation-cleanup-lock-" + lockedState);
        var bridge = sandbox.CreateBridge(request => MutationSuccessResponse(request, sandbox.Plan));
        for (var index = 0; index < 6; index++)
        {
            Assert.NotEqual(BridgeTransition.Blocked, bridge.AdvanceClaim(sandbox.FileName));
        }
        var lockedPath = lockedState == "confirmed"
            ? sandbox.ConfirmedIntentPath
            : sandbox.PreparedIntentPath;
        using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var transition = bridge.AdvanceClaim(sandbox.FileName);

        if (lockedState == "prepared")
        {
            Assert.Equal(BridgeTransition.MutationIntentDeleted, transition);
            transition = bridge.AdvanceClaim(sandbox.FileName);
        }
        Assert.Equal(BridgeTransition.Blocked, transition);
        Assert.True(File.Exists(lockedPath));
        Assert.True(File.Exists(sandbox.ResponsePath));
    }

    [Fact]
    public void Mutation_BlockedAmbiguityDiagnosticRetainsAmbiguousSourceState()
    {
        using var sandbox = new MutationBridgeSandbox("mutation-ambiguous-diagnostic-fault");
        var first = sandbox.CreateBridge(request => MutationSuccessResponse(request, sandbox.Plan));
        Assert.Equal(BridgeTransition.MutationIntentCreated, first.AdvanceClaim(sandbox.FileName));
        var restarted = sandbox.CreateBridge(_ => throw new InvalidOperationException());
        Assert.Equal(BridgeTransition.MutationAmbiguityRecorded, restarted.AdvanceClaim(sandbox.FileName));
        Assert.Equal(BridgeTransition.JournalCreated, restarted.AdvanceClaim(sandbox.FileName));
        Assert.Equal(BridgeTransition.ResponsePublished, restarted.AdvanceClaim(sandbox.FileName));
        Assert.Equal(BridgeTransition.ClaimDeleted, restarted.AdvanceClaim(sandbox.FileName));
        Assert.Equal(BridgeTransition.JournalDeleted, restarted.AdvanceClaim(sandbox.FileName));
        Directory.Delete(sandbox.FailedDirectory);
        File.WriteAllText(sandbox.FailedDirectory, "blocks diagnostic directory");

        var transition = restarted.AdvanceClaim(sandbox.FileName);

        Assert.Equal(BridgeTransition.Blocked, transition);
        Assert.True(File.Exists(sandbox.PreparedIntentPath));
        Assert.True(File.Exists(sandbox.AmbiguousIntentPath));
        Assert.True(File.Exists(sandbox.ResponsePath));
    }

    [Fact]
    public void Mutation_ForgedDiagnosticDirectoryBlocksAndRetainsAmbiguousState()
    {
        using var sandbox = new MutationBridgeSandbox("mutation-forged-diagnostic-directory");
        var first = sandbox.CreateBridge(request => MutationSuccessResponse(request, sandbox.Plan));
        Assert.Equal(BridgeTransition.MutationIntentCreated, first.AdvanceClaim(sandbox.FileName));
        var restarted = sandbox.CreateBridge(_ => throw new InvalidOperationException());
        Assert.Equal(BridgeTransition.MutationAmbiguityRecorded, restarted.AdvanceClaim(sandbox.FileName));
        Assert.Equal(BridgeTransition.JournalCreated, restarted.AdvanceClaim(sandbox.FileName));
        Assert.Equal(BridgeTransition.ResponsePublished, restarted.AdvanceClaim(sandbox.FileName));
        Assert.Equal(BridgeTransition.ClaimDeleted, restarted.AdvanceClaim(sandbox.FileName));
        Assert.Equal(BridgeTransition.JournalDeleted, restarted.AdvanceClaim(sandbox.FileName));
        var diagnosticPath = Path.Combine(
            sandbox.FailedDirectory,
            sandbox.Key + ".mutation-outcome-unknown.json");
        Directory.CreateDirectory(diagnosticPath);

        var transition = restarted.AdvanceClaim(sandbox.FileName);

        Assert.Equal(BridgeTransition.Blocked, transition);
        Assert.True(Directory.Exists(diagnosticPath));
        Assert.True(File.Exists(sandbox.PreparedIntentPath));
        Assert.True(File.Exists(sandbox.AmbiguousIntentPath));
    }

    [Fact]
    public void Mutation_CorruptResponseJournalBlocksIntentCleanup()
    {
        using var sandbox = new MutationBridgeSandbox("mutation-corrupt-response-journal");
        var bridge = sandbox.CreateBridge(request => MutationSuccessResponse(request, sandbox.Plan));
        for (var index = 0; index < 5; index++)
        {
            Assert.NotEqual(BridgeTransition.Blocked, bridge.AdvanceClaim(sandbox.FileName));
        }
        File.WriteAllText(sandbox.JournalPath, "{ malformed");

        var transition = bridge.AdvanceClaim(sandbox.FileName);

        Assert.Equal(BridgeTransition.Blocked, transition);
        Assert.True(File.Exists(sandbox.JournalPath));
        Assert.True(File.Exists(sandbox.PreparedIntentPath));
        Assert.True(File.Exists(sandbox.ConfirmedIntentPath));
        Assert.True(File.Exists(sandbox.ResponsePath));
    }

    [Fact]
    public void Mutation_NoncanonicalRequestFingerprintBlocksClaimlessIntentCleanup()
    {
        using var sandbox = new MutationBridgeSandbox("mutation-bad-request-fingerprint");
        var bridge = sandbox.CreateBridge(request => MutationSuccessResponse(request, sandbox.Plan));
        for (var index = 0; index < 6; index++)
        {
            Assert.NotEqual(BridgeTransition.Blocked, bridge.AdvanceClaim(sandbox.FileName));
        }
        var intent = JsonNode.Parse(File.ReadAllText(sandbox.ConfirmedIntentPath))!.AsObject();
        intent["request_fingerprint"] = "not-a-sha256";
        File.WriteAllText(sandbox.ConfirmedIntentPath, intent.ToJsonString(BridgeWireJson.Options));

        var transition = bridge.AdvanceClaim(sandbox.FileName);

        Assert.Equal(BridgeTransition.Blocked, transition);
        Assert.True(File.Exists(sandbox.ConfirmedIntentPath));
        Assert.True(File.Exists(sandbox.PreparedIntentPath));
    }

    [Theory]
    [InlineData("prepared")]
    [InlineData("confirmed")]
    [InlineData("ambiguous")]
    public void Mutation_CorruptIntentBlocksWithoutDeletingSourceOrExecuting(string state)
    {
        using var sandbox = new MutationBridgeSandbox("mutation-corrupt-" + state);
        var executeCount = 0;
        var bridge = sandbox.CreateBridge(request =>
        {
            executeCount++;
            return MutationSuccessResponse(request, sandbox.Plan);
        });
        Assert.Equal(BridgeTransition.MutationIntentCreated, bridge.AdvanceClaim(sandbox.FileName));
        string path;
        if (state == "confirmed")
        {
            Assert.Equal(BridgeTransition.MutationConfirmed, bridge.AdvanceClaim(sandbox.FileName));
            path = sandbox.ConfirmedIntentPath;
        }
        else if (state == "ambiguous")
        {
            var restarted = sandbox.CreateBridge(_ => throw new InvalidOperationException());
            Assert.Equal(BridgeTransition.MutationAmbiguityRecorded, restarted.AdvanceClaim(sandbox.FileName));
            bridge = restarted;
            path = sandbox.AmbiguousIntentPath;
        }
        else
        {
            path = sandbox.PreparedIntentPath;
        }
        var intent = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        intent["state"] = "tampered";
        File.WriteAllText(path, intent.ToJsonString(BridgeWireJson.Options));
        var before = executeCount;

        var transition = bridge.AdvanceClaim(sandbox.FileName);

        Assert.Equal(BridgeTransition.Blocked, transition);
        Assert.Equal(before, executeCount);
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(sandbox.ProcessingPath));
    }

    [Theory]
    [InlineData(0)] // crash before journal creation
    [InlineData(1)] // crash after journal creation
    [InlineData(2)] // crash after response publication
    [InlineData(3)] // crash after processing-claim deletion
    public void Recovery_ConvergesFromEveryDurabilityBoundary(int completedTransitions)
    {
        using var sandbox = new BridgeSandbox("crash-boundary-" + completedTransitions);
        var prepareCount = 0;
        var bridge = sandbox.CreateBridge(_ =>
        {
            prepareCount++;
            return SuccessResponse(sandbox.Request, FirstResponseTimestamp, "prepared-once");
        });

        for (var index = 0; index < completedTransitions; index++)
        {
            Assert.NotEqual(BridgeTransition.Blocked, bridge.AdvanceClaim(sandbox.FileName));
        }

        var restartedPrepareCount = 0;
        var restarted = sandbox.CreateBridge(_ =>
        {
            restartedPrepareCount++;
            return SuccessResponse(sandbox.Request, RetryResponseTimestamp, "regenerated");
        });

        restarted.RecoverAll();

        Assert.False(File.Exists(sandbox.ProcessingPath));
        Assert.False(File.Exists(sandbox.JournalPath));
        var response = ReadResponse(sandbox.ResponsePath);
        Assert.Equal(completedTransitions == 0 ? RetryResponseTimestamp : FirstResponseTimestamp, response.Timestamp);
        Assert.Equal(completedTransitions == 0 ? "regenerated" : "prepared-once", response.Payload.GetProperty("state").GetString());
        Assert.Equal(completedTransitions == 0 ? 1 : 0, restartedPrepareCount);
        Assert.Equal(completedTransitions == 0 ? 0 : 1, prepareCount);
    }

    [Fact]
    public void Retry_ReusesExactJournaledResponseWithoutCallingFactoryAgain()
    {
        using var sandbox = new BridgeSandbox("exact-journal-retry");
        var first = sandbox.CreateBridge(_ => SuccessResponse(sandbox.Request, FirstResponseTimestamp, "first-body"));
        Assert.Equal(BridgeTransition.JournalCreated, first.AdvanceClaim(sandbox.FileName));
        var exactJournalText = File.ReadAllText(sandbox.JournalPath);

        var restarted = sandbox.CreateBridge(_ => throw new InvalidOperationException("A valid journal must suppress response regeneration."));
        restarted.RecoverAll();

        using var journal = JsonDocument.Parse(exactJournalText);
        var exactResponseText = journal.RootElement.GetProperty("response_text").GetString();
        Assert.Equal(exactResponseText, File.ReadAllText(sandbox.ResponsePath));
        var response = ReadResponse(sandbox.ResponsePath);
        Assert.Equal(FirstResponseTimestamp, response.Timestamp);
        Assert.Equal("first-body", response.Payload.GetProperty("state").GetString());
    }

    [Fact]
    public void IdenticalSameKeyRequest_IsDeletedWhileProcessingClaimRemainsAuthoritative()
    {
        using var sandbox = new BridgeSandbox("identical-duplicate", writeRequestDuplicate: true, duplicateJson: "{\n  \"payload\": {}, \"timestamp\": \"2026-08-09T12:00:00+00:00\", \"command\": \"status\", \"request_id\": \"identical-duplicate\", \"schema_version\": \"1.0\"\n}");
        var bridge = sandbox.CreateBridge(_ => SuccessResponse(sandbox.Request, FirstResponseTimestamp, "ready"));

        Assert.Equal(BridgeTransition.JournalCreated, bridge.AdvanceClaim(sandbox.FileName));

        Assert.False(File.Exists(sandbox.RequestPath));
        Assert.True(File.Exists(sandbox.ProcessingPath));
        Assert.Empty(Directory.EnumerateFiles(sandbox.FailedDirectory, "*.duplicate-conflict.*.json"));
    }

    [Fact]
    public void ConflictingSameKeyRequest_IsPreservedAsUniqueFailureWhileProcessingClaimContinues()
    {
        using var sandbox = new BridgeSandbox("conflicting-duplicate", writeRequestDuplicate: true, duplicateJson: RequestJson("conflicting-duplicate", "other-command"));
        var bridge = sandbox.CreateBridge(_ => SuccessResponse(sandbox.Request, FirstResponseTimestamp, "ready"));

        Assert.Equal(BridgeTransition.JournalCreated, bridge.AdvanceClaim(sandbox.FileName));

        Assert.False(File.Exists(sandbox.RequestPath));
        Assert.True(File.Exists(sandbox.ProcessingPath));
        var failed = Assert.Single(Directory.EnumerateFiles(sandbox.FailedDirectory, "*.duplicate-conflict.*.json"));
        Assert.Equal(RequestJson("conflicting-duplicate", "other-command"), File.ReadAllText(failed));
    }

    [Fact]
    public void MatchingExistingResponse_IsVerifiedAndCompleted()
    {
        using var sandbox = new BridgeSandbox("matching-response");
        var expected = SuccessResponse(sandbox.Request, FirstResponseTimestamp, "ready");
        File.WriteAllText(sandbox.ResponsePath, JsonSerializer.Serialize(expected, BridgeWireJson.OptionsIndented));
        var bridge = sandbox.CreateBridge(_ => expected);

        bridge.RecoverAll();

        Assert.False(File.Exists(sandbox.ProcessingPath));
        Assert.False(File.Exists(sandbox.JournalPath));
        Assert.Equal("ready", ReadResponse(sandbox.ResponsePath).Payload.GetProperty("state").GetString());
    }

    [Fact]
    public void ConflictingExistingResponse_IsNeverOverwrittenAndRetainsRecoverableClaimAndJournal()
    {
        using var sandbox = new BridgeSandbox("conflicting-response");
        var occupied = SuccessResponse(sandbox.Request, FirstResponseTimestamp, "attacker-body");
        var occupiedText = JsonSerializer.Serialize(occupied, BridgeWireJson.Options);
        File.WriteAllText(sandbox.ResponsePath, occupiedText);
        var bridge = sandbox.CreateBridge(_ => SuccessResponse(sandbox.Request, RetryResponseTimestamp, "journal-body"));

        Assert.Equal(BridgeTransition.JournalCreated, bridge.AdvanceClaim(sandbox.FileName));
        Assert.Equal(BridgeTransition.Blocked, bridge.AdvanceClaim(sandbox.FileName));

        Assert.Equal(occupiedText, File.ReadAllText(sandbox.ResponsePath));
        Assert.True(File.Exists(sandbox.ProcessingPath));
        Assert.True(File.Exists(sandbox.JournalPath));
        Assert.Single(Directory.EnumerateFiles(sandbox.FailedDirectory, "*.response-conflict.*.json"));
    }

    [Fact]
    public void StaleJournalWithoutClaim_IsRemovedOnlyWhenResponseMatches()
    {
        using var matching = new BridgeSandbox("stale-journal-matching");
        var matchingBridge = matching.CreateBridge(_ => SuccessResponse(matching.Request, FirstResponseTimestamp, "ready"));
        Assert.Equal(BridgeTransition.JournalCreated, matchingBridge.AdvanceClaim(matching.FileName));
        Assert.Equal(BridgeTransition.ResponsePublished, matchingBridge.AdvanceClaim(matching.FileName));
        File.Delete(matching.ProcessingPath);

        matchingBridge.RecoverAll();

        Assert.False(File.Exists(matching.JournalPath));

        using var conflicting = new BridgeSandbox("stale-journal-conflicting");
        var conflictingBridge = conflicting.CreateBridge(_ => SuccessResponse(conflicting.Request, FirstResponseTimestamp, "ready"));
        Assert.Equal(BridgeTransition.JournalCreated, conflictingBridge.AdvanceClaim(conflicting.FileName));
        File.WriteAllText(conflicting.ResponsePath, JsonSerializer.Serialize(SuccessResponse(conflicting.Request, FirstResponseTimestamp, "wrong"), BridgeWireJson.Options));
        File.Delete(conflicting.ProcessingPath);

        conflictingBridge.RecoverAll();

        Assert.True(File.Exists(conflicting.JournalPath));
    }

    [Fact]
    public void StaleJournalWithMismatchedRequestId_IsNotAcceptedEvenWhenResponseTextMatches()
    {
        using var sandbox = new BridgeSandbox("stale-journal-wrong-id");
        var bridge = sandbox.CreateBridge(_ => SuccessResponse(sandbox.Request, FirstResponseTimestamp, "ready"));
        Assert.Equal(BridgeTransition.JournalCreated, bridge.AdvanceClaim(sandbox.FileName));
        Assert.Equal(BridgeTransition.ResponsePublished, bridge.AdvanceClaim(sandbox.FileName));
        File.Delete(sandbox.ProcessingPath);
        var journal = JsonNode.Parse(File.ReadAllText(sandbox.JournalPath))!.AsObject();
        journal["request_id"] = "different-request";
        File.WriteAllText(sandbox.JournalPath, journal.ToJsonString(BridgeWireJson.Options));

        bridge.RecoverAll();

        Assert.True(File.Exists(sandbox.JournalPath));
    }

    [Theory]
    [MemberData(nameof(WireTimestampContractCases.All), MemberType = typeof(WireTimestampContractCases))]
    public void RequestValidation_MatchesCanonicalWireTimestampContract(string timestamp, bool accepted)
    {
        using var sandbox = new RawBridgeSandbox("timestamp-validation", RequestJson("timestamp-validation", "status", timestamp));
        var bridge = new DungeondraftBridgeStateMachine(
            sandbox.Root,
            request => SuccessResponse(request, FirstResponseTimestamp, "ready"));

        var result = bridge.AdvanceClaim(sandbox.FileName);

        Assert.Equal(accepted ? BridgeTransition.JournalCreated : BridgeTransition.InvalidClaimFailed, result);
        Assert.Equal(accepted, File.Exists(sandbox.ProcessingPath));
    }

    [Fact]
    public void AtomicMailbox_DoesNotOccupyBridgeResponseJournalBeforeClaim()
    {
        using var sandbox = new RawBridgeSandbox("controller-journal", null);
        var mailbox = new AtomicMailbox(sandbox.Root);

        Assert.True(mailbox.PublishRequest(MailboxRequest.CreateStatus("controller-journal", RequestTimestamp)));

        Assert.Empty(Directory.EnumerateFiles(Path.Combine(sandbox.Root, "journal"), "*.json"));
    }

    [Fact]
    public void IdenticalDuplicateDeleteFailure_RetainsBothFilesAndDoesNotJournal()
    {
        using var sandbox = new BridgeSandbox("duplicate-delete-fault", writeRequestDuplicate: true);
        var bridge = sandbox.CreateBridge(_ => SuccessResponse(sandbox.Request, FirstResponseTimestamp, "ready"));
        using var lockFile = new FileStream(sandbox.RequestPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.ThrowsAny<IOException>(() => bridge.AdvanceClaim(sandbox.FileName));

        Assert.True(File.Exists(sandbox.RequestPath));
        Assert.True(File.Exists(sandbox.ProcessingPath));
        Assert.False(File.Exists(sandbox.JournalPath));
    }

    [Fact]
    public void ConflictingDuplicateMoveFailure_RetainsBothFilesAndDoesNotJournal()
    {
        using var sandbox = new BridgeSandbox("duplicate-move-fault", writeRequestDuplicate: true, duplicateJson: RequestJson("duplicate-move-fault", "other"));
        var bridge = sandbox.CreateBridge(_ => SuccessResponse(sandbox.Request, FirstResponseTimestamp, "ready"));
        using var lockFile = new FileStream(sandbox.RequestPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.ThrowsAny<IOException>(() => bridge.AdvanceClaim(sandbox.FileName));

        Assert.True(File.Exists(sandbox.RequestPath));
        Assert.True(File.Exists(sandbox.ProcessingPath));
        Assert.False(File.Exists(sandbox.JournalPath));
    }

    [Fact]
    public void InvalidClaimFailurePublicationFault_RetainsOnlySourceClaim()
    {
        using var sandbox = new RawBridgeSandbox("invalid-claim-fault", "{ malformed");
        var bridge = new DungeondraftBridgeStateMachine(sandbox.Root, _ => throw new InvalidOperationException());
        var failedDirectory = Path.Combine(sandbox.Root, "failed");
        Directory.Delete(failedDirectory);
        File.WriteAllText(failedDirectory, "blocks failed-record directory recreation");

        Assert.ThrowsAny<IOException>(() => bridge.AdvanceClaim(sandbox.FileName));

        Assert.True(File.Exists(sandbox.ProcessingPath));
        Assert.False(File.Exists(Path.Combine(sandbox.Root, "journal", sandbox.FileName)));
    }

    private static string RequestJson(string requestId, string command, string timestamp = "2026-08-09T12:00:00Z") =>
        $"{{\"schema_version\":\"1.0\",\"request_id\":\"{requestId}\",\"command\":\"{command}\",\"timestamp\":\"{timestamp}\",\"payload\":{{}}}}";

    private static MailboxResponse SuccessResponse(MailboxRequest request, DateTimeOffset timestamp, string state) => new()
    {
        SchemaVersion = MailboxRequest.CurrentSchemaVersion,
        RequestId = request.RequestId,
        Command = request.Command,
        Timestamp = timestamp,
        Success = true,
        Payload = JsonSerializer.SerializeToElement(new { state }),
    };

    private static MailboxResponse MutationSuccessResponse(MailboxRequest request, MapPlan plan) => new()
    {
        SchemaVersion = MailboxRequest.CurrentSchemaVersion,
        RequestId = request.RequestId,
        Command = request.Command,
        Timestamp = FirstResponseTimestamp,
        Success = true,
        Payload = JsonSerializer.SerializeToElement(new
        {
            applied = true,
            created_walls = 1,
            room_id = plan.Rooms[0].Id,
            undo_available = true,
            undo_instruction = "Use Dungeondraft Undo once",
            plan_fingerprint = MapPlanJson.Fingerprint(plan),
        }),
    };

    private static MailboxResponse ReadResponse(string path) =>
        JsonSerializer.Deserialize<MailboxResponse>(File.ReadAllText(path), BridgeWireJson.Options)!;

    private class RawBridgeSandbox : IDisposable
    {
        public RawBridgeSandbox(string requestId, string? processingText)
        {
            Root = Path.Combine(Path.GetTempPath(), "ddai-bridge-state-tests", Guid.NewGuid().ToString("N"));
            foreach (var directory in new[] { "requests", "processing", "responses", "failed", "journal", "mutation-intents" })
            {
                Directory.CreateDirectory(Path.Combine(Root, directory));
            }

            Key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestId))).ToLowerInvariant();
            FileName = Key + ".json";
            ProcessingPath = Path.Combine(Root, "processing", FileName);
            if (processingText is not null)
            {
                File.WriteAllText(ProcessingPath, processingText);
            }
        }

        public string Root { get; }
        public string Key { get; }
        public string FileName { get; }
        public string ProcessingPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class BridgeSandbox : RawBridgeSandbox
    {
        public BridgeSandbox(string requestId, bool writeRequestDuplicate = false, string? duplicateJson = null)
            : base(requestId, RequestJson(requestId, "status"))
        {
            Request = MailboxRequest.CreateStatus(requestId, RequestTimestamp);
            RequestPath = Path.Combine(Root, "requests", FileName);
            ResponsePath = Path.Combine(Root, "responses", FileName);
            JournalPath = Path.Combine(Root, "journal", FileName);
            FailedDirectory = Path.Combine(Root, "failed");
            if (writeRequestDuplicate)
            {
                File.WriteAllText(RequestPath, duplicateJson ?? RequestJson(requestId, "status"));
            }
        }

        public MailboxRequest Request { get; }
        public string RequestPath { get; }
        public string ResponsePath { get; }
        public string JournalPath { get; }
        public string FailedDirectory { get; }

        public DungeondraftBridgeStateMachine CreateBridge(Func<MailboxRequest, MailboxResponse> prepareResponse) =>
            new(Root, prepareResponse);
    }

    private sealed class MutationBridgeSandbox : RawBridgeSandbox
    {
        public MutationBridgeSandbox(string requestId)
            : base(requestId, ApplyRequestJson(requestId))
        {
            Plan = ValidPlan(requestId);
            Request = MailboxRequest.CreateApplyPlan(Plan, RequestTimestamp);
            ResponsePath = Path.Combine(Root, "responses", FileName);
            JournalPath = Path.Combine(Root, "journal", FileName);
            FailedDirectory = Path.Combine(Root, "failed");
            PreparedIntentPath = Path.Combine(Root, "mutation-intents", Key + ".prepared.json");
            ConfirmedIntentPath = Path.Combine(Root, "mutation-intents", Key + ".confirmed.json");
            AmbiguousIntentPath = Path.Combine(Root, "mutation-intents", Key + ".ambiguous.json");
        }

        public MapPlan Plan { get; }
        public MailboxRequest Request { get; }
        public string ResponsePath { get; }
        public string JournalPath { get; }
        public string FailedDirectory { get; }
        public string PreparedIntentPath { get; }
        public string ConfirmedIntentPath { get; }
        public string AmbiguousIntentPath { get; }

        public DungeondraftBridgeStateMachine CreateBridge(Func<MailboxRequest, MailboxResponse> executeMutation) =>
            new(
                Root,
                request => SuccessResponse(request, FirstResponseTimestamp, "status-only"),
                executeMutation);

        private static string ApplyRequestJson(string requestId) =>
            JsonSerializer.Serialize(
                MailboxRequest.CreateApplyPlan(ValidPlan(requestId), RequestTimestamp),
                BridgeWireJson.Options);

        private static MapPlan ValidPlan(string requestId) => new()
        {
            SchemaVersion = MapPlan.CurrentSchemaVersion,
            RequestId = requestId,
            BaseRevision = 0,
            Mode = MapOperationMode.Add,
            Canvas = new MapCanvas(40, 30),
            Rooms = [new MapRoom("room-entrance", 8, 7, 10, 8)],
        };
    }
}
