using System.Text.Json;
using AutoVpn.TestSupport;

namespace AutoVpn.UnitTests;

// These are received observations, never a claim about the child's exact position at a deadline.
internal sealed record FileUseProgressReport(string State, string ErrorKind, int Frames,
    long BytesObserved, int BytesRetained, bool Truncated, bool PartialLine, bool Eof,
    string? LastPhase, int? QueryOrdinal, long? HelperElapsedMilliseconds, long? ObservedMilliseconds);

internal sealed record FileUseAttemptReport(string ParentPhase, FileUseParentPhaseReport ParentAtAttemptEnd,
    string ExceptionKind, int HResult,
    long ElapsedMilliseconds, bool BudgetExpired, FileUseProgressReport HelperBeforeDeadline,
    FileUseProgressReport HelperAtAttemptEnd);

internal enum FileUseParentPhase
{
    NOT_STARTED, PROCESS_START, OUTPUT_CAPTURE, INPUT_CONTROL_WAIT,
    INPUT_WRITE, INPUT_FLUSH, INPUT_CLOSE, PROCESS_WAIT, OUTPUT_JOIN, RESULT_PARSE, COMPLETED,
}

internal sealed record FileUseParentPhaseReport(string Phase, long ObservedMilliseconds);

internal sealed class FileUseParentProgress(Func<long> elapsed)
{
    private readonly object _gate = new();
    private FileUseParentPhaseReport _latest = new("NOT_STARTED", 0);
    private FileUseParentPhaseReport _before = new("NOT_STARTED", 0);
    internal void Advance(FileUseParentPhase phase)
    {
        lock (_gate)
        {
            var time = Math.Max(0, elapsed());
            _latest = new(phase.ToString(), time);
            if (time < FileUseProgressCapture.BudgetMilliseconds) _before = _latest;
        }
    }
    internal FileUseParentPhaseReport Latest { get { lock (_gate) return _latest; } }
    internal FileUseParentPhaseReport BeforeDeadline { get { lock (_gate) return _before; } }
}

internal sealed class FileUseProgressCapture
{
    internal const long BudgetMilliseconds = 2000;
    private readonly object _gate = new();
    private readonly Func<long> _elapsed;
    private readonly byte[] _bytes = new byte[FileUseDiagnosticProtocol.MaximumOutputBytes];
    private readonly TaskCompletionSource _inputClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _kept, _lineStart, _frames;
    private long _total;
    private FileUsePhase? _phase;
    private int? _ordinal;
    private long? _helperElapsed, _observed;
    private string _error = "NONE";
    private bool _eof;
    private FileUseSnapshot? _result;
    private FileUseProgressReport _before;

    internal FileUseProgressCapture(Func<long> elapsed)
    {
        _elapsed = elapsed;
        _before = Current();
    }

    internal Task InputClosed => _inputClosed.Task;
    internal FileUseProgressReport BeforeDeadline { get { lock (_gate) return _before; } }
    internal FileUseProgressReport Latest { get { lock (_gate) return Current(); } }

    internal void Append(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_eof) { Invalid("AFTER_EOF"); RememberBeforeDeadline(); return; }
            _total = _total > long.MaxValue - data.Length ? long.MaxValue : _total + data.Length;
            var count = Math.Min(data.Length, _bytes.Length - _kept);
            data[..count].CopyTo(_bytes.AsSpan(_kept));
            _kept += count;
            for (var i = _lineStart; i < _kept; i++)
            {
                if (_bytes[i] != (byte)'\n') continue;
                if (_error == "NONE") Parse(_bytes.AsSpan(_lineStart, i - _lineStart));
                _lineStart = i + 1;
                RememberBeforeDeadline();
            }
            if (_total > _bytes.Length) Invalid("OUTPUT_LIMIT");
            RememberBeforeDeadline();
        }
    }

    internal void Finish()
    {
        lock (_gate)
        {
            _eof = true;
            if (_lineStart != _kept) Invalid("INCOMPLETE_FRAME");
            RememberBeforeDeadline();
        }
    }

    internal FileUseSnapshot RequireResult()
    {
        lock (_gate)
        {
            if (!_eof || _error != "NONE" || _result is null)
                throw new InvalidDataException("FILE_USE_PROTOCOL_INVALID");
            return _result;
        }
    }

    private void Parse(ReadOnlySpan<byte> line)
    {
        try
        {
            if (_result is not null) { Invalid("AFTER_RESULT"); return; }
            if (_frames >= FileUseDiagnosticProtocol.MaximumFrames) { Invalid("FRAME_LIMIT"); return; }
            // Disallow duplicate keys too: otherwise JSON's last value could hide an earlier field.
            using var json = JsonDocument.Parse(line.ToArray());
            if (!UniqueProperties(json.RootElement)) { Invalid("FRAME_JSON"); return; }
            var frame = JsonSerializer.Deserialize<FileUseDiagnosticFrame>(line, FileUseDiagnosticProtocol.JsonOptions);
            if (frame is null || frame.Sequence != _frames + 1 || frame.ElapsedMilliseconds < 0 ||
                frame.ElapsedMilliseconds > 60000 || frame.ElapsedMilliseconds < (_helperElapsed ?? 0))
            { Invalid("FRAME_SEQUENCE"); return; }
            if (frame.Kind == FileUseFrameKind.RESULT)
            {
                if (_phase != FileUsePhase.RESULT_READY || frame.Phase is not null || frame.QueryOrdinal is not null ||
                    frame.Snapshot is null || !ValidSnapshot(frame.Snapshot))
                { Invalid("FRAME_SHAPE"); return; }
                _result = frame.Snapshot;
            }
            else
            {
                if (frame.Phase is null || frame.Snapshot is not null || !Next(frame.Phase.Value, frame.QueryOrdinal))
                { Invalid("FRAME_ORDER"); return; }
                _phase = frame.Phase;
                _ordinal = frame.QueryOrdinal;
                if (_phase == FileUsePhase.INPUT_CLOSED) _inputClosed.TrySetResult();
            }
            _frames++;
            _helperElapsed = frame.ElapsedMilliseconds;
            _observed = Math.Max(0, _elapsed());
        }
        catch (JsonException) { Invalid("FRAME_JSON"); }
        catch (NotSupportedException) { Invalid("FRAME_JSON"); }
    }

    private bool Next(FileUsePhase next, int? ordinal)
    {
        if (next is not (FileUsePhase.QUERY_BEGIN or FileUsePhase.QUERY_RETURNED) && ordinal is not null) return false;
        if ((next is FileUsePhase.QUERY_BEGIN or FileUsePhase.QUERY_RETURNED) && ordinal is not (1 or 2)) return false;
        if (_phase is null) return next == FileUsePhase.HELPER_STARTED;
        if (next == FileUsePhase.RESULT_READY) return _phase != FileUsePhase.RESULT_READY;
        if (next == FileUsePhase.END_BEGIN)
            return _phase is FileUsePhase.RM_START_RETURNED or FileUsePhase.REGISTER_BEGIN or
                FileUsePhase.REGISTER_RETURNED or FileUsePhase.QUERY_BEGIN or FileUsePhase.QUERY_RETURNED;
        return (_phase, next) switch
        {
            (FileUsePhase.HELPER_STARTED, FileUsePhase.INPUT_RECEIVED or FileUsePhase.INPUT_CLOSED) => true,
            (FileUsePhase.INPUT_RECEIVED, FileUsePhase.INPUT_VALIDATED) => true,
            (FileUsePhase.INPUT_VALIDATED, FileUsePhase.RM_START_BEGIN) => true,
            (FileUsePhase.RM_START_BEGIN, FileUsePhase.RM_START_RETURNED) => true,
            (FileUsePhase.RM_START_RETURNED, FileUsePhase.REGISTER_BEGIN) => true,
            (FileUsePhase.REGISTER_BEGIN, FileUsePhase.REGISTER_RETURNED) => true,
            (FileUsePhase.REGISTER_RETURNED, FileUsePhase.QUERY_BEGIN) => ordinal == 1,
            (FileUsePhase.QUERY_BEGIN, FileUsePhase.QUERY_RETURNED) => ordinal == _ordinal,
            (FileUsePhase.QUERY_RETURNED, FileUsePhase.QUERY_BEGIN) => _ordinal == 1 && ordinal == 2,
            (FileUsePhase.END_BEGIN, FileUsePhase.END_RETURNED) => true,
            _ => false,
        };
    }

    private static bool ValidSnapshot(FileUseSnapshot value) =>
        (value.State is "OBSERVED" or "NO_HOLDER_OR_INCOMPLETE" or "QUERY_INCOMPLETE" or "HELPER_ERROR" or "TIMEOUT_CONTROL_FINISHED") &&
        value.QueryCalls is >= 0 and <= 2 && value.TotalOwners is >= 0 and <= 32 &&
        value.TestHostOwners >= 0 && value.OwnedChildOwners >= 0 && value.OtherOwners >= 0 &&
        (long)value.TestHostOwners + value.OwnedChildOwners + value.OtherOwners == value.TotalOwners &&
        (value.TotalOwners != 0 || value.Incomplete);

    private static bool UniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!names.Add(property.Name) || !UniqueProperties(property.Value)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) if (!UniqueProperties(child)) return false;
        return true;
    }

    private void Invalid(string kind) { if (_error == "NONE") _error = kind; }
    private void RememberBeforeDeadline()
    {
        // Read time at every update. A late timer callback cannot admit late frames.
        if (_elapsed() < BudgetMilliseconds) _before = Current();
    }
    private FileUseProgressReport Current() => new(
        _error != "NONE" ? "INVALID" : _result is not null ? "RESULT" : _frames == 0 ? "NO_FRAME" : "PREFIX",
        _error, _frames, _total, _kept, _total > _bytes.Length, _lineStart != _kept, _eof,
        _phase?.ToString(), _ordinal, _helperElapsed, _observed);
}
