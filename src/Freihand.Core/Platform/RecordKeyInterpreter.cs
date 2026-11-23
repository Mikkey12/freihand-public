using Freihand.Core.Profiles;

namespace Freihand.Core.Platform;

/// <summary>Aufnahmetaste: Ein/Aus (BE-03); Halten (BE-02) nur nach bestandener Messung (Kapitel 5.2).</summary>
public sealed class RecordKeyInterpreter(RecordingMode mode, bool holdApproved)
{
    private readonly bool _hold = mode == RecordingMode.Hold && holdApproved;

    /// <summary>Halten gewählt, aber nicht freigegeben: wirkt als Ein/Aus.</summary>
    public bool HoldUnavailable { get; } = mode == RecordingMode.Hold && !holdApproved;

    public RecordCommand? OnPressed(bool recording) =>
        _hold ? recording ? null : RecordCommand.Start
            : recording ? RecordCommand.Stop : RecordCommand.Start;

    public RecordCommand? OnReleased(bool recording) => _hold && recording ? RecordCommand.Stop : null;
}
