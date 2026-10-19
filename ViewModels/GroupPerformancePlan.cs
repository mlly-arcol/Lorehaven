namespace NativeTavern.ViewModels;

/// <summary>Tracks a finite group performance; every member speaks once per round.</summary>
public sealed class GroupPerformancePlan
{
    public GroupPerformancePlan(int rounds, IReadOnlyList<long> speakerOrder)
    {
        if (rounds is < 1 or > 20)
            throw new ArgumentOutOfRangeException(nameof(rounds), "Rounds must be between 1 and 20.");
        ArgumentNullException.ThrowIfNull(speakerOrder);
        if (speakerOrder.Count == 0)
            throw new ArgumentException("At least one speaker is required.", nameof(speakerOrder));
        if (speakerOrder.Any(id => id <= 0) || speakerOrder.Distinct().Count() != speakerOrder.Count)
            throw new ArgumentException("Speaker IDs must be positive and distinct.", nameof(speakerOrder));

        Rounds = rounds;
        SpeakerOrder = Array.AsReadOnly(speakerOrder.ToArray());
        TotalTurns = checked(rounds * SpeakerOrder.Count);
    }

    public int Rounds { get; }
    public IReadOnlyList<long> SpeakerOrder { get; }
    public int CompletedTurns { get; private set; }
    public int TotalTurns { get; }
    public bool IsComplete => CompletedTurns >= TotalTurns;
    public long? NextSpeakerId => IsComplete ? null : SpeakerOrder[CompletedTurns % SpeakerOrder.Count];
    public int CurrentRound => Math.Min(Rounds, CompletedTurns / SpeakerOrder.Count + 1);

    /// <summary>Advance after a speaker's reply has been saved successfully.</summary>
    public void Advance()
    {
        if (!IsComplete) CompletedTurns++;
    }
}
