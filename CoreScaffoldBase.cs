using ConsoleHub;

namespace ConsoleHub.Scaffolds;

/// <summary>
/// Shared file-inspection shell for the unimplemented platform projects.
/// It deliberately does not claim to execute instructions or decode game assets.
/// </summary>
public abstract class CoreScaffoldBase : CoreInterfaceBase
{
    private FileStream? _rom;
    private byte[] _romHeader = [];
    private readonly uint[] _pixels;
    private string _status = "Carregue uma imagem para inspecionar o cabeçalho.";
    private EmulatorInput _input;

    protected CoreScaffoldBase(int width, int height, uint accent)
    {
        FrameWidth = width;
        FrameHeight = height;
        Accent = accent;
        _pixels = new uint[width * height];
        PaintDiagnosticFrame();
    }

    protected abstract string CoreKey { get; }
    protected abstract string[] Extensions { get; }
    protected abstract string PlatformLabel { get; }
    protected uint Accent { get; }
    protected uint[] Pixels => _pixels;
    protected string? LoadedExtension { get; private set; }
    protected long RomLength { get; private set; }
    protected ReadOnlySpan<byte> RomHeader => _romHeader;

    public sealed override int FrameWidth { get; }
    public sealed override int FrameHeight { get; }
    public override string StatusText => _status;
    protected override string CoreId => CoreKey;

    protected override void LoadCoreRom(string romPath)
    {
        var extension = Path.GetExtension(romPath);
        if (!Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException($"{PlatformLabel}: formato {extension} não suportado por este scaffold.");

        _rom?.Dispose();
        _rom = new FileStream(romPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, options: FileOptions.RandomAccess);
        RomLength = _rom.Length;
        LoadedExtension = extension;

        Span<byte> header = stackalloc byte[64];
        var count = _rom.Read(header);
        _rom.Position = 0;
        _romHeader = header[..count].ToArray();
        _status = $"{PlatformLabel}: ROM identificada ({RomLength:N0} bytes). Scaffold apenas; CPU/GPU/áudio ainda não emulam o jogo.";
        OnRomInspected(romPath, RomHeader);
        PaintDiagnosticFrame();
    }

    protected virtual void OnRomInspected(string romPath, ReadOnlySpan<byte> header) { }
    protected void SetStatus(string message) => _status = message;

    /// <summary>Reads a small portion on demand; the image remains file-backed.</summary>
    protected int ReadRom(long offset, Span<byte> destination)
    {
        if (_rom is null) throw new InvalidOperationException("Carregue uma ROM antes de ler o conteúdo.");
        if (offset < 0 || offset >= _rom.Length) return 0;
        _rom.Position = offset;
        return _rom.Read(destination[..(int)Math.Min(destination.Length, _rom.Length - offset)]);
    }

    public override void RunFrame()
    {
        // This visual diagnostic only proves the framebuffer connection.
        // No emulated CPU, GPU, scheduler, or audio device is implemented here.
        PaintDiagnosticFrame();
    }

    public override ReadOnlyMemory<uint> GetFrameBuffer() => _pixels;
    public override ReadOnlyMemory<short> GetAudioBuffer() => ReadOnlyMemory<short>.Empty;
    public override void SetInput(EmulatorInput input) => _input = input;

    protected override void WriteCoreState(BinaryWriter writer) =>
        throw new NotSupportedException("Save states ficam indisponíveis até existir estado real de emulação.");

    protected override void ReadCoreState(BinaryReader reader) =>
        throw new NotSupportedException("Load states ficam indisponíveis até existir estado real de emulação.");

    public override void Dispose()
    {
        _rom?.Dispose();
        _rom = null;
    }

    private void PaintDiagnosticFrame()
    {
        Array.Fill(_pixels, 0xFF111827u);
        var stripeHeight = Math.Max(8, FrameHeight / 24);
        for (var y = 0; y < stripeHeight; y++)
        for (var x = 0; x < FrameWidth; x++)
            _pixels[y * FrameWidth + x] = Accent;

        var centerY = FrameHeight / 2;
        var centerX = FrameWidth / 2;
        var boxW = Math.Max(32, FrameWidth / 3);
        var boxH = Math.Max(16, FrameHeight / 8);
        var pressedColor = _input == EmulatorInput.None ? 0xFF374151u : 0xFF55B7F3u;
        for (var y = Math.Max(0, centerY - boxH / 2); y < Math.Min(FrameHeight, centerY + boxH / 2); y++)
        for (var x = Math.Max(0, centerX - boxW / 2); x < Math.Min(FrameWidth, centerX + boxW / 2); x++)
            _pixels[y * FrameWidth + x] = pressedColor;
    }
}
