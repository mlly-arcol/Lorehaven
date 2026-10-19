using NativeTavern.Models;

namespace NativeTavern.Services;

/// <summary>A conversation and the routes that branch from it.</summary>
public sealed class ConversationBranchNode
{
    private readonly List<ConversationBranchNode> _children = [];

    internal ConversationBranchNode(ChatSession session, long currentSessionId)
    {
        Session = session;
        IsCurrent = session.Id == currentSessionId;
    }

    public ChatSession Session { get; }
    public IReadOnlyList<ConversationBranchNode> Children => _children;
    public bool IsCurrent { get; }
    public bool HasMissingParent { get; internal set; }
    public bool IsCycleRoot { get; internal set; }

    internal void AddChild(ConversationBranchNode child) => _children.Add(child);
}

/// <summary>
/// Builds the current story's tree without modifying its persisted relationships.
/// Missing ancestors become visible roots; a circular parent chain is cut at its lowest ID.
/// </summary>
public static class ConversationBranchTree
{
    public static IReadOnlyList<ConversationBranchNode> Build(
        IReadOnlyList<ChatSession> sessions, long currentSessionId)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        // Repeated rows should not render the same route twice. Keep the first copy.
        var byId = new Dictionary<long, ChatSession>();
        foreach (var session in sessions) byId.TryAdd(session.Id, session);
        if (!byId.TryGetValue(currentSessionId, out var current)) return [];

        var ancestry = new List<ChatSession>();
        var ancestryPositions = new Dictionary<long, int>();
        var root = current;
        var cycleRoot = false;
        while (true)
        {
            if (ancestryPositions.TryGetValue(root.Id, out var cycleStart))
            {
                root = ancestry.Skip(cycleStart).MinBy(x => x.Id)!;
                cycleRoot = true;
                break;
            }

            ancestryPositions.Add(root.Id, ancestry.Count);
            ancestry.Add(root);
            if (root.ParentSessionId is not { } parentId || !byId.TryGetValue(parentId, out var parent))
                break;
            root = parent;
        }

        var childrenByParent = byId.Values
            .Where(x => x.ParentSessionId.HasValue)
            .GroupBy(x => x.ParentSessionId!.Value)
            .ToDictionary(x => x.Key, x => x.OrderBy(s => s.CreatedAt).ThenBy(s => s.Id).ToArray());
        // If an ancestor was deleted, its surviving children still belong to the same story.
        var missingParent = root.ParentSessionId is { } missingParentId && !byId.ContainsKey(missingParentId);
        ChatSession[] rootSessions = missingParent ? childrenByParent[root.ParentSessionId!.Value] : [root];
        var rootNodes = rootSessions.Select(session => new ConversationBranchNode(session, currentSessionId)
        {
            HasMissingParent = missingParent,
            IsCycleRoot = cycleRoot
        }).ToArray();
        var visited = new HashSet<long>(rootNodes.Select(x => x.Session.Id));
        var pending = new Queue<ConversationBranchNode>();
        foreach (var rootNode in rootNodes) pending.Enqueue(rootNode);
        while (pending.TryDequeue(out var parentNode))
        {
            if (!childrenByParent.TryGetValue(parentNode.Session.Id, out var children)) continue;
            foreach (var child in children)
            {
                if (!visited.Add(child.Id)) continue;
                var childNode = new ConversationBranchNode(child, currentSessionId);
                parentNode.AddChild(childNode);
                pending.Enqueue(childNode);
            }
        }
        return rootNodes;
    }
}
