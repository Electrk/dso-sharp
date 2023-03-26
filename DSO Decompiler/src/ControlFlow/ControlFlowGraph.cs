﻿using System;
using System.Collections.Generic;

using DSODecompiler.Disassembler;
using DSODecompiler.Util;

namespace DSODecompiler.ControlFlow
{
	public class ControlFlowNode : GraphNode
	{
		public uint Addr { get; }
		public List<Instruction> Instructions { get; } = new();

		public Instruction FirstInstruction => Instructions.Count > 0 ? Instructions[0] : null;
		public Instruction LastInstruction => Instructions.Count > 0 ? Instructions[^1] : null;

		public ControlFlowNode (uint addr) => Addr = addr;
	}

	public class ControlFlowGraph : DirectedGraph<uint, ControlFlowNode>
	{
		public bool IsFunction => EntryPoint != null
			&& EntryPoint.Instructions.Count > 0
			&& EntryPoint.FirstInstruction is FunctionInstruction;

		public ControlFlowNode AddOrGet (uint addr) => Has(addr) ? Get(addr) : Add(addr, new ControlFlowNode(addr));
	}
}
