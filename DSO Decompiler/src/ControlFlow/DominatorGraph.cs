﻿using System;
using System.Collections.Generic;

using DSODecompiler.Util;

namespace DSODecompiler.ControlFlow
{
	/// <summary>
	/// Calculates the immediate dominators of nodes in a control flow graph.<br /><br />
	///
	/// A node D is said to dominate a node N if all paths from the entry point must go through D
	/// to get to N.<br /><br />
	///
	/// Nodes can have multiple dominators, but we only care about the immediate dominator, which
	/// is the very last dominator before the node itself.
	/// </summary>
	public class DominatorGraph<K, N> where N : GraphNode
	{
		public class DominatorGraphException : Exception
		{
			public DominatorGraphException () {}
			public DominatorGraphException (string message) : base(message) {}
			public DominatorGraphException (string message, Exception inner) : base(message, inner) {}
		}

		private readonly DirectedGraph<K, N> digraph = null;

		private readonly Dictionary<N, int> reversePostorder = new();
		private readonly Dictionary<N, N> immediateDoms = new();

		public DominatorGraph (DirectedGraph<K, N> graph)
		{
			digraph = graph;

			Build();
		}

		public N ImmediateDom (N node)
		{
			return immediateDoms.ContainsKey(node) ? immediateDoms[node] : null;
		}

		/// <summary>
		/// Checks if `node1` dominates `node2`.
		/// </summary>
		/// <param name="node1"></param>
		/// <param name="node2"></param>
		/// <param name="strictly">
		/// Whether we're checking for strict domination (i.e. A node cannot strictly dominate itself).
		/// </param>
		/// <returns></returns>
		public bool Dominates (N node1, N node2, bool strictly = false)
		{
			// All nodes dominate themselves, but not strictly.
			if (node1 == node2)
			{
				return !strictly;
			}

			var dom = ImmediateDom(node2);

			while (dom != node1 && dom != null && dom != ImmediateDom(dom))
			{
				dom = ImmediateDom(dom);
			}

			return dom == node1;
		}

		/// <summary>
		/// Calculates the immediate dominator of every node in `cfg`.<br /><br />
		///
		/// "A Simple, Fast Dominance Algorithm" by Keith Cooper, Timothy Harvey, and Ken Kennedy:<br />
		/// https://www.cs.rice.edu/~keith/EMBED/dom.pdf
		/// </summary>
		private void Build ()
		{
			BuildReversePostorder();

			var entry = digraph.EntryPoint;
			var nodes = new SortedList<int, N>();

			foreach (var (node, order) in reversePostorder)
			{
				nodes.Add(order, node);
			}

			// Temporarily set this so the algorithm works.
			immediateDoms[entry] = entry;

			var changed = true;

			while (changed)
			{
				changed = false;

				foreach (var node in nodes.Values)
				{
					if (node == entry)
					{
						continue;
					}

					N newIDom = null;

					foreach (N pred in node.Predecessors)
					{
						if (immediateDoms.ContainsKey(pred))
						{
							if (newIDom == null)
							{
								newIDom = pred;
							}
							else
							{
								newIDom = FindCommonDominator(pred, newIDom);
							}
						}
					}

					if (newIDom == null)
					{
						throw new DominatorGraphException($"Could not find new immediate dominator for node at {node}");
					}

					if (ImmediateDom(node) != newIDom)
					{
						immediateDoms[node] = newIDom;
						changed = true;
					}
				}
			}

			// Set immediate dominator back to `null` because a node cannot be the immediate dominator
			// of itself.
			immediateDoms[entry] = null;
		}

		/// <summary>
		/// "A Simple, Fast Dominance Algorithm" by Keith Cooper, Timothy Harvey, and Ken Kennedy:<br />
		/// https://www.cs.rice.edu/~keith/EMBED/dom.pdf
		/// </summary>
		/// <param name="node1"></param>
		/// <param name="node2"></param>
		/// <returns></returns>
		private N FindCommonDominator (N node1, N node2)
		{
			var finger1 = node1;
			var finger2 = node2;

			while (finger1 != finger2)
			{
				while (reversePostorder[finger1] > reversePostorder[finger2])
				{
					finger1 = ImmediateDom(finger1);
				}

				while (reversePostorder[finger2] > reversePostorder[finger1])
				{
					finger2 = ImmediateDom(finger2);
				}
			}

			return finger1;
		}

		private void BuildReversePostorder ()
		{
			var order = digraph.Count - 1;

			foreach (var node in digraph.PostorderDFS())
			{
				reversePostorder[node] = order--;
			}
		}
	}
}