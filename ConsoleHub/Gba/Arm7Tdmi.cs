namespace ConsoleHub.Gba;

/// <summary>Starter ARM/Thumb interpreter. Unimplemented instructions behave as NOPs.</summary>
internal sealed class Arm7Tdmi
{
    private readonly GbaMemory _memory;
    private readonly uint[] _r = new uint[16];
    private uint _cpsr;

    public Arm7Tdmi(GbaMemory memory) => _memory = memory;

    public void Reset()
    {
        Array.Clear(_r);
        _r[13] = 0x03007F00;
        _r[15] = 0x08000000;
        _cpsr = 0x1F;
    }

    public void WriteState(BinaryWriter writer)
    {
        foreach (var register in _r) writer.Write(register);
        writer.Write(_cpsr);
    }

    public void ReadState(BinaryReader reader)
    {
        for (var i = 0; i < _r.Length; i++) _r[i] = reader.ReadUInt32();
        _cpsr = reader.ReadUInt32();
    }

    public int Step()
    {
        var thumb = (_cpsr & (1u << 5)) != 0;
        if (thumb)
        {
            var instruction = _memory.Read16(_r[15]);
            _r[15] += 2;
            return ExecuteThumb(instruction);
        }

        var arm = _memory.Read32(_r[15]);
        _r[15] += 4;
        return ExecuteArm(arm);
    }

    private int ExecuteArm(uint instruction)
    {
        var condition = (int)(instruction >> 28);
        if (!ConditionPassed(condition)) return 1;

        if ((instruction & 0x0E000000) == 0x0A000000) // B / BL
        {
            if ((instruction & (1u << 24)) != 0) _r[14] = _r[15];
            var offset = SignExtend(instruction & 0x00FFFFFF, 24) << 2;
            _r[15] = unchecked(_r[15] + 4 + (uint)offset);
            return 3;
        }

        if ((instruction & 0x0FFFFFF0) == 0x012FFF10) // BX
        {
            var target = ReadReg((int)(instruction & 15));
            if ((target & 1) != 0) _cpsr |= 1u << 5;
            else _cpsr &= ~(1u << 5);
            _r[15] = target & ((target & 1) != 0 ? ~1u : ~3u);
            return 3;
        }

        if ((instruction & 0x0C000000) == 0x04000000) // Single data transfer
            return ExecuteArmTransfer(instruction);

        if ((instruction & 0x0FC000F0) == 0x00000090) // MUL / MLA
        {
            var rd = (int)((instruction >> 16) & 15);
            var rn = (int)((instruction >> 12) & 15);
            var rs = (int)((instruction >> 8) & 15);
            var rm = (int)(instruction & 15);
            var result = _r[rm] * _r[rs];
            if ((instruction & (1u << 21)) != 0) result += _r[rn];
            _r[rd] = result;
            if ((instruction & (1u << 20)) != 0) SetNz(result);
            return 2;
        }

        if ((instruction & 0x0C000000) == 0) // Data processing
            return ExecuteArmData(instruction);

        return 1;
    }

    private int ExecuteArmTransfer(uint instruction)
    {
        var registerOffset = (instruction & (1u << 25)) != 0;
        var preIndex = (instruction & (1u << 24)) != 0;
        var add = (instruction & (1u << 23)) != 0;
        var byteTransfer = (instruction & (1u << 22)) != 0;
        var writeBack = (instruction & (1u << 21)) != 0;
        var load = (instruction & (1u << 20)) != 0;
        var rn = (int)((instruction >> 16) & 15);
        var rd = (int)((instruction >> 12) & 15);
        var baseAddress = ReadReg(rn);
        var offset = registerOffset
            ? Shift(_r[(int)(instruction & 15)], (int)((instruction >> 7) & 31), (int)((instruction >> 5) & 3), false).Value
            : instruction & 0xFFF;
        var adjusted = add ? baseAddress + offset : baseAddress - offset;
        var address = preIndex ? adjusted : baseAddress;

        if (load)
        {
            _r[rd] = byteTransfer ? _memory.Read8(address) : _memory.Read32(address);
            if (rd == 15) _r[15] &= ~3u;
        }
        else if (byteTransfer)
        {
            _memory.Write8(address, (byte)_r[rd]);
        }
        else
        {
            _memory.Write32(address, ReadReg(rd));
        }

        if (!preIndex || writeBack) _r[rn] = adjusted;
        return load ? 3 : 2;
    }

    private int ExecuteArmData(uint instruction)
    {
        var immediate = (instruction & (1u << 25)) != 0;
        var opcode = (int)((instruction >> 21) & 15);
        var setFlags = (instruction & (1u << 20)) != 0;
        var rn = (int)((instruction >> 16) & 15);
        var rd = (int)((instruction >> 12) & 15);
        uint operand;
        bool shifterCarry;
        if (immediate)
        {
            var rotate = (int)((instruction >> 8) & 15) * 2;
            operand = RotateRight(instruction & 0xFF, rotate);
            shifterCarry = rotate == 0 ? Flag(29) : (operand & 0x80000000) != 0;
        }
        else
        {
            var rm = (int)(instruction & 15);
            var shiftAmount = (int)((instruction >> 7) & 31);
            var shiftType = (int)((instruction >> 5) & 3);
            var shifted = Shift(ReadReg(rm), shiftAmount, shiftType, Flag(29));
            operand = shifted.Value;
            shifterCarry = shifted.Carry;
        }

        var left = ReadReg(rn);
        uint result;
        var arithmetic = false;
        var carry = false;
        var overflow = false;
        switch (opcode)
        {
            case 0: result = left & operand; break; // AND
            case 1: result = left ^ operand; break; // EOR
            case 2: result = Sub(left, operand, false, out carry, out overflow); arithmetic = true; break;
            case 3: result = Sub(operand, left, false, out carry, out overflow); arithmetic = true; break;
            case 4: result = Add(left, operand, false, out carry, out overflow); arithmetic = true; break;
            case 5: result = Add(left, operand, Flag(29), out carry, out overflow); arithmetic = true; break;
            case 6: result = Sub(left, operand, !Flag(29), out carry, out overflow); arithmetic = true; break;
            case 7: result = Sub(operand, left, !Flag(29), out carry, out overflow); arithmetic = true; break;
            case 8: result = left & operand; setFlags = true; break; // TST
            case 9: result = left ^ operand; setFlags = true; break; // TEQ
            case 10: result = Sub(left, operand, false, out carry, out overflow); setFlags = true; arithmetic = true; break;
            case 11: result = Add(left, operand, false, out carry, out overflow); setFlags = true; arithmetic = true; break;
            case 12: result = left | operand; break; // ORR
            case 13: result = operand; break; // MOV
            case 14: result = left & ~operand; break; // BIC
            default: result = ~operand; break; // MVN
        }

        var testOnly = opcode is 8 or 9 or 10 or 11;
        if (!testOnly) _r[rd] = result;
        if (rd == 15 && !testOnly) _r[15] &= ~3u;
        if (setFlags)
        {
            SetNz(result);
            SetFlag(29, arithmetic ? carry : shifterCarry);
            if (arithmetic) SetFlag(28, overflow);
        }
        return 1;
    }

    private int ExecuteThumb(ushort instruction)
    {
        var op = instruction;
        if ((op & 0xE000) == 0x0000) // Immediate shifts and add/sub
        {
            if ((op & 0x1800) != 0x1800)
            {
                var amount = (op >> 6) & 31;
                var type = (op >> 11) & 3;
                var rd = op & 7;
                var result = Shift(_r[(op >> 3) & 7], amount, type, Flag(29));
                _r[rd] = result.Value;
                SetNz(result.Value);
                SetFlag(29, result.Carry);
                return 1;
            }

            var immediate = (op & 0x0400) != 0;
            var subtract = (op & 0x0200) != 0;
            var a = _r[(op >> 3) & 7];
            var b = immediate ? (uint)((op >> 6) & 7) : _r[(op >> 6) & 7];
            var dest = op & 7;
            uint value;
            if (subtract)
            {
                value = Sub(a, b, false, out var carry, out var overflow);
                SetFlag(29, carry);
                SetFlag(28, overflow);
            }
            else
            {
                value = Add(a, b, false, out var carry, out var overflow);
                SetFlag(29, carry);
                SetFlag(28, overflow);
            }
            _r[dest] = value;
            SetNz(value);
            return 1;
        }

        if ((op & 0xE000) == 0x2000) // MOV/CMP/ADD/SUB immediate
        {
            var opcode = (op >> 11) & 3;
            var rd = (op >> 8) & 7;
            var value = (uint)(op & 0xFF);
            if (opcode == 0) { _r[rd] = value; SetNz(value); }
            else if (opcode == 1) { value = Sub(_r[rd], value, false, out var c, out var v); SetNz(value); SetFlag(29, c); SetFlag(28, v); }
            else if (opcode == 2) { _r[rd] = Add(_r[rd], value, false, out _, out _); SetNz(_r[rd]); }
            else { _r[rd] = Sub(_r[rd], value, false, out _, out _); SetNz(_r[rd]); }
            return 1;
        }

        if ((op & 0xFC00) == 0x4000) // ALU operations
        {
            var opcode = (op >> 6) & 15;
            var rd = op & 7;
            var right = _r[(op >> 3) & 7];
            uint result = _r[rd];
            switch (opcode)
            {
                case 0: result &= right; break;
                case 1: result ^= right; break;
                case 2: { var s = Shift(result, (int)(right & 0xFF), 0, Flag(29)); result = s.Value; SetFlag(29, s.Carry); break; }
                case 3: { var s = Shift(result, (int)(right & 0xFF), 1, Flag(29)); result = s.Value; SetFlag(29, s.Carry); break; }
                case 4: { var s = Shift(result, (int)(right & 0xFF), 2, Flag(29)); result = s.Value; SetFlag(29, s.Carry); break; }
                case 5: result = Add(result, right, Flag(29), out var c5, out var v5); SetFlag(29, c5); SetFlag(28, v5); break;
                case 6: result = Sub(result, right, !Flag(29), out var c6, out var v6); SetFlag(29, c6); SetFlag(28, v6); break;
                case 8: result &= right; SetNz(result); return 1;
                case 9: result = 0u - right; break;
                case 10: result = Sub(result, right, false, out var c10, out var v10); SetFlag(29, c10); SetFlag(28, v10); SetNz(result); return 1;
                case 11: result = Add(result, right, false, out var c11, out var v11); SetFlag(29, c11); SetFlag(28, v11); SetNz(result); return 1;
                case 12: result |= right; break;
                case 13: result *= right; break;
                case 14: result &= ~right; break;
                case 15: result = ~right; break;
                default: return 1;
            }
            _r[rd] = result;
            SetNz(result);
            return opcode == 13 ? 2 : 1;
        }

        if ((op & 0xFC00) == 0x4400) // High register operations and BX
        {
            var operation = (op >> 8) & 3;
            var rd = (op & 7) | ((op >> 4) & 8);
            var rs = (op >> 3) & 15;
            if (operation == 3)
            {
                _r[15] = _r[rs] & ~1u;
                _cpsr |= 1u << 5;
                return 3;
            }
            if (operation == 0) _r[rd] = ReadReg(rd) + ReadReg(rs);
            else if (operation == 1) { var res = Sub(ReadReg(rd), ReadReg(rs), false, out var c, out var v); SetNz(res); SetFlag(29, c); SetFlag(28, v); }
            else _r[rd] = ReadReg(rs);
            if (rd == 15) _r[15] &= ~1u;
            return 1;
        }

        if ((op & 0xF000) == 0x5000) return ThumbRegisterTransfer(op);
        if ((op & 0xE000) == 0x6000) return ThumbImmediateTransfer(op);
        if ((op & 0xF000) == 0x8000) return ThumbHalfwordTransfer(op);
        if ((op & 0xF000) == 0x9000) return ThumbStackTransfer(op);

        if ((op & 0xF000) == 0xD000) // Conditional branch
        {
            var cond = (op >> 8) & 15;
            if (cond == 15) return 1; // SWI placeholder
            if (ConditionPassed(cond)) _r[15] = unchecked(_r[15] + (uint)(SignExtend((uint)(op & 0xFF), 8) << 1));
            return 3;
        }

        if ((op & 0xF800) == 0x4800) // PC-relative LDR
        {
            var rd = (op >> 8) & 7;
            var address = ((_r[15] + 2) & ~3u) + (uint)((op & 0xFF) * 4);
            _r[rd] = _memory.Read32(address);
            return 3;
        }

        if ((op & 0xF000) == 0xA000) // ADD Rd, PC/SP, #imm
        {
            var rd = (op >> 8) & 7;
            var source = (op & 0x0800) != 0 ? _r[13] : ((_r[15] + 2) & ~3u);
            _r[rd] = source + (uint)((op & 0xFF) * 4);
            return 1;
        }

        if ((op & 0xFF00) == 0xB000) // ADD/SUB SP, #imm
        {
            var amount = (uint)((op & 0x7F) * 4);
            _r[13] = (op & 0x80) != 0 ? _r[13] - amount : _r[13] + amount;
            return 1;
        }

        if ((op & 0xF800) == 0xE000) // Unconditional branch
        {
            _r[15] = unchecked(_r[15] + (uint)(SignExtend((uint)(op & 0x7FF), 11) << 1));
            return 3;
        }

        if ((op & 0xF800) == 0xF000) // Long branch prefix (BL)
        {
            _r[14] = unchecked(_r[15] + (uint)(SignExtend((uint)(op & 0x7FF), 11) << 12));
            return 1;
        }

        if ((op & 0xF800) == 0xF800) // Long branch suffix
        {
            var target = unchecked(_r[14] + (uint)((op & 0x7FF) << 1));
            _r[14] = (_r[15] - 2) | 1;
            _r[15] = target;
            return 3;
        }

        return 1;
    }

    private int ThumbRegisterTransfer(ushort op)
    {
        var operation = (op >> 9) & 7;
        var address = _r[(op >> 3) & 7] + _r[(op >> 6) & 7];
        var rd = op & 7;
        switch (operation)
        {
            case 0: _memory.Write32(address, _r[rd]); break;
            case 1: _memory.Write16(address, (ushort)_r[rd]); break;
            case 2: _memory.Write8(address, (byte)_r[rd]); break;
            case 3: _r[rd] = unchecked((uint)(sbyte)_memory.Read8(address)); break;
            case 4: _r[rd] = _memory.Read32(address); break;
            case 5: _r[rd] = _memory.Read16(address); break;
            case 6: _r[rd] = _memory.Read8(address); break;
            case 7: _r[rd] = unchecked((uint)(short)_memory.Read16(address)); break;
        }
        return operation >= 4 ? 3 : 2;
    }

    private int ThumbImmediateTransfer(ushort op)
    {
        var byteTransfer = (op & 0x1000) != 0;
        var load = (op & 0x0800) != 0;
        var offset = (uint)((op >> 6) & 31) * (byteTransfer ? 1u : 4u);
        var address = _r[(op >> 3) & 7] + offset;
        var rd = op & 7;
        if (load) _r[rd] = byteTransfer ? _memory.Read8(address) : _memory.Read32(address);
        else if (byteTransfer) _memory.Write8(address, (byte)_r[rd]);
        else _memory.Write32(address, _r[rd]);
        return load ? 3 : 2;
    }

    private int ThumbHalfwordTransfer(ushort op)
    {
        var load = (op & 0x0800) != 0;
        var offset = (uint)((op >> 6) & 31) * 2;
        var address = _r[(op >> 3) & 7] + offset;
        var rd = op & 7;
        if (load) _r[rd] = _memory.Read16(address);
        else _memory.Write16(address, (ushort)_r[rd]);
        return load ? 3 : 2;
    }

    private int ThumbStackTransfer(ushort op)
    {
        var load = (op & 0x0800) != 0;
        var rd = (op >> 8) & 7;
        var address = _r[13] + (uint)((op & 0xFF) * 4);
        if (load) _r[rd] = _memory.Read32(address);
        else _memory.Write32(address, _r[rd]);
        return load ? 3 : 2;
    }

    private uint ReadReg(int index) => index == 15 ? _r[15] + 4 : _r[index];

    private bool ConditionPassed(int condition)
    {
        var n = Flag(31); var z = Flag(30); var c = Flag(29); var v = Flag(28);
        return condition switch
        {
            0 => z, 1 => !z, 2 => c, 3 => !c, 4 => n, 5 => !n,
            6 => v, 7 => !v, 8 => c && !z, 9 => !c || z,
            10 => n == v, 11 => n != v, 12 => !z && n == v,
            13 => z || n != v, 14 => true, _ => false
        };
    }

    private static (uint Value, bool Carry) Shift(uint value, int amount, int type, bool carryIn)
    {
        if (amount == 0)
        {
            if (type == 0) return (value, carryIn);
            if (type == 1) amount = 32;
            else if (type == 2) amount = 32;
            else return (RotateRight(value, 1), (value & 1) != 0);
        }
        return type switch
        {
            0 => amount < 32 ? (value << amount, ((value >> (32 - amount)) & 1) != 0) : (0, amount == 32 && (value & 1u << 0) != 0),
            1 => amount < 32 ? (value >> amount, ((value >> (amount - 1)) & 1) != 0) : (0, amount == 32 && (value & 0x80000000) != 0),
            2 => amount < 32 ? ((uint)((int)value >> amount), ((value >> (amount - 1)) & 1) != 0) : ((value & 0x80000000) != 0 ? uint.MaxValue : 0, (value & 0x80000000) != 0),
            _ => (RotateRight(value, amount & 31), (RotateRight(value, amount & 31) & 0x80000000) != 0)
        };
    }

    private static uint RotateRight(uint value, int amount) => amount == 0 ? value : (value >> amount) | (value << (32 - amount));
    private static int SignExtend(uint value, int bits) => (int)(value << (32 - bits)) >> (32 - bits);
    private bool Flag(int bit) => (_cpsr & (1u << bit)) != 0;
    private void SetFlag(int bit, bool set) => _cpsr = set ? _cpsr | (1u << bit) : _cpsr & ~(1u << bit);
    private void SetNz(uint value) { SetFlag(31, (value & 0x80000000) != 0); SetFlag(30, value == 0); }

    private static uint Add(uint left, uint right, bool carryIn, out bool carry, out bool overflow)
    {
        var sum = (ulong)left + right + (carryIn ? 1UL : 0UL);
        var result = (uint)sum;
        carry = (sum >> 32) != 0;
        overflow = (~(left ^ right) & (left ^ result) & 0x80000000) != 0;
        return result;
    }

    private static uint Sub(uint left, uint right, bool borrow, out bool carry, out bool overflow)
    {
        var subtrahend = (ulong)right + (borrow ? 1UL : 0UL);
        var result = unchecked(left - (uint)subtrahend);
        carry = (ulong)left >= subtrahend;
        overflow = ((left ^ right) & (left ^ result) & 0x80000000) != 0;
        return result;
    }
}
