﻿using System;
using System.Linq;
using System.Collections.Generic;

using DSODecompiler.ControlFlow.Structure.Regions;
using DSODecompiler.Disassembler;

namespace DSODecompiler.ControlFlow.Structure
{
	/// <summary>
	/// Performs a structural analysis on a control flow graph in order to determine various structures
	/// in the code, such as loops, if-else statements, etc.<br/><br/>
	///
	/// <b>Sources:</b><br/>
	/// <list type="number">
	/// <item>
	/// <see href="https://www.usenix.org/system/files/conference/usenixsecurity13/sec13-paper_schwartz.pdf">
	/// "Native x86 Decompilation Using Semantics-Preserving Structural Analysis and Iterative
	/// Control-Flow Structuring"</see> by Edward J. Schwartz, JongHyup Lee, Maverick Woo, and David Brumley.
	/// </item>
	/// <item><see href="https://github.com/uxmal/reko">Reko Decompiler</see> by John Källén, et al.</item>
	/// </list>
	/// </summary>
	public class StructureAnalyzer
	{
		public class StructuralAnalysisException : Exception
		{
			public StructuralAnalysisException () {}
			public StructuralAnalysisException (string message) : base(message) {}
			public StructuralAnalysisException (string message, Exception inner) : base(message, inner) {}
		}

		protected Disassembly disassembly = null;
		protected RegionGraph regionGraph = null;
		protected DominatorGraph<uint, RegionGraphNode> domGraph = null;
		protected Dictionary<uint, VirtualRegion> virtualRegions = null;
		protected Queue<Loop<RegionGraphNode>> unreducedLoops;

		public VirtualRegion Analyze (ControlFlowGraph cfg, Disassembly disasm)
		{
			disassembly = disasm;
			regionGraph = RegionGraph.From(cfg);
			domGraph = new(regionGraph);
			virtualRegions = new();
			unreducedLoops = new();

			var entryPoint = regionGraph.EntryPoint;

			/* Silly edge case where there's one graph node that isn't a loop or branch or anything. */
			if (regionGraph.Count == 1)
			{
				AddVirtualRegion(entryPoint.Addr, new InstructionRegion(entryPoint.Region));
			}

			do
			{
				var oldCount = regionGraph.Count;

				foreach (var node in regionGraph.PostorderDFS())
				{
					ReduceNode(node);
				}

				// If we couldn't reduce anything, let's try some refinement.
				if (regionGraph.Count == oldCount && regionGraph.Count > 1)
				{
					RefineUnreducedRegions();
				}
			}
			while (regionGraph.Count > 1);

			return GetVirtualRegion(entryPoint.Addr);
		}

		/* TODO: Gotos, breaks, tail regions, etc. */
		protected void ReduceNode (RegionGraphNode node)
		{
			var reduced = true;

			while (reduced)
			{
				if (node.Successors.Count > 2)
				{
					throw new NotImplementedException($"Region graph node with more than 2 successors");
				}

				reduced = !IsCycleEnd(node) && ReduceAcyclic(node);

				if (!reduced && IsCycleStart(node))
				{
					reduced = ReduceCyclic(node);
				}
			}
		}

		protected bool IsCycleStart (RegionGraphNode node)
		{
			return node.Predecessors.Any(predecessor => domGraph.Dominates(node, predecessor as RegionGraphNode, strictly: false));
		}

		protected bool IsCycleEnd (RegionGraphNode node)
		{
			return node.Successors.Any(successor => domGraph.Dominates(successor as RegionGraphNode, node, strictly: false));
		}

		// TODO: Test expression inversion and comparison.
		protected bool ReduceCyclic (RegionGraphNode node)
		{
			foreach (RegionGraphNode successor in node.Successors.ToArray())
			{
				var isSelfLoop = successor == node;

				if (isSelfLoop || (successor.FirstSuccessor == node && successor.Predecessors.Count == 1))
				{
					if (successor.Region.LastInstruction is not BranchInstruction branch)
					{
						throw new NotImplementedException($"Cyclic region at {node.Addr} does not end with branch instruction.");
					}

					var loop = new LoopRegion()
					{
						Infinite = successor.Successors.Count == 1 || branch.IsUnconditional
					};

					if (HasVirtualRegion(node.Addr))
					{
						loop.Body.Add(GetVirtualRegion(node.Addr));
					}
					else if (isSelfLoop)
					{
						loop.Body.Add(new InstructionRegion(node.Region));
					}
					else
					{
						loop.CopyInstructions(node.Region);
					}

					if (!isSelfLoop && HasVirtualRegion(successor.Addr))
					{
						loop.Body.Add(GetVirtualRegion(successor.Addr));
					}

					AddVirtualRegion(node.Addr, loop);

					regionGraph.RemoveEdge(node, successor);
					regionGraph.RemoveEdge(successor, node);

					return true;
				}
			}

			unreducedLoops.Enqueue(domGraph.FindLoopNodes(node));

			return false;
		}

		protected bool ReduceAcyclic (RegionGraphNode node)
		{
			var reduced = false;

			switch (node.Successors.Count)
			{
				case 0:
				{
					break;
				}

				case 1:
				{
					reduced = ReduceSequence(node);
					break;
				}

				case 2:
				{
					reduced = ReduceConditional(node);
					break;
				}

				default:
				{
					throw new NotImplementedException($"Region graph node has {node.Successors.Count} successors");
				}
			}

			return reduced;
		}

		protected bool ReduceConditional (RegionGraphNode node)
		{
			var region = node.Region;
			var successors = regionGraph.GetSuccessors(node);

			if (successors.Count != 2)
			{
				return false;
			}

			var then = successors[0];
			var @else = successors[1];
			var thenSucc = regionGraph.FirstSuccessor(then);
			var elseSucc = regionGraph.FirstSuccessor(@else);

			var reduced = false;

			if (thenSucc == @else)
			{
				if (HasOtherPredecessors(then, node))
				{
					return false;
				}

				reduced = true;

				Console.WriteLine($"{node.Addr} :: if-then");

				if (!HasVirtualRegion(then.Addr))
				{
					AddVirtualRegion(then.Addr, new InstructionRegion(then.Region));
				}

				// TODO: Conditional inversion based on instruction.
				AddVirtualRegion(node.Addr, new ConditionalRegion(node.Region, GetVirtualRegion(then)));

				regionGraph.RemoveEdge(node, then);
				regionGraph.RemoveEdge(then, thenSucc);
				regionGraph.Remove(then);
			}
			else if (elseSucc != null && thenSucc == elseSucc)
			{
				if (HasOtherPredecessors(then, node) || HasOtherPredecessors(@else, node))
				{
					return false;
				}

				reduced = true;

				Console.WriteLine($"{node.Addr} :: if-then-else");

				if (!HasVirtualRegion(then.Addr))
				{
					AddVirtualRegion(then.Addr, new InstructionRegion(then.Region));
				}

				if (!HasVirtualRegion(@else.Addr))
				{
					AddVirtualRegion(@else.Addr, new InstructionRegion(@else.Region));
				}

				// TODO: Conditional inversion based on instruction.
				AddVirtualRegion(
					node.Addr,
					new ConditionalRegion(
						node.Region,
						GetVirtualRegion(then),
						GetVirtualRegion(@else)
					)
				);

				regionGraph.RemoveEdge(then, thenSucc);
				regionGraph.RemoveEdge(@else, elseSucc);
				regionGraph.RemoveEdge(node, then);
				regionGraph.RemoveEdge(node, @else);

				regionGraph.AddEdge(node, elseSucc);

				regionGraph.Remove(then);
				regionGraph.Remove(@else);
			}
			else
			{
				Console.WriteLine($"{node.Addr} :: <failed>    {node.Instructions[^1]}");
			}

			return reduced;
		}

		protected bool ReduceSequence (RegionGraphNode node)
		{
			if (node.Successors.Count != 1)
			{
				return false;
			}

			var next = regionGraph.FirstSuccessor(node);

			// Don't want to accidentally delete a jump target.
			if (next.Predecessors.Count > 1)
			{
				return false;
			}

			SequenceRegion sequence;

			if (node.Region.IsFunction)
			{
				sequence = new FunctionRegion(node.Region.FunctionHeader);
			}
			else
			{
				sequence = new SequenceRegion();
			}

			if (HasVirtualRegion(node.Addr))
			{
				sequence.Add(GetVirtualRegion(node));
			}
			else
			{
				sequence.CopyInstructions(node.Region);
			}

			if (!HasVirtualRegion(next.Addr))
			{
				if (IsCycleEnd(next))
				{
					AddVirtualRegion(next.Addr, new LoopFooterRegion(next.Region));
				}
				else
				{
					AddVirtualRegion(next.Addr, new InstructionRegion(next.Region));
				}
			}

			sequence.Add(GetVirtualRegion(next));

			AddVirtualRegion(node.Addr, sequence);

			regionGraph.RemoveEdge(node, next);
			regionGraph.ReplaceSuccessors(next, node);
			regionGraph.Remove(next);

			return true;
		}

		protected void ReduceTailSuccessors (RegionGraphNode node)
		{
			return;
			if (node.Successors.Count != 2)
			{
				return;
			}

			var successors = regionGraph.GetSuccessors(node);
			var then = successors[0];
			var @else = successors[1];

			// Next node is either not a tail or is a branch target.
			if (then.Successors.Count > 0 || then.Predecessors.Count > 1)
			{
				return;
			}

			if (!HasVirtualRegion(then.Addr))
			{
				AddVirtualRegion(then.Addr, new InstructionRegion(then.Region));
			}

			// TODO: Conditional inversion based on instruction.
			AddVirtualRegion(
				node.Addr,
				new ConditionalRegion(
					node.Region,
					GetVirtualRegion(then.Addr),
					new GotoRegion(@else.Addr)
				)
			);

			regionGraph.RemoveEdge(node, then);
			regionGraph.Remove(then);
		}

		protected bool HasOtherPredecessors (RegionGraphNode node, RegionGraphNode compare)
		{
			return node.Predecessors.Any(predecessor => predecessor != compare);
		}

		protected void RefineUnreducedRegions ()
		{
			var oldCount = regionGraph.Count;

			RefineUnreducedLoops();

			foreach (RegionGraphNode node in regionGraph.PostorderDFS())
			{
				ReduceTailSuccessors(node);
			}

			// If we still weren't able to reduce some regions, it's time for our last resort...
			if (regionGraph.Count == oldCount && regionGraph.Count > 1)
			{
				var reduced = false;

				foreach (RegionGraphNode node in regionGraph.PostorderDFS())
				{
					reduced = LastResort(node);

					if (reduced)
					{
						break;
					}
				}
			}
		}

		protected bool LastResort (RegionGraphNode node)
		{
			if (node.Successors.Count == 1)
			{
				var successor = regionGraph.FirstSuccessor(node);

				if (domGraph.Dominates(node, successor, strictly: true) || domGraph.Dominates(successor, node, strictly: true))
				{
					return false;
				}

				SequenceRegion sequence = new();

				if (HasVirtualRegion(node.Addr))
				{
					sequence.Add(GetVirtualRegion(node.Addr));
				}

				sequence.Add(new GotoRegion(successor.Addr));

				AddVirtualRegion(node.Addr, sequence);
				regionGraph.RemoveEdge(node, successor);

				return true;
			}
			else if (node.Successors.Count == 2)
			{
				var successors = regionGraph.GetSuccessors(node);
				var @else = successors[1];

				if (domGraph.Dominates(@else, node, strictly: true) || domGraph.Dominates(node, @else, strictly: true))
				{
					return false;
				}

				// TODO: Conditional inversion based on instruction.
				AddVirtualRegion(node.Addr, new ConditionalGotoRegion(node.Region, @else.Addr));
				regionGraph.RemoveEdge(node, @else);

				return true;
			}

			return false;
		}

		protected void RefineUnreducedLoops ()
		{
			while (unreducedLoops.Count > 0)
			{
				RefineLoop(unreducedLoops.Dequeue());
			}
		}

		protected void RefineLoop (Loop<RegionGraphNode> loop)
		{
			var head = EnsureSingleEntry(loop);
		}

		protected RegionGraphNode EnsureSingleEntry (Loop<RegionGraphNode> loop)
		{
			var entry = loop.Head;
			var maxEdges = CountIncomingEdges(entry, loop);

			foreach (var node in loop.Nodes)
			{
				var count = CountIncomingEdges(node, loop);

				if (count > maxEdges)
				{
					maxEdges = count;
					entry = node;
				}
			}

			/* Virtualize other incoming edges. */

			foreach (var node in loop.Nodes)
			{
				var nodes = FindIncomingNodes(node, loop);

				foreach (var incoming in nodes)
				{
					AddVirtualRegion(incoming.Addr, new GotoRegion(incoming.Addr));
					regionGraph.RemoveEdge(incoming, node);
				}
			}

			return entry;
		}

		protected int CountIncomingEdges (RegionGraphNode node, Loop<RegionGraphNode> loop) => FindIncomingNodes(node, loop).Count;

		protected HashSet<RegionGraphNode> FindIncomingNodes (RegionGraphNode node, Loop<RegionGraphNode> loop)
		{
			var edges = new HashSet<RegionGraphNode>();

			node.Predecessors.ForEach(predecessor =>
			{
				if (!loop.Nodes.Contains(predecessor))
				{
					edges.Add(predecessor as RegionGraphNode);
				}
			});

			return edges;
		}

		protected VirtualRegion AddVirtualRegion (uint addr, VirtualRegion vr) => virtualRegions[addr] = vr;
		protected bool HasVirtualRegion (uint addr) => virtualRegions.ContainsKey(addr);
		protected VirtualRegion GetVirtualRegion (uint addr) => HasVirtualRegion(addr) ? virtualRegions[addr] : null;
		protected VirtualRegion GetVirtualRegion (RegionGraphNode node) => GetVirtualRegion(node.Addr);
	}
}
