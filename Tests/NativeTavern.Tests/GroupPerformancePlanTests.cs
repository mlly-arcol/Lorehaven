using NativeTavern.ViewModels;
using Xunit;

namespace NativeTavern.Tests;

public sealed class GroupPerformancePlanTests
{
    [Fact]
    public void EverySpeakerGetsOneTurnPerRoundInConfiguredOrder()
    {
        var plan = new GroupPerformancePlan(3, [17, 4, 82]);
        Assert.Equal(9, plan.TotalTurns);

        for (var round = 1; round <= 3; round++)
        {
            foreach (var speakerId in new long[] { 17, 4, 82 })
            {
                Assert.False(plan.IsComplete);
                Assert.Equal(round, plan.CurrentRound);
                Assert.Equal(speakerId, plan.NextSpeakerId);
                plan.Advance();
            }
        }

        Assert.True(plan.IsComplete);
        Assert.Null(plan.NextSpeakerId);
        Assert.Equal(9, plan.CompletedTurns);
        Assert.Equal(3, plan.CurrentRound);
        plan.Advance();
        Assert.Equal(9, plan.CompletedTurns);
        Assert.Null(plan.NextSpeakerId);
    }

    [Fact]
    public void SpeakerOrderIsSnapshotOfTheRequestedOrder()
    {
        var requestedOrder = new List<long> { 8, 3 };
        var plan = new GroupPerformancePlan(1, requestedOrder);
        requestedOrder[0] = 19;
        requestedOrder.Add(22);

        Assert.Equal(new long[] { 8, 3 }, plan.SpeakerOrder);
        Assert.Equal(8, plan.NextSpeakerId);
        Assert.Equal(2, plan.TotalTurns);
        Assert.Throws<NotSupportedException>(() => ((IList<long>)plan.SpeakerOrder)[0] = 9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(21)]
    public void RejectsRoundsOutsideTheAllowedRange(int rounds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GroupPerformancePlan(rounds, [1, 2]));
    }

    [Theory]
    [InlineData(new long[0])]
    [InlineData(new long[] { 1, 1 })]
    [InlineData(new long[] { 0, 2 })]
    [InlineData(new long[] { -3, 2 })]
    public void RejectsEmptyDuplicateOrInvalidSpeakers(long[] speakerOrder)
    {
        Assert.Throws<ArgumentException>(() => new GroupPerformancePlan(1, speakerOrder));
    }

    [Fact]
    public void RejectsMissingSpeakerOrder()
    {
        Assert.Throws<ArgumentNullException>(() => new GroupPerformancePlan(1, null!));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    public void AcceptsRoundBoundariesAndCompletesSingleSpeaker(int rounds)
    {
        var plan = new GroupPerformancePlan(rounds, [7]);
        for (var index = 0; index < rounds; index++)
        {
            Assert.Equal(index, plan.CompletedTurns);
            Assert.Equal(index + 1, plan.CurrentRound);
            Assert.Equal(7, plan.NextSpeakerId);
            plan.Advance();
        }
        Assert.True(plan.IsComplete);
        Assert.Equal(rounds, plan.CompletedTurns);
    }
}
