using NativeTavern.Models;
using NativeTavern.Services;
using Xunit;

namespace NativeTavern.Tests;

public sealed class ConversationBranchTreeTests
{
    [Fact]
    public void BuildIncludesAncestorsSiblingsAndDescendantsOfCurrentRoute()
    {
        var sessions = new[] { Session(1), Session(2, 1), Session(3, 1), Session(4, 2), Session(5, 4) };

        var root = Assert.Single(ConversationBranchTree.Build(sessions, 4));

        Assert.Equal(1, root.Session.Id);
        Assert.Equal(new long[] { 2, 3 }, root.Children.Select(x => x.Session.Id));
        var current = Assert.Single(root.Children[0].Children);
        Assert.True(current.IsCurrent);
        Assert.Equal(5, Assert.Single(current.Children).Session.Id);
        Assert.Single(Flatten(root), x => x.IsCurrent);
    }

    [Fact]
    public void BuildExcludesUnrelatedStoriesAndOrdersBranchesByCreationThenId()
    {
        var earlier = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var sessions = new[]
        {
            Session(1), Session(4, 1, earlier.AddMinutes(1)), Session(3, 1, earlier),
            Session(2, 1, earlier), Session(20), Session(21, 20)
        };

        var root = Assert.Single(ConversationBranchTree.Build(sessions, 4));

        Assert.Equal(new long[] { 2, 3, 4 }, root.Children.Select(x => x.Session.Id));
        Assert.Equal(new long[] { 1, 2, 3, 4 }, Flatten(root).Select(x => x.Session.Id).Order());
    }

    [Fact]
    public void MissingParentLeavesAnOpenableRootAndItsDescendants()
    {
        var sessions = new[] { Session(2, 99), Session(3, 2), Session(4) };

        var root = Assert.Single(ConversationBranchTree.Build(sessions, 3));

        Assert.Equal(2, root.Session.Id);
        Assert.True(root.HasMissingParent);
        Assert.False(root.IsCycleRoot);
        Assert.True(Assert.Single(root.Children).IsCurrent);
    }

    [Fact]
    public void SurvivingSiblingsOfADeletedParentRemainInTheSameStory()
    {
        var sessions = new[] { Session(2, 99), Session(3, 2), Session(4, 99), Session(5, 4), Session(6, 100) };

        var roots = ConversationBranchTree.Build(sessions, 3);

        Assert.Equal(new long[] { 2, 4 }, roots.Select(x => x.Session.Id));
        Assert.All(roots, x => Assert.True(x.HasMissingParent));
        Assert.Equal(5, Assert.Single(roots[1].Children).Session.Id);
    }

    [Fact]
    public void CircularParentsAppearOnceFromTheSameStableRoot()
    {
        var sessions = new[] { Session(3, 2), Session(2, 1), Session(1, 3), Session(4, 2), Session(5, 4) };

        var root = Assert.Single(ConversationBranchTree.Build(sessions, 5));
        var otherRoot = Assert.Single(ConversationBranchTree.Build(sessions.Reverse().ToArray(), 3));

        Assert.Equal(1, root.Session.Id);
        Assert.Equal(root.Session.Id, otherRoot.Session.Id);
        Assert.True(root.IsCycleRoot);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, Flatten(root).Select(x => x.Session.Id).Order());
        Assert.Equal(5, Flatten(root).Count());
    }

    [Fact]
    public void SelfParentAndDuplicateRowsDoNotCreateRepeatedNodes()
    {
        var first = Session(1, 1);
        var duplicate = Session(1, 2);

        var root = Assert.Single(ConversationBranchTree.Build([first, duplicate, Session(2)], 1));

        Assert.Same(first, root.Session);
        Assert.True(root.IsCycleRoot);
        Assert.Empty(root.Children);
    }

    [Fact]
    public void UnknownCurrentConversationHasNoTree() =>
        Assert.Empty(ConversationBranchTree.Build([Session(1)], 99));

    private static ChatSession Session(long id, long? parent = null, DateTimeOffset? created = null) => new()
    {
        Id = id,
        ParentSessionId = parent,
        Title = $"Route {id}",
        CreatedAt = created ?? DateTimeOffset.UnixEpoch,
        UpdatedAt = created ?? DateTimeOffset.UnixEpoch
    };

    private static IEnumerable<ConversationBranchNode> Flatten(ConversationBranchNode root)
    {
        var pending = new Queue<ConversationBranchNode>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var node))
        {
            yield return node;
            foreach (var child in node.Children) pending.Enqueue(child);
        }
    }
}
