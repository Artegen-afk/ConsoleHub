using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ConsoleHub;

namespace ConsoleHub.Libretro;

public sealed unsafe class LibretroBridgeCore : CoreInterfaceBase
{
    private const uint ApiVersionExpected = 1;
    private const uint EnvironmentGetOverscan = 2;
    private const uint EnvironmentGetCanDupe = 3;
    private const uint EnvironmentSetMessage = 6;
    private const uint EnvironmentShutdown = 7;
    private const uint EnvironmentGetSystemDirectory = 9;
    private const uint EnvironmentSetPixelFormat = 10;
    private const uint EnvironmentSetInputDescriptors = 11;
    private const uint EnvironmentSetHwRender = 14;
    private const uint EnvironmentGetVariable = 15;
    private const uint EnvironmentSetVariables = 16;
    private const uint EnvironmentGetVariableUpdate = 17;
    private const uint EnvironmentSetSupportNoGame = 18;
    private const uint EnvironmentGetSaveDirectory = 31;
    private const uint EnvironmentSetSystemAvInfo = 32;
    private const uint EnvironmentSetGeometry = 37;
    private const uint EnvironmentGetLanguage = 39;

    private const uint DeviceJoypad = 1;
    private const int PixelFormat0Rgb1555 = 0;
    private const int PixelFormatXrgb8888 = 1;
    private const int PixelFormatRgb565 = 2;
    private const int MaxVideoDimension = 8192;
    private const int MaxSerializeBytes = 512 * 1024 * 1024;

    private readonly string _nativeCorePath;
    private readonly nint _module;
    private readonly RetroApiVersion _apiVersion;
    private readonly RetroSetEnvironment _setEnvironment;
    private readonly RetroSetVideoRefresh _setVideoRefresh;
    private readonly RetroSetAudioSample _setAudioSample;
    private readonly RetroSetAudioSampleBatch _setAudioSampleBatch;
    private readonly RetroSetInputPoll _setInputPoll;
    private readonly RetroSetInputState _setInputState;
    private readonly RetroInit _init;
    private readonly RetroDeinit _deinit;
    private readonly RetroGetSystemInfo _getSystemInfo;
    private readonly RetroGetSystemAvInfo _getSystemAvInfo;
    private readonly RetroLoadGame _loadGame;
    private readonly RetroRun _run;
    private readonly RetroUnloadGame _unloadGame;
    private readonly RetroSetControllerPortDevice? _setControllerPortDevice;
    private readonly RetroSerializeSize? _serializeSize;
    private readonly RetroSerialize? _serialize;
    private readonly RetroUnserialize? _unserialize;
    private readonly RetroReset? _reset;

    // Keep callback delegates rooted for the entire native core lifetime.
    private readonly RetroEnvironmentCallback _environmentCallback;
    private readonly RetroVideoRefresh _videoRefreshCallback;
    private readonly RetroAudioSample _audioSampleCallback;
    private readonly RetroAudioSampleBatch _audioSampleBatchCallback;
    private readonly RetroInputPoll _inputPollCallback;
    private readonly RetroInputState _inputStateCallback;
    private readonly RetroHwGetCurrentFramebuffer _hwGetCurrentFramebufferCallback;
    private readonly RetroHwGetProcAddress _hwGetProcAddressCallback;
    private RetroHwContextReset? _coreHwContextReset;
    private RetroHwContextDestroy? _coreHwContextDestroy;

    private readonly List<short> _audioScratch = [];
    private readonly string[] _validExtensions;
    private readonly string[] _systemIds;
    private uint[] _video = new uint[320 * 240];
    private short[] _audio = [];
    private EmulatorInput _input;
    private int _width = 320;
    private int _height = 240;
    private int _pixelFormat = PixelFormat0Rgb1555;
    private bool _initialized;
    private bool _gameLoaded;
    private bool _usesHardwareRender;
    private bool _disposed;
    private string? _shutdownMessage;
    private nint _systemDirectoryPointer;
    private nint _saveDirectoryPointer;
    private nint _gamePathPointer;
    private MemoryMappedFile? _romMap;
    private MemoryMappedViewAccessor? _romView;
    private byte* _romMapPointer;
    private bool _romMapPointerAcquired;
    private nuint _romLength;
    private readonly bool _needsFullPath;
    private double _sampleRate = 32768;

    public LibretroBridgeCore(string nativeCorePath) : this(nativeCorePath, initializeCore: true) { }

    private LibretroBridgeCore(string nativeCorePath, bool initializeCore)
    {
        _nativeCorePath = Path.GetFullPath(nativeCorePath);
        _module = NativeMethods.LoadLibrary(_nativeCorePath);
        if (_module == 0)
            throw new InvalidOperationException($"Não foi possível abrir o núcleo nativo ({Marshal.GetLastWin32Error()}).");

        try
        {
            _apiVersion = GetRequired<RetroApiVersion>("retro_api_version");
            if (_apiVersion() != ApiVersionExpected)
                throw new NotSupportedException("A DLL não usa a versão 1 da API libretro.");

            _setEnvironment = GetRequired<RetroSetEnvironment>("retro_set_environment");
            _setVideoRefresh = GetRequired<RetroSetVideoRefresh>("retro_set_video_refresh");
            _setAudioSample = GetRequired<RetroSetAudioSample>("retro_set_audio_sample");
            _setAudioSampleBatch = GetRequired<RetroSetAudioSampleBatch>("retro_set_audio_sample_batch");
            _setInputPoll = GetRequired<RetroSetInputPoll>("retro_set_input_poll");
            _setInputState = GetRequired<RetroSetInputState>("retro_set_input_state");
            _init = GetRequired<RetroInit>("retro_init");
            _deinit = GetRequired<RetroDeinit>("retro_deinit");
            _getSystemInfo = GetRequired<RetroGetSystemInfo>("retro_get_system_info");
            _getSystemAvInfo = GetRequired<RetroGetSystemAvInfo>("retro_get_system_av_info");
            _loadGame = GetRequired<RetroLoadGame>("retro_load_game");
            _run = GetRequired<RetroRun>("retro_run");
            _unloadGame = GetRequired<RetroUnloadGame>("retro_unload_game");
            _setControllerPortDevice = GetOptional<RetroSetControllerPortDevice>("retro_set_controller_port_device");
            _serializeSize = GetOptional<RetroSerializeSize>("retro_serialize_size");
            _serialize = GetOptional<RetroSerialize>("retro_serialize");
            _unserialize = GetOptional<RetroUnserialize>("retro_unserialize");
            _reset = GetOptional<RetroReset>("retro_reset");

            _environmentCallback = EnvironmentCallback;
            _videoRefreshCallback = VideoRefreshCallback;
            _audioSampleCallback = AudioSampleCallback;
            _audioSampleBatchCallback = AudioSampleBatchCallback;
            _inputPollCallback = InputPollCallback;
            _inputStateCallback = InputStateCallback;
            _hwGetCurrentFramebufferCallback = HwGetCurrentFramebufferCallback;
            _hwGetProcAddressCallback = HwGetProcAddressCallback;

            var dirs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConsoleHub");
            var systemDirectory = Path.Combine(dirs, "System");
            var saveDirectory = Path.Combine(dirs, "Saves");
            Directory.CreateDirectory(systemDirectory);
            Directory.CreateDirectory(saveDirectory);
            _systemDirectoryPointer = Marshal.StringToCoTaskMemUTF8(systemDirectory);
            _saveDirectoryPointer = Marshal.StringToCoTaskMemUTF8(saveDirectory);

            _setEnvironment(_environmentCallback);
            _setVideoRefresh(_videoRefreshCallback);
            _setAudioSample(_audioSampleCallback);
            _setAudioSampleBatch(_audioSampleBatchCallback);
            _setInputPoll(_inputPollCallback);
            _setInputState(_inputStateCallback);

            _getSystemInfo(out var nativeInfo);
            LibraryName = Utf8(nativeInfo.LibraryName);
            Version = Utf8(nativeInfo.LibraryVersion);
            _validExtensions = ParseExtensions(Utf8(nativeInfo.ValidExtensions));
            if (!nativeInfo.BlockExtract)
                _validExtensions = _validExtensions.Where(extension => extension is not ".zip" and not ".7z").ToArray();
            _needsFullPath = nativeInfo.NeedFullPath;
            _systemIds = MapSystemIds(LibraryName, Path.GetFileNameWithoutExtension(_nativeCorePath));

            if (initializeCore)
            {
                _init();
                _initialized = true;
                UpdateAvInfo();
            }
        }
        catch
        {
            ReleaseNativeResources();
            throw;
        }
    }

    public string LibraryName { get; } = "";
    public string Version { get; } = "";
    public IReadOnlyList<string> ValidExtensions => _validExtensions;
    public IReadOnlyList<string> SystemIds => _systemIds;
    public override int FrameWidth => _width;
    public override int FrameHeight => _height;
    public override int AudioSampleRate => Math.Clamp((int)Math.Round(_sampleRate), 8000, 192000);
    public override CoreRenderMode RenderMode => _usesHardwareRender ? CoreRenderMode.OpenGL : CoreRenderMode.Framebuffer;
    protected override string CoreId => "libretro-" + Sanitize(Path.GetFileNameWithoutExtension(_nativeCorePath));

    public static LibretroCoreInfo Inspect(string nativeCorePath)
    {
        using var core = new LibretroBridgeCore(nativeCorePath, initializeCore: false);
        return new LibretroCoreInfo(core.LibraryName, core.Version, core._validExtensions, core._systemIds, core._needsFullPath);
    }

    protected override void LoadCoreRom(string romPath)
    {
        if (_gameLoaded || _romMap is not null || _gamePathPointer != 0)
            ReleaseRomData();

        if (!_needsFullPath)
        {
            _romMap = MemoryMappedFile.CreateFromFile(romPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            _romView = _romMap.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            _romView.SafeMemoryMappedViewHandle.AcquirePointer(ref _romMapPointer);
            _romMapPointerAcquired = true;
            _romMapPointer += _romView.PointerOffset;
            _romLength = (nuint)new FileInfo(romPath).Length;
        }
        else
        {
            _gamePathPointer = Marshal.StringToCoTaskMemUTF8(romPath);
            _romLength = (nuint)new FileInfo(romPath).Length;
        }

        var game = new RetroGameInfo
        {
            Path = _gamePathPointer,
            Data = _needsFullPath ? 0 : (nint)_romMapPointer,
            Size = _needsFullPath ? 0 : _romLength,
            Meta = 0
        };

        if (!_loadGame(ref game))
        {
            ReleaseRomData();
            throw new InvalidDataException($"O núcleo {LibraryName} recusou a ROM.");
        }

        _gameLoaded = true;
        _setControllerPortDevice?.Invoke(0, DeviceJoypad);
        UpdateAvInfo();
        _coreHwContextReset?.Invoke();
        Array.Fill(_video, 0xFF000000u);
    }

    public override void RunFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_gameLoaded) throw new InvalidOperationException("Carregue uma ROM antes de executar o núcleo libretro.");
        _audioScratch.Clear();
        _shutdownMessage = null;
        _run();
        _audio = _audioScratch.ToArray();
    }

    public override ReadOnlyMemory<uint> GetFrameBuffer() => _video;
    public override ReadOnlyMemory<short> GetAudioBuffer() => _audio;
    public override void SetInput(EmulatorInput input) => _input = input;

    protected override void WriteCoreState(BinaryWriter writer)
    {
        if (_serializeSize is null || _serialize is null)
            throw new NotSupportedException($"{LibraryName} não oferece save states libretro.");
        var size = _serializeSize().ToUInt64();
        if (size == 0 || size > MaxSerializeBytes)
            throw new InvalidDataException($"Tamanho de save state fora do limite: {size} bytes.");
        var bytes = new byte[(int)size];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            if (!_serialize(handle.AddrOfPinnedObject(), (nuint)size))
                throw new IOException($"{LibraryName} não conseguiu serializar o estado.");
        }
        finally { handle.Free(); }
        writer.Write((int)size);
        writer.Write(bytes);
    }

    protected override void ReadCoreState(BinaryReader reader)
    {
        if (_unserialize is null) throw new NotSupportedException($"{LibraryName} não oferece load state libretro.");
        var size = reader.ReadInt32();
        if (size <= 0 || size > MaxSerializeBytes) throw new InvalidDataException("Tamanho de estado libretro inválido.");
        var bytes = reader.ReadBytes(size);
        if (bytes.Length != size) throw new InvalidDataException("Save state libretro truncado.");
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            if (!_unserialize(handle.AddrOfPinnedObject(), (nuint)size))
                throw new InvalidDataException($"{LibraryName} recusou o save state.");
        }
        finally { handle.Free(); }
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseNativeResources();
        base.Dispose();
    }

    private void UpdateAvInfo()
    {
        _getSystemAvInfo(out var av);
        if (av.Geometry.BaseWidth is > 0 and <= MaxVideoDimension) _width = (int)av.Geometry.BaseWidth;
        if (av.Geometry.BaseHeight is > 0 and <= MaxVideoDimension) _height = (int)av.Geometry.BaseHeight;
        if (av.Timing.SampleRate is >= 8000 and <= 192000) _sampleRate = av.Timing.SampleRate;
        EnsureVideoBuffer(_width, _height);
    }

    private bool EnvironmentCallback(uint command, nint data)
    {
        try
        {
            switch (command)
            {
                case EnvironmentGetOverscan:
                    Marshal.WriteByte(data, 0);
                    return true;
                case EnvironmentGetCanDupe:
                    Marshal.WriteByte(data, 1);
                    return true;
                case EnvironmentSetMessage:
                case EnvironmentSetInputDescriptors:
                case EnvironmentSetSupportNoGame:
                    return true;
                case EnvironmentShutdown:
                    _shutdownMessage = "O núcleo solicitou desligamento.";
                    return true;
                case EnvironmentGetSystemDirectory:
                    Marshal.WriteIntPtr(data, _systemDirectoryPointer);
                    return true;
                case EnvironmentGetSaveDirectory:
                    Marshal.WriteIntPtr(data, _saveDirectoryPointer);
                    return true;
                case EnvironmentSetPixelFormat:
                    var format = Marshal.ReadInt32(data);
                    if (format is < PixelFormat0Rgb1555 or > PixelFormatRgb565) return false;
                    _pixelFormat = format;
                    return true;
                case EnvironmentSetHwRender:
                    return ConfigureHardwareRender(data);
                case EnvironmentGetVariable:
                case EnvironmentSetVariables:
                    return false;
                case EnvironmentGetVariableUpdate:
                    Marshal.WriteByte(data, 0);
                    return true;
                case EnvironmentSetSystemAvInfo:
                    var av = Marshal.PtrToStructure<RetroSystemAvInfo>(data);
                    if (av.Geometry.BaseWidth is > 0 and <= MaxVideoDimension) _width = (int)av.Geometry.BaseWidth;
                    if (av.Geometry.BaseHeight is > 0 and <= MaxVideoDimension) _height = (int)av.Geometry.BaseHeight;
                    if (av.Timing.SampleRate is >= 8000 and <= 192000) _sampleRate = av.Timing.SampleRate;
                    EnsureVideoBuffer(_width, _height);
                    return true;
                case EnvironmentSetGeometry:
                    var geometry = Marshal.PtrToStructure<RetroGameGeometry>(data);
                    if (geometry.BaseWidth is > 0 and <= MaxVideoDimension) _width = (int)geometry.BaseWidth;
                    if (geometry.BaseHeight is > 0 and <= MaxVideoDimension) _height = (int)geometry.BaseHeight;
                    EnsureVideoBuffer(_width, _height);
                    return true;
                case EnvironmentGetLanguage:
                    Marshal.WriteInt32(data, 0);
                    return true;
                default:
                    return false;
            }
        }
        catch
        {
            return false; // Never allow a managed exception to unwind through native code.
        }
    }

    private void VideoRefreshCallback(nint data, uint width, uint height, nuint pitch)
    {
        try
        {
            if (data == 0 || data == -1) return; // Frame duping or a hardware-render framebuffer sentinel.
            if (width == 0 || height == 0 || width > MaxVideoDimension || height > MaxVideoDimension) return;
            _width = (int)width;
            _height = (int)height;
            EnsureVideoBuffer(_width, _height);
            var bytesPerPixel = _pixelFormat == PixelFormatXrgb8888 ? 4 : 2;
            if (pitch < (nuint)width * (nuint)bytesPerPixel) return;

            for (var y = 0; y < _height; y++)
            {
                var row = data + checked((int)(pitch * (nuint)y));
                for (var x = 0; x < _width; x++)
                {
                    _video[y * _width + x] = _pixelFormat == PixelFormatXrgb8888
                        ? ConvertXrgb8888(Marshal.ReadInt32(row, x * 4))
                        : ConvertRgb16(unchecked((ushort)Marshal.ReadInt16(row, x * 2)), _pixelFormat);
                }
            }
        }
        catch { /* Native callback boundary: ignore malformed frames rather than crash the frontend. */ }
    }

    private void AudioSampleCallback(short left, short right)
    {
        _audioScratch.Add(left);
        _audioScratch.Add(right);
    }

    private nuint AudioSampleBatchCallback(nint data, nuint frameCount)
    {
        var count = frameCount > (nuint)1_000_000 ? 1_000_000 : (int)frameCount;
        for (var i = 0; i < count; i++)
        {
            _audioScratch.Add(Marshal.ReadInt16(data, i * 4));
            _audioScratch.Add(Marshal.ReadInt16(data, i * 4 + 2));
        }
        return (nuint)count;
    }

    private void InputPollCallback() { }

    private bool ConfigureHardwareRender(nint data)
    {
        if (data == 0) return false;
        var callback = Marshal.PtrToStructure<RetroHwRenderCallback>(data);
        // Raylib currently owns a desktop OpenGL context. GLES, Vulkan, and D3D requests are declined.
        if (callback.ContextType is not (1 or 3)) return false;

        if (callback.ContextReset != 0)
            _coreHwContextReset = Marshal.GetDelegateForFunctionPointer<RetroHwContextReset>(callback.ContextReset);
        if (callback.ContextDestroy != 0)
            _coreHwContextDestroy = Marshal.GetDelegateForFunctionPointer<RetroHwContextDestroy>(callback.ContextDestroy);
        callback.GetCurrentFramebuffer = Marshal.GetFunctionPointerForDelegate(_hwGetCurrentFramebufferCallback);
        callback.GetProcAddress = Marshal.GetFunctionPointerForDelegate(_hwGetProcAddressCallback);
        Marshal.StructureToPtr(callback, data, false);
        _usesHardwareRender = true;
        return true;
    }

    private static nuint HwGetCurrentFramebufferCallback() => 0;

    private static nint HwGetProcAddressCallback(nint symbol)
    {
        var name = Utf8(symbol);
        if (string.IsNullOrWhiteSpace(name)) return 0;
        var address = NativeMethods.WglGetProcAddress(name);
        var numeric = address.ToInt64();
        if (address != 0 && numeric is not (1 or 2 or 3 or -1)) return address;
        var library = NativeMethods.GetModuleHandle("opengl32.dll");
        return library == 0 ? 0 : NativeMethods.GetProcAddress(library, name);
    }

    private short InputStateCallback(uint port, uint device, uint index, uint id)
    {
        if (port != 0 || (device & 0xFF) != DeviceJoypad) return 0;
        var button = id switch
        {
            0 => EmulatorInput.B,
            2 => EmulatorInput.Select,
            3 => EmulatorInput.Start,
            4 => EmulatorInput.Up,
            5 => EmulatorInput.Down,
            6 => EmulatorInput.Left,
            7 => EmulatorInput.Right,
            8 => EmulatorInput.A,
            10 => EmulatorInput.L,
            11 => EmulatorInput.R,
            _ => EmulatorInput.None
        };
        return (_input & button) != 0 ? (short)1 : (short)0;
    }

    private void EnsureVideoBuffer(int width, int height)
    {
        var size = checked(width * height);
        if (_video.Length != size) _video = new uint[size];
    }

    private static uint ConvertXrgb8888(int pixel)
    {
        var r = (uint)((pixel >> 16) & 0xFF);
        var g = (uint)((pixel >> 8) & 0xFF);
        var b = (uint)(pixel & 0xFF);
        return 0xFF000000u | (b << 16) | (g << 8) | r;
    }

    private static uint ConvertRgb16(ushort pixel, int format)
    {
        int r, g, b;
        if (format == PixelFormatRgb565)
        {
            r = (pixel >> 11) & 31;
            g = (pixel >> 5) & 63;
            b = pixel & 31;
            g = (g << 2) | (g >> 4);
        }
        else
        {
            r = (pixel >> 10) & 31;
            g = (pixel >> 5) & 31;
            b = pixel & 31;
        }
        r = (r << 3) | (r >> 2);
        g = format == PixelFormatRgb565 ? g : (g << 3) | (g >> 2);
        b = (b << 3) | (b >> 2);
        return 0xFF000000u | ((uint)b << 16) | ((uint)g << 8) | (uint)r;
    }

    private T GetRequired<T>(string export) where T : Delegate
    {
        var pointer = NativeMethods.GetProcAddress(_module, export);
        if (pointer == 0) throw new EntryPointNotFoundException($"A DLL não exporta {export}.");
        return Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }

    private T? GetOptional<T>(string export) where T : Delegate
    {
        var pointer = NativeMethods.GetProcAddress(_module, export);
        return pointer == 0 ? null : Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }

    private void ReleaseRomData()
    {
        if (_gameLoaded)
        {
            _unloadGame();
            _gameLoaded = false;
        }
        if (_romMapPointerAcquired && _romView is not null)
        {
            _romMapPointer -= _romView.PointerOffset;
            _romView.SafeMemoryMappedViewHandle.ReleasePointer();
            _romMapPointerAcquired = false;
        }
        _romView?.Dispose();
        _romView = null;
        _romMap?.Dispose();
        _romMap = null;
        if (_gamePathPointer != 0) Marshal.FreeCoTaskMem(_gamePathPointer);
        _gamePathPointer = 0;
        _romLength = 0;
    }

    private void ReleaseNativeResources()
    {
        try
        {
            ReleaseRomData();
            _coreHwContextDestroy?.Invoke();
        }
        catch { }
        if (_initialized)
        {
            try { _deinit(); } catch { }
            _initialized = false;
        }
        if (_systemDirectoryPointer != 0) Marshal.FreeCoTaskMem(_systemDirectoryPointer);
        if (_saveDirectoryPointer != 0) Marshal.FreeCoTaskMem(_saveDirectoryPointer);
        _systemDirectoryPointer = _saveDirectoryPointer = 0;
        if (_module != 0) NativeMethods.FreeLibrary(_module);
    }

    private static string Utf8(nint pointer) => pointer == 0 ? "" : Marshal.PtrToStringUTF8(pointer) ?? "";

    private static string[] ParseExtensions(string extensions) => extensions
        .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(extension => extension.StartsWith('.') ? extension : "." + extension)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string[] MapSystemIds(string libraryName, string dllName)
    {
        var value = (libraryName + " " + dllName).Replace("_libretro", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        if (value.Contains("mgba")) return ["GBA", "GB", "GBC"];
        if (value.Contains("dolphin")) return ["GameCube", "Wii"];
        if (value.Contains("pcsx2")) return ["PS2"];
        if (value.Contains("snes9x")) return ["SNES"];
        if (value.Contains("genesis plus gx")) return ["Genesis", "MegaDrive", "MasterSystem", "GameGear"];
        if (value.Contains("picodrive")) return ["Genesis", "MegaDrive", "32X"];
        if (value.Contains("melonds")) return ["DS"];
        if (value.Contains("desmume")) return ["DS"];
        if (value.Contains("mupen64plus") || value.Contains("parallel-n64")) return ["N64"];
        if (value.Contains("beetle psx") || value.Contains("mednafen psx")) return ["PS1"];
        return [string.IsNullOrWhiteSpace(libraryName) ? dllName : libraryName];
    }

    private static string Sanitize(string name)
    {
        var chars = name.Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-').ToArray();
        return new string(chars).Trim('-');
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RetroSystemInfo
    {
        public nint LibraryName;
        public nint LibraryVersion;
        public nint ValidExtensions;
        [MarshalAs(UnmanagedType.I1)] public bool NeedFullPath;
        [MarshalAs(UnmanagedType.I1)] public bool BlockExtract;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RetroGameInfo
    {
        public nint Path;
        public nint Data;
        public nuint Size;
        public nint Meta;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RetroGameGeometry
    {
        public uint BaseWidth;
        public uint BaseHeight;
        public uint MaxWidth;
        public uint MaxHeight;
        public float AspectRatio;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RetroSystemTiming
    {
        public double Fps;
        public double SampleRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RetroSystemAvInfo
    {
        public RetroGameGeometry Geometry;
        public RetroSystemTiming Timing;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RetroHwRenderCallback
    {
        public uint ContextType;
        public nint ContextReset;
        public nint GetCurrentFramebuffer;
        public nint GetProcAddress;
        [MarshalAs(UnmanagedType.I1)] public bool Depth;
        [MarshalAs(UnmanagedType.I1)] public bool Stencil;
        [MarshalAs(UnmanagedType.I1)] public bool BottomLeftOrigin;
        public uint VersionMajor;
        public uint VersionMinor;
        [MarshalAs(UnmanagedType.I1)] public bool CacheContext;
        public nint ContextDestroy;
        [MarshalAs(UnmanagedType.I1)] public bool DebugContext;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint RetroApiVersion();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool RetroEnvironmentCallback(uint command, nint data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroSetEnvironment(RetroEnvironmentCallback callback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroVideoRefresh(nint data, uint width, uint height, nuint pitch);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroSetVideoRefresh(RetroVideoRefresh callback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroAudioSample(short left, short right);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroSetAudioSample(RetroAudioSample callback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint RetroAudioSampleBatch(nint data, nuint frameCount);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroSetAudioSampleBatch(RetroAudioSampleBatch callback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroInputPoll();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroSetInputPoll(RetroInputPoll callback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate short RetroInputState(uint port, uint device, uint index, uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroSetInputState(RetroInputState callback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroInit();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroDeinit();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroGetSystemInfo(out RetroSystemInfo info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroGetSystemAvInfo(out RetroSystemAvInfo info);
    [return: MarshalAs(UnmanagedType.I1)]
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool RetroLoadGame(ref RetroGameInfo game);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroRun();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroUnloadGame();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroSetControllerPortDevice(uint port, uint device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint RetroSerializeSize();
    [return: MarshalAs(UnmanagedType.I1)]
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool RetroSerialize(nint data, nuint size);
    [return: MarshalAs(UnmanagedType.I1)]
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool RetroUnserialize(nint data, nuint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroReset();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroHwContextReset();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RetroHwContextDestroy();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint RetroHwGetCurrentFramebuffer();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint RetroHwGetProcAddress(nint symbol);

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "LoadLibraryW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern nint LoadLibrary(string fileName);

        [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        internal static extern nint GetProcAddress(nint module, string procName);

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern nint GetModuleHandle(string moduleName);

        [DllImport("opengl32.dll", EntryPoint = "wglGetProcAddress", CharSet = CharSet.Ansi, ExactSpelling = true)]
        internal static extern nint WglGetProcAddress(string procName);

        [DllImport("kernel32.dll", EntryPoint = "FreeLibrary", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FreeLibrary(nint module);
    }
}

public sealed record LibretroCoreInfo(
    string LibraryName,
    string Version,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> SystemIds,
    bool NeedFullPath);
