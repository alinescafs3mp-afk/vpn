using System.Text;
using System.Text.Json;
using AutoVpn.TestSupport;

namespace AutoVpn.UnitTests;

public sealed class FileUseProgressCaptureTests
{
    private const string PrivateText = "SYNTHETIC_PRIVATE_REQUEST_секрет";

    [Fact]
    public void CompleteEmptyResultSurvivesArbitraryByteBoundaries()
    {
        var capture = new FileUseProgressCapture(() => 125);
        var frames = EmptyObservation();
        var bytes = Encode(frames);
        // Real UTF-8 JSON is parsed only after its complete line, even when the
        // stream delivers every byte separately instead of delivering frames.
        foreach (var value in bytes) capture.Append([value]);
        Assert.Throws<InvalidDataException>(() => capture.RequireResult());
        capture.Finish();

        var result = capture.RequireResult();
        Assert.Equal(EmptySnapshot(), result);
        Assert.True(result.Incomplete);
        Assert.Equal(0, result.TotalOwners);
        var observed = capture.Latest;
        Assert.Equal("RESULT", observed.State);
        Assert.Equal("NONE", observed.ErrorKind);
        Assert.Equal(frames.Length, observed.Frames);
        Assert.Equal(bytes.Length, observed.BytesObserved);
        Assert.Equal(bytes.Length, observed.BytesRetained);
        Assert.Equal("RESULT_READY", observed.LastPhase);
        Assert.Equal(125, observed.ObservedMilliseconds);
        Assert.True(observed.Eof);
        Assert.False(observed.PartialLine);
        Assert.False(observed.Truncated);
        Assert.False(capture.InputClosed.IsCompleted);
        Assert.Equal(observed, capture.BeforeDeadline);
    }

    [Fact]
    public void DeadlineSnapshotRemainsImmutableWhenResultAndEofArriveLate()
    {
        long elapsed = 1999;
        var capture = new FileUseProgressCapture(() => elapsed);
        var frames = EmptyObservation();
        capture.Append(Encode(frames[..8]));
        var before = capture.BeforeDeadline;
        var originalJson = JsonSerializer.Serialize(before);
        Assert.Equal("QUERY_BEGIN", before.LastPhase);
        Assert.Equal(1, before.QueryOrdinal);
        Assert.Equal("PREFIX", before.State);
        Assert.False(before.Eof);

        // No cancellation callback is needed for this boundary. Bytes first
        // observed at exactly the deadline cannot amend the earlier snapshot.
        elapsed = 2000;
        capture.Append(Encode(frames[8..]));
        elapsed = 2500;
        capture.Finish();

        Assert.Same(before, capture.BeforeDeadline);
        Assert.Equal(originalJson, JsonSerializer.Serialize(capture.BeforeDeadline));
        Assert.Equal("RESULT", capture.Latest.State);
        Assert.Equal("RESULT_READY", capture.Latest.LastPhase);
        Assert.True(capture.Latest.Eof);
        Assert.Equal(2000, capture.Latest.ObservedMilliseconds);
        Assert.Equal(EmptySnapshot(), capture.RequireResult());
        Assert.True(capture.Latest.BytesObserved > before.BytesObserved);

        // The actual budget token can cancel while the separate stopwatch still
        // reports 1999 whole milliseconds. Freeze on that signal too, without
        // inventing 2000 ms or changing the existing nominal clock boundary.
        elapsed = 1999;
        var canceled = false;
        var signaled = new FileUseProgressCapture(() => elapsed, () => canceled);
        var parent = new FileUseParentProgress(() => elapsed, () => canceled);
        signaled.Append(Encode(frames[..8]));
        parent.Advance(FileUseParentPhase.PROCESS_WAIT);
        var signaledBefore = signaled.BeforeDeadline;
        var parentBefore = parent.BeforeDeadline;
        canceled = true;
        signaled.Append(Encode(frames[8..]));
        signaled.Finish();
        parent.Advance(FileUseParentPhase.OUTPUT_JOIN);
        Assert.Same(signaledBefore, signaled.BeforeDeadline);
        Assert.Same(parentBefore, parent.BeforeDeadline);
        Assert.Equal(1999, signaled.Latest.ObservedMilliseconds);
        Assert.Equal(new FileUseParentPhaseReport("OUTPUT_JOIN", 1999), parent.Latest);
        Assert.Equal("RESULT", signaled.Latest.State);
        Assert.True(signaled.Latest.Eof);
    }

    [Fact]
    public void MalformedOrPrivateFramesAreRejectedWithoutLeakingTheirContents()
    {
        var invalid = new[]
        {
            "{\"Kind\":\"PHASE\",\"Sequence\":1,\"ElapsedMilliseconds\":0,\"Phase\":\"" + PrivateText + "\"}\n",
            "{\"Kind\":\"PHASE\",\"Sequence\":1,\"Sequence\":1,\"ElapsedMilliseconds\":0,\"Phase\":\"HELPER_STARTED\"}\n",
            "{\"Kind\":0,\"Sequence\":1,\"ElapsedMilliseconds\":0,\"Phase\":\"HELPER_STARTED\"}\n",
            "{\"Kind\":\"PHASE\",\"Sequence\":1,\"ElapsedMilliseconds\":0,\"Phase\":0}\n",
            "{\"Kind\":\"PHASE\",\"Sequence\":1,\"ElapsedMilliseconds\":0,\"Phase\":\"HELPER_STARTED\",\"path\":\"" + PrivateText + "\"}\n",
        };
        foreach (var payload in invalid)
        {
            var capture = new FileUseProgressCapture(() => 10);
            // This splits the actual multibyte Russian text as well as JSON
            // syntax. It must never be mistaken for a valid progress frame.
            foreach (var value in Encoding.UTF8.GetBytes(payload)) capture.Append([value]);
            capture.Finish();
            Assert.Equal("FRAME_JSON", capture.Latest.ErrorKind);
            RequireInvalidAndPrivate(capture);
            Assert.False(capture.InputClosed.IsCompleted);
            Assert.Equal(0, capture.Latest.Frames);
            Assert.Null(capture.Latest.LastPhase);
        }
    }

    [Fact]
    public void InvalidTransitionsOrdinalsSequencesAndFramesAfterResultAreRejected()
    {
        var normal = EmptyObservation();
        var invalid = new (FileUseDiagnosticFrame[] Frames, string Error)[]
        {
            ([Phase(1, FileUsePhase.HELPER_STARTED), Phase(2, FileUsePhase.INPUT_VALIDATED)], "FRAME_ORDER"),
            ([Phase(1, FileUsePhase.HELPER_STARTED), Phase(3, FileUsePhase.INPUT_RECEIVED)], "FRAME_SEQUENCE"),
            ([Phase(1, FileUsePhase.HELPER_STARTED), Phase(1, FileUsePhase.INPUT_RECEIVED)], "FRAME_SEQUENCE"),
            ([Phase(1, FileUsePhase.HELPER_STARTED) with { ElapsedMilliseconds = 10 },
                Phase(2, FileUsePhase.INPUT_RECEIVED) with { ElapsedMilliseconds = 9 }], "FRAME_SEQUENCE"),
            ([.. normal[..7], Phase(8, FileUsePhase.QUERY_BEGIN, 2)], "FRAME_ORDER"),
            ([.. normal[..8], Phase(9, FileUsePhase.QUERY_RETURNED, 2)], "FRAME_ORDER"),
            ([Phase(1, FileUsePhase.HELPER_STARTED, 1)], "FRAME_ORDER"),
            ([.. normal, Phase(normal.Length + 1, FileUsePhase.HELPER_STARTED)], "AFTER_RESULT"),
        };
        foreach (var (frames, error) in invalid)
        {
            var capture = new FileUseProgressCapture(() => 20);
            capture.Append(Encode(frames));
            capture.Finish();
            Assert.Equal(error, capture.Latest.ErrorKind);
            RequireInvalidAndPrivate(capture);
        }
    }

    [Fact]
    public void PartialFrameAtEofPreservesOnlyTheLastCompleteObservation()
    {
        var capture = new FileUseProgressCapture(() => 100);
        capture.Append(Encode([Phase(1, FileUsePhase.HELPER_STARTED)]));
        var partial = Encode([Phase(2, FileUsePhase.INPUT_RECEIVED)]);
        capture.Append(partial.AsSpan(0, partial.Length - 1));
        Assert.Equal(1, capture.Latest.Frames);
        Assert.Equal("HELPER_STARTED", capture.Latest.LastPhase);
        Assert.True(capture.Latest.PartialLine);
        capture.Finish();

        Assert.Equal("INCOMPLETE_FRAME", capture.Latest.ErrorKind);
        Assert.True(capture.Latest.Eof);
        Assert.True(capture.Latest.PartialLine);
        Assert.Equal(1, capture.Latest.Frames);
        Assert.Equal("HELPER_STARTED", capture.Latest.LastPhase);
        RequireInvalidAndPrivate(capture);
        Assert.False(capture.InputClosed.IsCompleted);
    }

    [Fact]
    public void OutputLimitAcceptsExactBoundaryAndRetainsNoMoreOnOverflow()
    {
        var frame = Encode([Phase(1, FileUsePhase.HELPER_STARTED)]);
        var padded = new byte[4096];
        frame.AsSpan(0, frame.Length - 1).CopyTo(padded);
        padded.AsSpan(frame.Length - 1).Fill((byte)' ');
        padded[^1] = (byte)'\n';
        var exact = new FileUseProgressCapture(() => 10);
        exact.Append(padded);
        exact.Finish();
        Assert.Equal("PREFIX", exact.Latest.State);
        Assert.Equal("NONE", exact.Latest.ErrorKind);
        Assert.Equal(1, exact.Latest.Frames);
        Assert.Equal(4096, exact.Latest.BytesObserved);
        Assert.Equal(4096, exact.Latest.BytesRetained);
        Assert.False(exact.Latest.Truncated);
        Assert.False(exact.Latest.PartialLine);

        var overflow = new FileUseProgressCapture(() => 10);
        overflow.Append(padded);
        overflow.Append([(byte)' ']);
        overflow.Finish();
        Assert.Equal("OUTPUT_LIMIT", overflow.Latest.ErrorKind);
        Assert.Equal(4097, overflow.Latest.BytesObserved);
        Assert.Equal(4096, overflow.Latest.BytesRetained);
        Assert.True(overflow.Latest.Truncated);
        Assert.Equal(1, overflow.Latest.Frames);
        RequireInvalidAndPrivate(overflow);
    }

    [Fact]
    public void InvalidResultCountersAndPrivateStateCannotBecomeAcceptedEvidence()
    {
        var invalid = new[]
        {
            EmptySnapshot() with { QueryCalls = 3 },
            EmptySnapshot() with { TotalOwners = 33, OtherOwners = 33 },
            EmptySnapshot() with { TotalOwners = 1, TestHostOwners = int.MaxValue, OtherOwners = int.MaxValue },
            EmptySnapshot() with { OwnedChildOwners = -1, OtherOwners = 1 },
            EmptySnapshot() with { State = "OBSERVED", Incomplete = false },
            EmptySnapshot() with { State = PrivateText },
        };
        foreach (var result in invalid)
        {
            var frames = EmptyObservation();
            frames[^1] = frames[^1] with { Snapshot = result };
            var capture = new FileUseProgressCapture(() => 30);
            capture.Append(Encode(frames));
            capture.Finish();
            Assert.Equal("FRAME_SHAPE", capture.Latest.ErrorKind);
            Assert.Equal("RESULT_READY", capture.Latest.LastPhase);
            RequireInvalidAndPrivate(capture);
        }
    }

    [Fact]
    public void InputClosedGateRequiresItsValidCompleteProtocolFrame()
    {
        var capture = new FileUseProgressCapture(() => 10);
        capture.Append(Encode([Phase(1, FileUsePhase.HELPER_STARTED)]));
        var closed = Encode([Phase(2, FileUsePhase.INPUT_CLOSED)]);
        capture.Append(closed.AsSpan(0, closed.Length - 1));
        Assert.False(capture.InputClosed.IsCompleted);
        capture.Append(closed.AsSpan(closed.Length - 1));
        Assert.True(capture.InputClosed.IsCompletedSuccessfully);
        Assert.Equal("INPUT_CLOSED", capture.Latest.LastPhase);
        Assert.Equal("PREFIX", capture.Latest.State);

        var invalid = new FileUseProgressCapture(() => 10);
        invalid.Append(Encode([Phase(1, FileUsePhase.INPUT_CLOSED)]));
        invalid.Finish();
        Assert.False(invalid.InputClosed.IsCompleted);
        Assert.Equal("FRAME_ORDER", invalid.Latest.ErrorKind);
        RequireInvalidAndPrivate(invalid);
    }

    private static void RequireInvalidAndPrivate(FileUseProgressCapture capture)
    {
        Assert.Equal("INVALID", capture.Latest.State);
        var failure = Assert.Throws<InvalidDataException>(() => capture.RequireResult());
        Assert.Equal("FILE_USE_PROTOCOL_INVALID", failure.Message);
        var safe = JsonSerializer.Serialize(capture.Latest);
        Assert.DoesNotContain(PrivateText, safe, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_REQUEST", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("path", safe, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ProcessId", safe, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(capture.Latest.BytesRetained, 0, 4096);
    }

    private static FileUseDiagnosticFrame[] EmptyObservation() =>
    [
        Phase(1, FileUsePhase.HELPER_STARTED),
        Phase(2, FileUsePhase.INPUT_RECEIVED),
        Phase(3, FileUsePhase.INPUT_VALIDATED),
        Phase(4, FileUsePhase.RM_START_BEGIN),
        Phase(5, FileUsePhase.RM_START_RETURNED),
        Phase(6, FileUsePhase.REGISTER_BEGIN),
        Phase(7, FileUsePhase.REGISTER_RETURNED),
        Phase(8, FileUsePhase.QUERY_BEGIN, 1),
        Phase(9, FileUsePhase.QUERY_RETURNED, 1),
        Phase(10, FileUsePhase.END_BEGIN),
        Phase(11, FileUsePhase.END_RETURNED),
        Phase(12, FileUsePhase.RESULT_READY),
        new() { Kind = FileUseFrameKind.RESULT, Sequence = 13, ElapsedMilliseconds = 13, Snapshot = EmptySnapshot() },
    ];

    private static FileUseSnapshot EmptySnapshot() => new()
    {
        State = "NO_HOLDER_OR_INCOMPLETE", StartCode = 0, RegisterCode = 0,
        QueryCode = 0, EndCode = 0, QueryCalls = 1, Incomplete = true,
    };

    private static FileUseDiagnosticFrame Phase(int sequence, FileUsePhase phase, int? ordinal = null) => new()
    {
        Kind = FileUseFrameKind.PHASE, Sequence = sequence, ElapsedMilliseconds = sequence,
        Phase = phase, QueryOrdinal = ordinal,
    };

    private static byte[] Encode(FileUseDiagnosticFrame[] frames) => Encoding.UTF8.GetBytes(string.Concat(
        frames.Select(frame => JsonSerializer.Serialize(frame, FileUseDiagnosticProtocol.JsonOptions) + "\n")));
}
