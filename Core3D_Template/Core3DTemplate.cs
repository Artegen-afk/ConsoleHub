using ConsoleHub;

namespace Core3D_Template;

[CorePlugin("GameCube", ".iso", ".gcm", ".wbfs")]
[CorePlugin("Wii", ".iso", ".gcm", ".wbfs")]
[CorePlugin("PS2", ".iso", ".cso", ".chd")]
[CorePlugin("Switch", ".nsp", ".xci")]
public sealed class Core3DTemplate : CoreInterfaceBase
{
    private FileStream? _rom;
    private long _romLength;

    protected override string CoreId => "core3d-template-v1";
    public override int FrameWidth => 1280;
    public override int FrameHeight => 720;
    public override CoreRenderMode RenderMode => CoreRenderMode.OpenGL;

    protected override void LoadCoreRom(string romPath)
    {
        var extension = Path.GetExtension(romPath);
        var supported = new[] { ".iso", ".gcm", ".wbfs", ".cso", ".chd", ".nsp", ".xci" };
        if (!supported.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException($"Formato não suportado pelo template: {extension}");

        _rom?.Dispose();
        _rom = new FileStream(romPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024, options: FileOptions.RandomAccess);
        _romLength = _rom.Length;

        // Exemplo: inspeciona somente um pequeno cabeçalho, sem copiar ISO/NSP inteiro para RAM.
        Span<byte> header = stackalloc byte[64];
        _ = RandomAccess.Read(_rom.SafeFileHandle, header, 0);
    }

    /// <summary>Exemplo de leitura por faixa para imagens grandes mantidas em disco.</summary>
    public int ReadRom(long offset, Span<byte> destination)
    {
        if (_rom is null) throw new InvalidOperationException("Nenhuma imagem foi carregada.");
        if (offset < 0 || offset >= _romLength) return 0;
        var available = (int)Math.Min(destination.Length, _romLength - offset);
        return RandomAccess.Read(_rom.SafeFileHandle, destination[..available], offset);
    }

    public override void RunFrame()
    {
        // TODO: avançar CPU, GPU, áudio e sincronização deste console.
    }

    public override ReadOnlyMemory<uint> GetFrameBuffer() => ReadOnlyMemory<uint>.Empty;
    public override ReadOnlyMemory<short> GetAudioBuffer() => ReadOnlyMemory<short>.Empty;
    public override void SetInput(EmulatorInput input)
    {
        // TODO: traduzir os botões padronizados para os controles do console.
    }

    public override void Render(GraphicsContext context)
    {
        if (context.Api != CoreRenderMode.OpenGL)
            throw new NotSupportedException("Este template de exemplo usa OpenGL.");

        // O host chama este método durante BeginDrawing/EndDrawing, com o contexto GL corrente.
        // Obtenha funções GL em tempo de execução: context.GetProcAddress?.Invoke("gl...")
        // Desenhe a imagem 3D e restaure o estado GL usado pelo Raylib antes de retornar.
    }

    protected override void WriteCoreState(BinaryWriter writer)
    {
        // TODO: serializar RAM, registradores e estado do subsistema gráfico.
    }

    protected override void ReadCoreState(BinaryReader reader)
    {
        // TODO: restaurar exatamente os dados gravados em WriteCoreState.
    }

    public override void Dispose()
    {
        _rom?.Dispose();
        _rom = null;
    }
}
