namespace ConsoleHub.Gba;

internal sealed class GbaMemory
{
    private readonly byte[] _ewram = new byte[256 * 1024];
    private readonly byte[] _iwram = new byte[32 * 1024];
    private readonly byte[] _io = new byte[0x400];
    private readonly byte[] _palette = new byte[0x400];
    private readonly byte[] _vram = new byte[0x18000];
    private readonly byte[] _oam = new byte[0x400];
    private byte[] _rom = [];
    private ushort _keys = 0x03FF;

    public byte[] Palette => _palette;
    public byte[] Vram => _vram;
    public byte ReadVram(int offset) => _vram[MapVram(offset)];

    public void LoadCartridge(byte[] rom)
    {
        if (rom.Length < 0xC0)
            throw new InvalidDataException("O arquivo é pequeno demais para um cartucho GBA válido.");
        _rom = rom;
        Array.Clear(_ewram);
        Array.Clear(_iwram);
        Array.Clear(_io);
        Array.Clear(_palette);
        Array.Clear(_vram);
        Array.Clear(_oam);
        _keys = 0x03FF;
    }

    public void SetInput(EmulatorInput input)
    {
        ushort pressed = 0;
        foreach (var (button, bit) in new[]
        {
            (EmulatorInput.A, 0), (EmulatorInput.B, 1), (EmulatorInput.Select, 2),
            (EmulatorInput.Start, 3), (EmulatorInput.Right, 4), (EmulatorInput.Left, 5),
            (EmulatorInput.Up, 6), (EmulatorInput.Down, 7), (EmulatorInput.R, 8), (EmulatorInput.L, 9)
        })
            if ((input & button) != 0) pressed |= (ushort)(1 << bit);

        _keys = (ushort)(0x03FF & ~pressed);
    }

    public ushort ReadIo16(int offset)
    {
        if (offset == 0x130) return _keys;
        return (ushort)(_io[offset] | (_io[offset + 1] << 8));
    }

    public void WriteState(BinaryWriter writer)
    {
        writer.Write(_ewram); writer.Write(_iwram); writer.Write(_io);
        writer.Write(_palette); writer.Write(_vram); writer.Write(_oam);
        writer.Write(_keys);
    }

    public void ReadState(BinaryReader reader)
    {
        ReadArray(reader, _ewram); ReadArray(reader, _iwram); ReadArray(reader, _io);
        ReadArray(reader, _palette); ReadArray(reader, _vram); ReadArray(reader, _oam);
        _keys = reader.ReadUInt16();
    }

    private static void ReadArray(BinaryReader reader, byte[] target)
    {
        var bytes = reader.ReadBytes(target.Length);
        if (bytes.Length != target.Length) throw new InvalidDataException("Save state GBA truncado.");
        Buffer.BlockCopy(bytes, 0, target, 0, target.Length);
    }

    public byte Read8(uint address)
    {
        var region = address >> 24;
        var offset = (int)(address & 0x00FFFFFF);
        return region switch
        {
            0x02 => _ewram[offset & 0x3FFFF],
            0x03 => _iwram[offset & 0x7FFF],
            0x04 => offset switch
            {
                0x130 => (byte)_keys,
                0x131 => (byte)(_keys >> 8),
                _ when offset < _io.Length => _io[offset],
                _ => (byte)0
            },
            0x05 => _palette[offset & 0x3FF],
            0x06 => ReadVramByte(offset),
            0x07 => _oam[offset & 0x3FF],
            >= 0x08 and <= 0x0D => ReadRom(address),
            0x0E => 0xFF,
            _ => 0
        };
    }

    public ushort Read16(uint address)
    {
        address &= ~1u;
        return (ushort)(Read8(address) | (Read8(address + 1) << 8));
    }

    public uint Read32(uint address)
    {
        address &= ~3u;
        return (uint)(Read8(address) | (Read8(address + 1) << 8) |
                      (Read8(address + 2) << 16) | (Read8(address + 3) << 24));
    }

    public void Write8(uint address, byte value)
    {
        var region = address >> 24;
        var offset = (int)(address & 0x00FFFFFF);
        switch (region)
        {
            case 0x02: _ewram[offset & 0x3FFFF] = value; break;
            case 0x03: _iwram[offset & 0x7FFF] = value; break;
            case 0x04:
                if (offset < _io.Length && offset != 0x130 && offset != 0x131) _io[offset] = value;
                break;
            case 0x05: _palette[offset & 0x3FF] = value; break;
            case 0x06: _vram[MapVram(offset)] = value; break;
            case 0x07: _oam[offset & 0x3FF] = value; break;
        }
    }

    public void Write16(uint address, ushort value)
    {
        address &= ~1u;
        Write8(address, (byte)value);
        Write8(address + 1, (byte)(value >> 8));
    }

    public void Write32(uint address, uint value)
    {
        address &= ~3u;
        Write16(address, (ushort)value);
        Write16(address + 2, (ushort)(value >> 16));
    }

    private byte ReadRom(uint address)
    {
        if (_rom.Length == 0) return 0;
        var offset = (int)((address - 0x08000000u) % (uint)_rom.Length);
        return _rom[offset];
    }

    private byte ReadVramByte(int offset) => _vram[MapVram(offset)];

    private static int MapVram(int offset)
    {
        offset &= 0x1FFFF;
        return offset >= 0x18000 ? offset - 0x8000 : offset;
    }
}
