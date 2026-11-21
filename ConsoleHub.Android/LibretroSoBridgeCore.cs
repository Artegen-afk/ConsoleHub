using System.Runtime.InteropServices;
using ConsoleHub;

namespace ConsoleHub.Android;

/// <summary>Android libretro frontend for software framebuffer cores loaded from app-private .so files.</summary>
internal sealed class LibretroSoBridgeCore : CoreInterfaceBase
{
    private const int EnvironmentSetPixelFormat = 10;
    private const int EnvironmentGetSystemDirectory = 9;
    private const int EnvironmentGetSaveDirectory = 31;
    private readonly string _appDirectory;
    private readonly string _systemDirectory;
    private readonly string _saveDirectory;
    private readonly List<short> _audioWork = new(4096);
    private nint _library;
    private RetroLoadGame? _loadGame;
    private RetroRun? _run;
    private RetroUnloadGame? _unloadGame;
    private RetroDeinit? _deinit;
    private RetroSerializeSize? _serializeSize;
    private RetroSerialize? _serialize;
    private RetroUnserialize? _unserialize;
    private RetroSetEnvironment? _setEnvironment;
    private RetroSetVideoRefresh? _setVideoRefresh;
    private RetroSetAudioSample? _setAudioSample;
    private RetroSetAudioSampleBatch? _setAudioBatch;
    private RetroSetInputPoll? _setInputPoll;
    private RetroSetInputState? _setInputState;
    private readonly RetroEnvironment _environmentCallback;
    private readonly RetroVideoRefresh _videoCallback;
    private readonly RetroAudioSample _audioCallback;
    private readonly RetroAudioBatch _audioBatchCallback;
    private readonly RetroInputPoll _inputPollCallback;
    private readonly RetroInputState _inputStateCallback;
    private string _coreName = "libretro";
    private EmulatorInput _input;
    private uint[] _frame = [];
    private short[] _audio = [];
    private int _pixelFormat;
    private double _sampleRate = 48000;
    private bool _gameLoaded;

    public LibretroSoBridgeCore(string appDirectory)
    {
        _appDirectory = appDirectory;
        _systemDirectory = Path.Combine(appDirectory, "system");
        _saveDirectory = Path.Combine(appDirectory, "saves");
        Directory.CreateDirectory(_systemDirectory);
        Directory.CreateDirectory(_saveDirectory);
        _environmentCallback = Environment;
        _videoCallback = VideoRefresh;
        _audioCallback = AudioSample;
        _audioBatchCallback = AudioBatch;
        _inputPollCallback = InputPoll;
        _inputStateCallback = InputState;
    }

    protected override string CoreId => $"libretro-{_coreName}";
    private int _frameWidth = 240;
    private int _frameHeight = 160;
    public override int FrameWidth => _frameWidth;
    public override int FrameHeight => _frameHeight;
    public override int AudioSampleRate => (int)_sampleRate;
    public override string StatusText => _coreName;

    public void LoadCore(string path)
    {
        if (_library != 0) throw new InvalidOperationException("Um núcleo libretro já foi carregado nesta instância.");
        _library = NativeLibrary.Load(path);
        _coreName = Path.GetFileNameWithoutExtension(path);
        try
        {
            _setEnvironment = Export<RetroSetEnvironment>("retro_set_environment");
            _setVideoRefresh = Export<RetroSetVideoRefresh>("retro_set_video_refresh");
            _setAudioSample = Export<RetroSetAudioSample>("retro_set_audio_sample");
            _setAudioBatch = Export<RetroSetAudioSampleBatch>("retro_set_audio_sample_batch");
            _setInputPoll = Export<RetroSetInputPoll>("retro_set_input_poll");
            _setInputState = Export<RetroSetInputState>("retro_set_input_state");
            _loadGame = Export<RetroLoadGame>("retro_load_game");
            _run = Export<RetroRun>("retro_run");
            _unloadGame = Export<RetroUnloadGame>("retro_unload_game");
            _deinit = Export<RetroDeinit>("retro_deinit");
            _serializeSize = Export<RetroSerializeSize>("retro_serialize_size");
            _serialize = Export<RetroSerialize>("retro_serialize");
            _unserialize = Export<RetroUnserialize>("retro_unserialize");
            _setEnvironment(_environmentCallback);
            _setVideoRefresh(_videoCallback);
            _setAudioSample(_audioCallback);
            _setAudioBatch(_audioBatchCallback);
            _setInputPoll(_inputPollCallback);
            _setInputState(_inputStateCallback);
            Export<RetroInit>("retro_init")();
            var av = Export<RetroGetAvInfo>("retro_get_system_av_info");
            av(out var info);
            if (info.Geometry.BaseWidth is > 0 and <= 4096 && info.Geometry.BaseHeight is > 0 and <= 4096)
                ResizeFrame((int)info.Geometry.BaseWidth, (int)info.Geometry.BaseHeight);
            if (info.Timing.SampleRate is > 1000 and < 384000) _sampleRate = info.Timing.SampleRate;
        }
        catch
        {
            NativeLibrary.Free(_library);
            _library = 0;
            throw;
        }
    }

    protected override void LoadCoreRom(string romPath)
    {
        if (_library == 0 || _loadGame is null) throw new InvalidOperationException("Carregue primeiro um núcleo libretro .so.");
        var info = new RetroGameInfo { Path = Marshal.StringToCoTaskMemUTF8(romPath), Data = 0, Size = 0, Meta = 0 };
        try
        {
            if (!_loadGame(ref info)) throw new InvalidDataException($"O núcleo {_coreName} recusou a ROM.");
            _gameLoaded = true;
        }
        finally { Marshal.FreeCoTaskMem(info.Path); }
    }

    public override void RunFrame()
    {
        if (!_gameLoaded) return;
        _audioWork.Clear();
        _run!();
        _audio = _audioWork.ToArray();
    }

    public override ReadOnlyMemory<uint> GetFrameBuffer() => _frame;
    public override ReadOnlyMemory<short> GetAudioBuffer() => _audio;
    public override void SetInput(EmulatorInput input) => _input = input;

    protected override void WriteCoreState(BinaryWriter writer)
    {
        if (_serializeSize is null || _serialize is null) throw new NotSupportedException("Este núcleo não oferece save states.");
        var size = checked((int)_serializeSize());
        if (size <= 0 || size > 256 * 1024 * 1024) throw new InvalidDataException("Tamanho de save state inválido.");
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!_serialize(buffer, (nuint)size)) throw new InvalidOperationException("O núcleo falhou ao criar o save state.");
            var bytes = new byte[size];
            Marshal.Copy(buffer, bytes, 0, size);
            writer.Write(size);
            writer.Write(bytes);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    protected override void ReadCoreState(BinaryReader reader)
    {
        if (_unserialize is null) throw new NotSupportedException("Este núcleo não oferece save states.");
        var size = reader.ReadInt32();
        if (size <= 0 || size > 256 * 1024 * 1024) throw new InvalidDataException("Tamanho de save state inválido.");
        var bytes = reader.ReadBytes(size);
        if (bytes.Length != size) throw new EndOfStreamException();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(bytes, 0, buffer, size);
            if (!_unserialize(buffer, (nuint)size)) throw new InvalidDataException("O núcleo rejeitou o save state.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    private bool Environment(uint command, nint data)
    {
        switch (command)
        {
            case EnvironmentSetPixelFormat:
                _pixelFormat = Marshal.ReadInt32(data);
                return _pixelFormat is 0 or 1 or 2;
            case EnvironmentGetSystemDirectory:
                Marshal.WriteIntPtr(data, Marshal.StringToCoTaskMemUTF8(_systemDirectory));
                return true;
            case EnvironmentGetSaveDirectory:
                Marshal.WriteIntPtr(data, Marshal.StringToCoTaskMemUTF8(_saveDirectory));
                return true;
            default: return false;
        }
    }

    private void VideoRefresh(nint data, uint width, uint height, nuint pitch)
    {
        if (data == 0 || width == 0 || height == 0 || width > 4096 || height > 4096) return;
        ResizeFrame((int)width, (int)height);
        var rowBytes = checked((int)width * (_pixelFormat == 2 ? 4 : 2));
        var sourcePitch = checked((int)pitch);
        var row = new byte[rowBytes];
        for (var y = 0; y < (int)height; y++)
        {
            Marshal.Copy(data + y * sourcePitch, row, 0, rowBytes);
            for (var x = 0; x < (int)width; x++)
            {
                if (_pixelFormat == 1)
                {
                    var p = x * 2;
                    var value = (ushort)(row[p] | row[p + 1] << 8);
                    var r = (uint)(((value >> 11) & 31) * 255 / 31);
                    var g = (uint)(((value >> 5) & 63) * 255 / 63);
                    var b = (uint)((value & 31) * 255 / 31);
                    _frame[y * (int)width + x] = 0xFF000000u | b << 16 | g << 8 | r;
                }
                else if (_pixelFormat == 0)
                {
                    var p = x * 2;
                    var value = (ushort)(row[p] | row[p + 1] << 8);
                    var r = (uint)(((value >> 10) & 31) * 255 / 31);
                    var g = (uint)(((value >> 5) & 31) * 255 / 31);
                    var b = (uint)((value & 31) * 255 / 31);
                    _frame[y * (int)width + x] = 0xFF000000u | b << 16 | g << 8 | r;
                }
                else
                {
                    var p = x * 4;
                    var value = (uint)(row[p] | row[p + 1] << 8 | row[p + 2] << 16 | row[p + 3] << 24);
                    _frame[y * (int)width + x] = value | 0xFF000000u;
                }
            }
        }
    }

    private void ResizeFrame(int width, int height)
    {
        if (FrameWidth == width && FrameHeight == height && _frame.Length == width * height) return;
        _frameWidth = width;
        _frameHeight = height;
        _frame = new uint[width * height];
    }

    private void AudioSample(short left, short right) { _audioWork.Add(left); _audioWork.Add(right); }
    private nuint AudioBatch(nint data, nuint frames)
    {
        var count = checked((int)frames * 2);
        var values = new short[count];
        Marshal.Copy(data, values, 0, count);
        _audioWork.AddRange(values);
        return frames;
    }
    private void InputPoll() { }
    private short InputState(uint port, uint device, uint index, uint id)
    {
        if (port != 0 || device != 1 || index != 0) return 0;
        var button = id switch
        {
            0 => EmulatorInput.B, 2 => EmulatorInput.Select, 3 => EmulatorInput.Start,
            4 => EmulatorInput.Up, 5 => EmulatorInput.Down, 6 => EmulatorInput.Left, 7 => EmulatorInput.Right,
            8 => EmulatorInput.A, 10 => EmulatorInput.L, 11 => EmulatorInput.R, _ => EmulatorInput.None
        };
        return (_input & button) != 0 ? (short)1 : (short)0;
    }

    public override void Dispose()
    {
        if (_gameLoaded) { _unloadGame?.Invoke(); _gameLoaded = false; }
        if (_library != 0)
        {
            _deinit?.Invoke();
            NativeLibrary.Free(_library);
            _library = 0;
        }
        base.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] private struct RetroGameInfo { public nint Path, Data; public nuint Size; public nint Meta; }
    [StructLayout(LayoutKind.Sequential)] private struct RetroGameGeometry { public uint BaseWidth, BaseHeight, MaxWidth, MaxHeight; public float AspectRatio; }
    [StructLayout(LayoutKind.Sequential)] private struct RetroSystemTiming { public double Fps, SampleRate; }
    [StructLayout(LayoutKind.Sequential)] private struct RetroAvInfo { public RetroGameGeometry Geometry; public RetroSystemTiming Timing; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroInit();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroDeinit();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint RetroApiVersion();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroGetAvInfo(out RetroAvInfo info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroSetEnvironment(RetroEnvironment callback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroSetVideoRefresh(RetroVideoRefresh callback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroSetAudioSample(RetroAudioSample callback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroSetAudioSampleBatch(RetroAudioBatch callback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroSetInputPoll(RetroInputPoll callback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroSetInputState(RetroInputState callback);
    [return: MarshalAs(UnmanagedType.I1)] [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool RetroLoadGame(ref RetroGameInfo info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroUnloadGame();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroRun();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint RetroSerializeSize();
    [return: MarshalAs(UnmanagedType.I1)] [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool RetroSerialize(nint data, nuint size);
    [return: MarshalAs(UnmanagedType.I1)] [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool RetroUnserialize(nint data, nuint size);
    [return: MarshalAs(UnmanagedType.I1)] [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool RetroEnvironment(uint command, nint data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroVideoRefresh(nint data, uint width, uint height, nuint pitch);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroAudioSample(short left, short right);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint RetroAudioBatch(nint data, nuint frames);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroInputPoll();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate short RetroInputState(uint port, uint device, uint index, uint id);
}
