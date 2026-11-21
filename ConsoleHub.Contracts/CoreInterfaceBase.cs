using System.Security.Cryptography;
using System.Text;

namespace ConsoleHub;

/// <summary>Shared ROM identity and on-disk save-state format for all emulator cores.</summary>
public abstract class CoreInterfaceBase : ICoreInterface
{
    private const int StateFormatVersion = 1;
    private const int StateMagic = 0x43535431; // CST1
    private byte[] _romHash = [];

    protected abstract string CoreId { get; }
    protected string LoadedRomPath { get; private set; } = "";
    public abstract int FrameWidth { get; }
    public abstract int FrameHeight { get; }
    public virtual int AudioSampleRate => 32768;
    public virtual string StatusText => "";
    public virtual CoreRenderMode RenderMode => CoreRenderMode.Framebuffer;
    public abstract void RunFrame();
    public abstract ReadOnlyMemory<uint> GetFrameBuffer();
    public abstract ReadOnlyMemory<short> GetAudioBuffer();
    public abstract void SetInput(EmulatorInput input);
    public virtual void Render(GraphicsContext context) { }
    public virtual void Dispose() { }
    protected abstract void LoadCoreRom(string romPath);
    protected abstract void WriteCoreState(BinaryWriter writer);
    protected abstract void ReadCoreState(BinaryReader reader);

    public void LoadRom(string romPath)
    {
        var fullPath = Path.GetFullPath(romPath);
        LoadCoreRom(fullPath);
        LoadedRomPath = fullPath;
        using var rom = File.OpenRead(fullPath);
        _romHash = SHA256.HashData(rom);
    }

    public void SaveState(int slot)
    {
        ValidateSlot(slot);
        EnsureRomLoaded();
        using var payloadStream = new MemoryStream();
        using (var payloadWriter = new BinaryWriter(payloadStream, Encoding.UTF8, leaveOpen: true))
            WriteCoreState(payloadWriter);

        var savePath = GetSlotPath(slot);
        Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
        var tempPath = savePath + ".tmp";
        try
        {
            using (var file = File.Create(tempPath))
            using (var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(StateMagic);
                writer.Write(StateFormatVersion);
                writer.Write(CoreId);
                writer.Write(_romHash.Length);
                writer.Write(_romHash);
                writer.Write((int)payloadStream.Length);
                writer.Write(payloadStream.GetBuffer(), 0, (int)payloadStream.Length);
            }
            File.Move(tempPath, savePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    public void LoadState(int slot)
    {
        ValidateSlot(slot);
        EnsureRomLoaded();
        var savePath = GetSlotPath(slot);
        using var file = File.OpenRead(savePath);
        using var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: false);

        if (reader.ReadInt32() != StateMagic) throw new InvalidDataException("Arquivo de save state inválido.");
        if (reader.ReadInt32() != StateFormatVersion) throw new InvalidDataException("Versão de save state não suportada.");
        if (!reader.ReadString().Equals(CoreId, StringComparison.Ordinal))
            throw new InvalidDataException("Esse save state pertence a outro núcleo.");

        var hashLength = reader.ReadInt32();
        var storedHash = reader.ReadBytes(hashLength);
        if (hashLength != _romHash.Length || !CryptographicOperations.FixedTimeEquals(storedHash, _romHash))
            throw new InvalidDataException("O save state foi criado com outra ROM.");

        var payloadLength = reader.ReadInt32();
        if (payloadLength < 0 || payloadLength > file.Length - file.Position)
            throw new InvalidDataException("Conteúdo do save state incompleto.");
        var payload = reader.ReadBytes(payloadLength);
        using var payloadStream = new MemoryStream(payload, writable: false);
        using var payloadReader = new BinaryReader(payloadStream, Encoding.UTF8, leaveOpen: false);
        ReadCoreState(payloadReader);
        if (payloadStream.Position != payloadStream.Length)
            throw new InvalidDataException("O save state contém dados inesperados.");
    }

    protected void EnsureRomLoaded()
    {
        if (string.IsNullOrEmpty(LoadedRomPath)) throw new InvalidOperationException("Carregue uma ROM antes de usar save states.");
    }

    private string GetSlotPath(int slot) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ConsoleHub", "Saves", CoreId, $"slot-{slot}.state");

    private static void ValidateSlot(int slot)
    {
        if (slot is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(slot), "Slots válidos: 1 a 10.");
    }
}
