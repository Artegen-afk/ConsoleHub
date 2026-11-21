namespace ConsoleHub;

/// <summary>Contract between the launcher and independently compiled emulator cores.</summary>
public interface ICoreInterface : IDisposable
{
    int FrameWidth { get; }
    int FrameHeight { get; }
    int AudioSampleRate { get; }
    string StatusText { get; }
    CoreRenderMode RenderMode { get; }
    void LoadRom(string romPath);
    void RunFrame();
    ReadOnlyMemory<uint> GetFrameBuffer();
    ReadOnlyMemory<short> GetAudioBuffer();
    void SetInput(EmulatorInput input);
    void SaveState(int slot);
    void LoadState(int slot);
    /// <summary>Invoked inside the host's active graphics frame/context.</summary>
    void Render(GraphicsContext context);
}

public enum CoreRenderMode
{
    Framebuffer,
    OpenGL,
    Vulkan
}

/// <summary>Native graphics handles valid only for the duration of a core Render call.</summary>
public sealed class GraphicsContext
{
    public GraphicsContext(
        CoreRenderMode api,
        nint nativeWindowHandle = 0,
        nint nativeContextHandle = 0,
        Func<string, nint>? getProcAddress = null,
        nint instance = 0,
        nint physicalDevice = 0,
        nint device = 0,
        nint queue = 0,
        uint queueFamilyIndex = 0,
        nint commandBuffer = 0,
        nint renderPass = 0,
        nint framebuffer = 0,
        uint imageIndex = 0,
        nint surface = 0,
        nint swapchain = 0,
        int width = 0,
        int height = 0)
    {
        Api = api;
        NativeWindowHandle = nativeWindowHandle;
        NativeContextHandle = nativeContextHandle;
        GetProcAddress = getProcAddress;
        Instance = instance;
        PhysicalDevice = physicalDevice;
        Device = device;
        Queue = queue;
        QueueFamilyIndex = queueFamilyIndex;
        CommandBuffer = commandBuffer;
        RenderPass = renderPass;
        Framebuffer = framebuffer;
        ImageIndex = imageIndex;
        Surface = surface;
        Swapchain = swapchain;
        Width = width;
        Height = height;
    }

    public CoreRenderMode Api { get; }
    public nint NativeWindowHandle { get; }
    public nint NativeContextHandle { get; }
    public Func<string, nint>? GetProcAddress { get; }
    public nint Instance { get; }
    public nint PhysicalDevice { get; }
    public nint Device { get; }
    public nint Queue { get; }
    public uint QueueFamilyIndex { get; }
    public nint CommandBuffer { get; }
    public nint RenderPass { get; }
    public nint Framebuffer { get; }
    public uint ImageIndex { get; }
    public nint Surface { get; }
    public nint Swapchain { get; }
    public int Width { get; }
    public int Height { get; }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class CorePluginAttribute : Attribute
{
    public CorePluginAttribute(string systemId, params string[] extensions)
    {
        SystemId = systemId;
        Extensions = extensions.Select(extension => extension.StartsWith('.') ? extension : "." + extension).ToArray();
    }

    public string SystemId { get; }
    public IReadOnlyList<string> Extensions { get; }
}

[Flags]
public enum EmulatorInput : ushort
{
    None = 0,
    A = 1 << 0,
    B = 1 << 1,
    Select = 1 << 2,
    Start = 1 << 3,
    Right = 1 << 4,
    Left = 1 << 5,
    Up = 1 << 6,
    Down = 1 << 7,
    R = 1 << 8,
    L = 1 << 9
}
