﻿using System;
using System.Collections.Generic;

using DSODecompiler.ControlFlow;
using DSODecompiler.ControlFlow.Structure;
using DSODecompiler.ControlFlow.Structure.Regions;
using DSODecompiler.Disassembler;
using DSODecompiler.Loader;

namespace DSODecompiler
{
	class Program
	{
		static void Main (string[] args)
		{
			var loader = new FileLoader();
			var fileData = loader.LoadFile("test.cs.dso", 210);
			var disassembler = new Disassembler.Disassembler();
			var disassembly = disassembler.Disassemble(fileData);

			/*foreach (var instruction in disassembly)
			{
				Console.WriteLine("{0,8}    {1}", instruction.Addr, instruction);
			}*/

			/*Console.WriteLine($"\n==== Branches: {disassembly.NumBranches} ====\n");

			foreach (var branch in disassembly.GetBranches())
			{
				Console.Write(branch);

				if (disassembly.HasInstruction(branch.Source))
				{
					var insn = disassembly.GetInstruction(branch.Source) as BranchInstruction;

					Console.WriteLine($" {insn.IsUnconditional}");
				}
			}*/

			var cfgs = new ControlFlowGraphBuilder().Build(disassembly);

			foreach (var cfg in cfgs)
			{
				if (cfg.IsFunction)
				{
					Console.WriteLine(cfg.FunctionHeader);
				}
				else
				{
					Console.WriteLine($"\nBLOCK at {cfg.EntryPoint.Addr}:");

					/*foreach (var node in cfg.PreorderDFS())
					{
						foreach (var instruction in node.Instructions)
						{
							Console.WriteLine($"    {instruction}");
							break;
						}

						break;
					}*/

					Console.Write("\n");
				}

				var domGraph = new DominatorGraph<uint, ControlFlowNode>(cfg);

				foreach (var node in cfg)
				{
					var idom = domGraph.ImmediateDom(node);

					if (idom != null)
					{
						Console.WriteLine($"{domGraph.ImmediateDom(node).Addr} Dom {node.Addr}");
					}
				}
			}

			Console.WriteLine("\n==== Structure Analysis ====\n");

			foreach (var cfg in cfgs)
			{
				var analyzer = new StructureAnalyzer();
				var virtualRegion = analyzer.Analyze(cfg, disassembly);

				PrintVRegion(virtualRegion, 0);
			}
		}

		private static void PrintVRegion (List<VirtualRegion> list, int indent)
		{
			foreach (var vr in list)
			{
				PrintVRegion(vr, indent);
			}
		}

		private static void PrintVRegion (VirtualRegion vr, int indent = 0)
		{
			if (vr == null)
			{
				return;
			}

			vr.Instructions.ForEach(instruction =>
			{
				for (var i = 0; i < indent; i++)
				{
					Console.Write("\t");
				}

				Console.WriteLine(instruction);
			});

			PrintIndent(indent);
			Console.WriteLine(vr);

			switch (vr)
			{
				case ConditionalRegion cond:
				{
					PrintVRegion(cond.Then, indent + 1);
					PrintVRegion(cond.Else, indent + 1);
					break;
				}

				case SequenceRegion seq:
				{
					seq.Body.ForEach(child => PrintVRegion(child, indent + 1));
					break;
				}

				case LoopRegion loop:
				{
					PrintVRegion(loop.Body, indent + 1);
					break;
				}
			}

			Console.WriteLine("");
		}

		private static void PrintIndent (int indent)
		{
			for (var i = 0; i < indent; i++)
			{
				Console.Write("\t");
			}
		}
	}
}
