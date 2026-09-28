namespace ArchLens.Core.Scoring;

// Tarjan's algorithm: finds groups of nodes where every node can reach every other
// (strongly connected components) in one depth-first pass. A group bigger than one is a cycle.
public static class Tarjan
{
    public static List<List<string>> StronglyConnected(Dictionary<string, HashSet<string>> edges)
    {
        var index = 0;
        var indexOf = new Dictionary<string, int>();
        // The smallest index reachable from this node while it's still on the stack.
        var lowLink = new Dictionary<string, int>();
        var stack = new Stack<string>();
        var onStack = new HashSet<string>();
        var result = new List<List<string>>();

        void Visit(string node)
        {
            indexOf[node] = lowLink[node] = index++;
            stack.Push(node);
            onStack.Add(node);

            foreach (var next in edges.GetValueOrDefault(node) ?? new HashSet<string>())
            {
                if (!indexOf.ContainsKey(next))
                {
                    Visit(next);
                    lowLink[node] = Math.Min(lowLink[node], lowLink[next]);
                }
                // "next" is on the stack, so it's part of the loop we're currently inside.
                else if (onStack.Contains(next))
                {
                    lowLink[node] = Math.Min(lowLink[node], indexOf[next]);
                }
            }

            // Nothing below this node reaches higher up: it closes a component. Pop it off.
            if (lowLink[node] == indexOf[node])
            {
                var component = new List<string>();
                string popped;
                do
                {
                    popped = stack.Pop();
                    onStack.Remove(popped);
                    component.Add(popped);
                } while (popped != node);
                component.Sort(StringComparer.Ordinal);
                result.Add(component);
            }
        }

        var nodes = edges.Keys.Concat(edges.Values.SelectMany(v => v)).Distinct().OrderBy(n => n, StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (!indexOf.ContainsKey(node))
                Visit(node);
        }
        return result;
    }
}
