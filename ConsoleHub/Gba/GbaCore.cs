namespace ConsoleHub.Gba;

/// <summary>
/// Early GBA core: cartridge bus, a small ARM7TDMI instruction subset and BG0/bitmap video.
/// It is an integration skeleton, not yet a compatibility-complete emulator.
/// </summary>
[CorePlugin("GBA", ".gba")]
public sealed class GbaCore : CoreInterfaceBase
{
    public const int ScreenWidth = 240;
    public const int ScreenHeight = 160;
    private const int CyclesPerFrame = 280896;
    private const int CpuClockHz = 16777216;
    private const int AudioRateHz = 32768;
    private readonly GbaMemory _memory = new();
    private readonly Arm7Tdmi _cpu;
    private readonly uint[] _frame = new uint[ScreenWidth * ScreenHeight];
    private short[] _audio = [];
    private long _audioSamplePhase;
    private double _square1Phase;
    private double _square2Phase;
    private EmulatorInput _input;

    public GbaCore() => _cpu = new Arm7Tdmi(_memory);

    protected override string CoreId => "gba-arm7tdmi-v1";
    public override int FrameHeight => ScreenHeight;
    public override int FrameWidth => ScreenWidth;
    public override int AudioSampleRate => AudioRateHz;

    protected override void LoadCoreRom(string romPath)
    {
        if (!Path.GetExtension(romPath).Equals(".gba", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("O núcleo GBA aceita arquivos .gba.", nameof(romPath));

        _memory.LoadCartridge(File.ReadAllBytes(romPath));
        _cpu.Reset();
        Array.Fill(_frame, 0xFF000000u);
        _audioSamplePhase = 0;
        _square1Phase = 0;
        _square2Phase = 0;
    }

    public override void SetInput(EmulatorInput input) => _input = input;

    public override void RunFrame()
    {
        _memory.SetInput(_input);
        var cycles = 0;
        while (cycles < CyclesPerFrame)
            cycles += _cpu.Step();

        RenderVideo();
        GenerateAudio();
    }

    public override ReadOnlyMemory<uint> GetFrameBuffer() => _frame;
    public override ReadOnlyMemory<short> GetAudioBuffer() => _audio;

    protected override void WriteCoreState(BinaryWriter writer)
    {
        _memory.WriteState(writer);
        _cpu.WriteState(writer);
        writer.Write((ushort)_input);
        writer.Write(_audioSamplePhase);
        writer.Write(_square1Phase);
        writer.Write(_square2Phase);
    }

    protected override void ReadCoreState(BinaryReader reader)
    {
        _memory.ReadState(reader);
        _cpu.ReadState(reader);
        _input = (EmulatorInput)reader.ReadUInt16();
        _audioSamplePhase = reader.ReadInt64();
        _square1Phase = reader.ReadDouble();
        _square2Phase = reader.ReadDouble();
        RenderVideo();
        _audio = [];
    }

    private void GenerateAudio()
    {
        _audioSamplePhase += (long)AudioRateHz * CyclesPerFrame;
        var sampleCount = (int)(_audioSamplePhase / CpuClockHz);
        _audioSamplePhase %= CpuClockHz;
        _audio = new short[sampleCount * 2];

        var soundEnabled = (_memory.ReadIo16(0x084) & 0x0080) != 0;
        if (!soundEnabled) return;

        var routing = _memory.ReadIo16(0x080);
        var master = _memory.ReadIo16(0x082);
        var ch1Volume = _memory.ReadIo16(0x062) >> 12 & 15;
        var ch2Volume = _memory.ReadIo16(0x068) >> 12 & 15;
        var ch1Duty = ((_memory.ReadIo16(0x060) >> 6) & 3) switch { 0 => 0.125, 1 => 0.25, 2 => 0.5, _ => 0.75 };
        var ch2Duty = ((_memory.ReadIo16(0x066) >> 6) & 3) switch { 0 => 0.125, 1 => 0.25, 2 => 0.5, _ => 0.75 };
        var ch1Hz = SquareFrequency(_memory.ReadIo16(0x064));
        var ch2Hz = SquareFrequency(_memory.ReadIo16(0x06C));
        var leftScale = ((master >> 4) & 7) + 1;
        var rightScale = (master & 7) + 1;
        var ch1Left = (routing & 1) != 0;
        var ch2Left = (routing & 2) != 0;
        var ch1Right = (routing & 0x10) != 0;
        var ch2Right = (routing & 0x20) != 0;

        for (var i = 0; i < sampleCount; i++)
        {
            var one = SquareSample(ref _square1Phase, ch1Hz, ch1Duty, ch1Volume);
            var two = SquareSample(ref _square2Phase, ch2Hz, ch2Duty, ch2Volume);
            var left = ((ch1Left ? one : 0) + (ch2Left ? two : 0)) * leftScale / 8;
            var right = ((ch1Right ? one : 0) + (ch2Right ? two : 0)) * rightScale / 8;
            _audio[i * 2] = (short)Math.Clamp(left, short.MinValue, short.MaxValue);
            _audio[i * 2 + 1] = (short)Math.Clamp(right, short.MinValue, short.MaxValue);
        }
    }

    private static double SquareFrequency(ushort control)
    {
        var frequency = control & 0x07FF;
        return frequency == 2047 ? 0 : CpuClockHz / (32.0 * (2048 - frequency));
    }

    private static int SquareSample(ref double phase, double frequency, double duty, int volume)
    {
        if (frequency <= 0 || volume == 0) return 0;
        phase += frequency / AudioRateHz;
        phase -= Math.Floor(phase);
        var amplitude = volume * 500;
        return phase < duty ? amplitude : -amplitude;
    }

    private void RenderVideo()
    {
        var mode = _memory.ReadIo16(0x000) & 7;
        switch (mode)
        {
            case 3: RenderBitmapMode3(); break;
            case 4: RenderBitmapMode4(); break;
            case 5: RenderBitmapMode5(); break;
            default: RenderBackground0(); break;
        }
    }

    private void RenderBitmapMode3()
    {
        var vram = _memory.Vram;
        for (var i = 0; i < _frame.Length; i++)
            _frame[i] = Color15((ushort)(vram[i * 2] | (vram[i * 2 + 1] << 8)));
    }

    private void RenderBitmapMode4()
    {
        var vram = _memory.Vram;
        var page = (_memory.ReadIo16(0) & 0x10) != 0 ? 0xA000 : 0;
        var palette = _memory.Palette;
        for (var i = 0; i < _frame.Length; i++)
        {
            var index = vram[page + i];
            var p = index * 2;
            _frame[i] = Color15((ushort)(palette[p] | (palette[p + 1] << 8)));
        }
    }

    private void RenderBitmapMode5()
    {
        Array.Fill(_frame, 0xFF000000u);
        var vram = _memory.Vram;
        var page = (_memory.ReadIo16(0) & 0x10) != 0 ? 0xA000 : 0;
        for (var y = 0; y < 128; y++)
        for (var x = 0; x < 160; x++)
        {
            var src = page + (y * 160 + x) * 2;
            _frame[(y + 16) * ScreenWidth + x + 40] = Color15((ushort)(vram[src] | (vram[src + 1] << 8)));
        }
    }

    private void RenderBackground0()
    {
        Array.Fill(_frame, 0xFF000000u);
        var display = _memory.ReadIo16(0);
        if ((display & 0x0100) == 0) return;

        var control = _memory.ReadIo16(0x008);
        var charBase = ((control >> 2) & 3) * 0x4000;
        var screenBase = ((control >> 8) & 31) * 0x800;
        var size = (control >> 14) & 3;
        var mapWidth = (size & 1) != 0 ? 64 : 32;
        var mapHeight = (size & 2) != 0 ? 64 : 32;
        var scrollX = _memory.ReadIo16(0x010) & 0x1FF;
        var scrollY = _memory.ReadIo16(0x012) & 0x1FF;
        var color256 = (control & 0x0080) != 0;

        for (var y = 0; y < ScreenHeight; y++)
        for (var x = 0; x < ScreenWidth; x++)
        {
            var sx = (x + scrollX) % (mapWidth * 8);
            var sy = (y + scrollY) % (mapHeight * 8);
            var tileX = sx >> 3;
            var tileY = sy >> 3;
            var blockX = tileX / 32;
            var blockY = tileY / 32;
            var block = blockY * (mapWidth / 32) + blockX;
            var mapIndex = screenBase + block * 0x800 + ((tileY % 32) * 32 + tileX % 32) * 2;
            var entry = (ushort)(_memory.ReadVram(mapIndex) | (_memory.ReadVram(mapIndex + 1) << 8));
            var tile = entry & 0x03FF;
            var px = sx & 7;
            var py = sy & 7;
            if ((entry & 0x0400) != 0) px = 7 - px;
            if ((entry & 0x0800) != 0) py = 7 - py;

            int paletteIndex;
            if (color256)
            {
                paletteIndex = _memory.ReadVram(charBase + tile * 64 + py * 8 + px);
            }
            else
            {
                var packed = _memory.ReadVram(charBase + tile * 32 + py * 4 + px / 2);
                paletteIndex = ((px & 1) == 0 ? packed & 15 : packed >> 4) + ((entry >> 12) & 15) * 16;
            }

            if (paletteIndex == 0) continue;
            var p = paletteIndex * 2;
            var palette = _memory.Palette;
            _frame[y * ScreenWidth + x] = Color15((ushort)(palette[p] | (palette[p + 1] << 8)));
        }
    }

    private static uint Color15(ushort color)
    {
        var r5 = color & 31;
        var g5 = (color >> 5) & 31;
        var b5 = (color >> 10) & 31;
        var r = (uint)((r5 << 3) | (r5 >> 2));
        var g = (uint)((g5 << 3) | (g5 >> 2));
        var b = (uint)((b5 << 3) | (b5 >> 2));
        return 0xFF000000u | (b << 16) | (g << 8) | r;
    }
}
