﻿/*
    Copyright (C) 2018 de4dot@gmail.com

    This file is part of Iced.

    Iced is free software: you can redistribute it and/or modify
    it under the terms of the GNU Lesser General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    Iced is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU Lesser General Public License for more details.

    You should have received a copy of the GNU Lesser General Public License
    along with Iced.  If not, see <https://www.gnu.org/licenses/>.
*/

#if !NO_ENCODER
using System;
using System.Diagnostics;

namespace Iced.Intel.BlockEncoderInternal {
	/// <summary>
	/// Instruction with a memory operand that is RIP/EIP relative
	/// </summary>
	sealed class IpRelMemOpInstr : Instr {
		Instruction instruction;
		InstrKind instrKind;
		readonly uint eipInstructionSize;
		readonly uint ripInstructionSize;
		TargetInstr targetInstr;

		enum InstrKind {
			Unchanged,
			Rip,
			Eip,
			Long,
			Uninitialized,
		}

		public IpRelMemOpInstr(BlockEncoder blockEncoder, ref Instruction instruction)
			: base(blockEncoder, instruction.IP64) {
			Debug.Assert(instruction.IsIPRelativeMemoryOp);
			this.instruction = instruction;
			instrKind = InstrKind.Uninitialized;

			string errorMessage;

			instruction.MemoryBase = Register.RIP;
			ripInstructionSize = (uint)blockEncoder.NullEncoder.Encode(ref instruction, instruction.IP64, out errorMessage);
			if (errorMessage != null)
				ripInstructionSize = DecoderConstants.MaxInstructionLength;

			instruction.MemoryBase = Register.EIP;
			eipInstructionSize = (uint)blockEncoder.NullEncoder.Encode(ref instruction, instruction.IP64, out errorMessage);
			if (errorMessage != null)
				eipInstructionSize = DecoderConstants.MaxInstructionLength;

			Debug.Assert(eipInstructionSize >= ripInstructionSize);
			Size = eipInstructionSize;
		}

		public override void Initialize() {
			targetInstr = blockEncoder.GetTarget(instruction.IPRelativeMemoryAddress);
			TryOptimize();
		}

		public override bool Optimize() => TryOptimize();

		bool TryOptimize() {
			if (instrKind == InstrKind.Unchanged || instrKind == InstrKind.Rip || instrKind == InstrKind.Eip)
				return false;

			// If it's in the same block, we assume the target is at most 2GB away.
			bool useRip = targetInstr.IsInBlock(Block);
			var targetAddress = targetInstr.GetAddress();
			if (!useRip) {
				var nextRip = IP + ripInstructionSize;
				long diff = (long)(targetAddress - nextRip);
				useRip = int.MinValue <= diff && diff <= int.MaxValue;
			}

			if (useRip) {
				Size = ripInstructionSize;
				instrKind = InstrKind.Rip;
				return true;
			}

			// If it's in the lower 4GB we can use EIP relative addressing
			if (targetAddress <= uint.MaxValue) {
				Size = eipInstructionSize;
				instrKind = InstrKind.Eip;
				return true;
			}

			instrKind = InstrKind.Long;
			return false;
		}

		public override string TryEncode(Encoder encoder, out ConstantOffsets constantOffsets, out bool isOriginalInstruction) {
			switch (instrKind) {
			case InstrKind.Unchanged:
			case InstrKind.Rip:
			case InstrKind.Eip:
				isOriginalInstruction = true;

				uint instrSize;
				if (instrKind == InstrKind.Rip) {
					instrSize = ripInstructionSize;
					instruction.MemoryBase = Register.RIP;
				}
				else if (instrKind == InstrKind.Eip) {
					instrSize = eipInstructionSize;
					instruction.MemoryBase = Register.EIP;
				}
				else {
					Debug.Assert(instrKind == InstrKind.Unchanged);
					instrSize = instruction.MemoryBase == Register.EIP ? eipInstructionSize : ripInstructionSize;
				}

				var targetAddress = targetInstr.GetAddress();
				var nextRip = IP + instrSize;
				instruction.NextIP64 = nextRip;
				instruction.MemoryDisplacement = (uint)targetAddress - (uint)nextRip;
				encoder.Encode(ref instruction, IP, out var errorMessage);
				bool b = instruction.IPRelativeMemoryAddress == (instruction.MemoryBase == Register.EIP ? (uint)targetAddress : targetAddress);
				Debug.Assert(b);
				if (!b)
					errorMessage = "Invalid IP relative address";
				if (errorMessage != null) {
					constantOffsets = default;
					return CreateErrorMessage(errorMessage, ref instruction);
				}
				constantOffsets = encoder.GetConstantOffsets();
				return null;

			case InstrKind.Long:
				isOriginalInstruction = false;
				constantOffsets = default;
				return "IP relative memory operand is too far away and isn't currently supported";

			case InstrKind.Uninitialized:
			default:
				throw new InvalidOperationException();
			}
		}
	}
}
#endif
