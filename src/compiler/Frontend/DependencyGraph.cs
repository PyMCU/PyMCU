/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 *
 * -----------------------------------------------------------------------------
 * SAFETY WARNING / HIGH RISK ACTIVITIES:
 * THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
 * ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
 * NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
 * TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
 * -----------------------------------------------------------------------------
 */

using PyMCU.Common;

namespace PyMCU.Frontend;

public class DependencyGraph
{
    private readonly HashSet<ProgramNode> _nodes = [];
    private readonly Dictionary<ProgramNode, List<ProgramNode>> _adjacencyList = new();
    private readonly Dictionary<ProgramNode, int> _inDegrees = new();

    public void AddNode(ProgramNode node)
    {
        if (!_nodes.Add(node)) return;
        _adjacencyList[node] = [];
        _inDegrees[node] = 0;
    }

    public void AddDependencyEdge(ProgramNode dependency, ProgramNode dependent)
    {
        AddNode(dependency);
        AddNode(dependent);

        _adjacencyList[dependency].Add(dependent);
        _inDegrees[dependent]++;
    }

    public List<ProgramNode> GetTopologicalSort()
    {
        var result = new List<ProgramNode>();
        var queue = new Queue<ProgramNode>();

        foreach (var node in _nodes)
        {
            if (_inDegrees[node] == 0) queue.Enqueue(node);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            result.Add(current);

            foreach (var neighbor in _adjacencyList[current])
            {
                _inDegrees[neighbor]--;
                if (_inDegrees[neighbor] == 0)
                {
                    queue.Enqueue(neighbor);
                }
            }
        }

        return result.Count != _nodes.Count
            ? throw new CompilerError("SemanticError", "Cyclic dependency detected. Cannot build compilation tree.", 0,
                0)
            : result;
    }
}