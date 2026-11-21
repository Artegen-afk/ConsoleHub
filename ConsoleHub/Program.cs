using System.Diagnostics;
using System.Runtime.InteropServices;
using Raylib_cs;
using ConsoleHub.Gba;

namespace ConsoleHub;

internal static class Program
{
    private const int ScreenWidth = 1280;
    private const int ScreenHeight = 720;
    private const int Gamepad = 0;
    private static readonly string[] RomExtensions =
    [
        ".gba", ".gbc", ".gb", ".nds", ".3ds", ".cci", ".cia", ".vpk",
        ".n64", ".z64", ".v64", ".gcm", ".gcz", ".iso", ".wbfs", ".wad",
        ".rvz", ".wia", ".ciso", ".wud", ".wux", ".xci", ".nsp", ".cue", ".chd", ".pbp", ".bin", ".cso",
        ".sfc", ".smc", ".nes", ".md", ".gen"
    ];

    private static readonly Color Background = new(14, 17, 25, 255);
    private static readonly Color Panel = new(26, 31, 44, 255);
    private static readonly Color Selected = new(35, 112, 201, 255);
    private static readonly Color Text = new(238, 242, 250, 255);
    private static readonly Color Muted = new(149, 160, 179, 255);

    private static string _root = Directory.GetCurrentDirectory();
    private static CorePluginRegistry _corePlugins = new();
    private static List<SystemTab> _tabs = [];
    private static int _tabIndex;
    private static int _itemIndex;
    private static string _message = "Setas / D-pad navegam  •  Enter / A seleciona";
    private static DateTime _lastScan;
    private static ICoreInterface? _activeCore;
    private static Texture2D _gameTexture;
    private static int _textureWidth;
    private static int _textureHeight;
    private static AudioStream _audioStream;
    private static bool _audioDeviceReady;
    private static bool _audioReady;
    private static int _audioStreamRate;
    private static readonly Queue<short> PendingAudio = new();
    private static string _activeRomName = "";
    private static string _gameNotice = "";

    [STAThread]
    private static void Main()
    {
        _root = FindProjectRoot(Directory.GetCurrentDirectory());
        Directory.CreateDirectory(Path.Combine(_root, "roms"));
        Directory.CreateDirectory(Path.Combine(_root, "ports"));
        _corePlugins.LoadFrom(Path.Combine(_root, "cores"));
        _message = _corePlugins.Errors.Count == 0
            ? $"Núcleos carregados: {_corePlugins.Count}."
            : $"Falha ao carregar plugin: {_corePlugins.Errors[0]}";
        RefreshLibrary();

        Raylib.SetConfigFlags(ConfigFlags.FLAG_WINDOW_RESIZABLE | ConfigFlags.FLAG_VSYNC_HINT);
        Raylib.InitWindow(ScreenWidth, ScreenHeight, "ConsoleHub | Multi-console launcher");
        Raylib.SetTargetFPS(60);
        Raylib.InitAudioDevice();
        _audioDeviceReady = Raylib.IsAudioDeviceReady();
        if (_audioDeviceReady)
            Raylib.SetAudioStreamBufferSizeDefault(1024);

        while (!Raylib.WindowShouldClose())
        {
            if ((DateTime.UtcNow - _lastScan).TotalSeconds >= 2)
                RefreshLibrary();

            if (_activeCore is null)
            {
                HandleInput();
                Draw();
            }
            else
            {
                HandleGameInput();
                if (_activeCore is not null)
                {
                    if (_activeCore.RenderMode == CoreRenderMode.Framebuffer)
                    {
                        _activeCore.RunFrame();
                        EnsureFramebufferTexture(_activeCore.FrameWidth, _activeCore.FrameHeight);
                        Raylib.UpdateTexture(_gameTexture, _activeCore.GetFrameBuffer().Span);
                        QueueCoreAudio(_activeCore.GetAudioBuffer());
                    }
                    DrawGame();
                }
                else
                {
                    Draw();
                }
            }
        }

        _activeCore?.Dispose();
        if (_gameTexture.Id != 0) Raylib.UnloadTexture(_gameTexture);
        if (_audioReady) Raylib.UnloadAudioStream(_audioStream);
        if (_audioDeviceReady) Raylib.CloseAudioDevice();
        Raylib.CloseWindow();
    }

    private static string FindProjectRoot(string startingDirectory)
    {
        var current = new DirectoryInfo(startingDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ConsoleHub.csproj")))
                return current.FullName;
            if (File.Exists(Path.Combine(current.FullName, "ConsoleHub", "ConsoleHub.csproj")))
                return Path.Combine(current.FullName, "ConsoleHub");
            current = current.Parent;
        }

        return startingDirectory;
    }

    private static void RefreshLibrary()
    {
        var previousTab = _tabs.Count > 0 ? _tabs[Math.Clamp(_tabIndex, 0, _tabs.Count - 1)].Name : "Ports";
        var romsPath = Path.Combine(_root, "roms");
        var portsPath = Path.Combine(_root, "ports");

        var systems = new List<SystemTab>();
        foreach (var folder in SafeDirectories(romsPath).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var games = SafeFiles(folder)
                .Where(IsRom)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(path => new LibraryItem(Path.GetFileName(path), path))
                .ToList();

            if (games.Count > 0)
                systems.Add(new SystemTab(Path.GetFileName(folder), games, false));
        }

        var ports = SafeDirectories(portsPath)
            .SelectMany(SafeFiles)
            .Where(path => string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetRelativePath(portsPath, path), StringComparer.OrdinalIgnoreCase)
            .Select(path => new LibraryItem(Path.GetRelativePath(portsPath, path), path))
            .ToList();

        systems.Add(new SystemTab("Ports", ports, true));
        _tabs = systems;
        _tabIndex = Math.Max(0, _tabs.FindIndex(tab => tab.Name.Equals(previousTab, StringComparison.OrdinalIgnoreCase)));
        _itemIndex = Math.Clamp(_itemIndex, 0, Math.Max(0, CurrentTab.Items.Count - 1));
        _lastScan = DateTime.UtcNow;
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path).ToArray(); }
        catch { return []; }
    }

    private static IEnumerable<string> SafeFiles(string path)
    {
        try { return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToArray(); }
        catch { return []; }
    }

    private static bool IsRom(string path)
    {
        var extension = Path.GetExtension(path);
        return RomExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ||
               _corePlugins.SupportsExtension(extension);
    }

    private static SystemTab CurrentTab => _tabs[_tabIndex];

    private static void HandleInput()
    {
        if (Pressed(KeyboardKey.KEY_RIGHT, GamepadButton.GAMEPAD_BUTTON_LEFT_FACE_RIGHT))
        {
            _tabIndex = (_tabIndex + 1) % _tabs.Count;
            _itemIndex = 0;
        }
        else if (Pressed(KeyboardKey.KEY_LEFT, GamepadButton.GAMEPAD_BUTTON_LEFT_FACE_LEFT))
        {
            _tabIndex = (_tabIndex - 1 + _tabs.Count) % _tabs.Count;
            _itemIndex = 0;
        }
        else if (Pressed(KeyboardKey.KEY_DOWN, GamepadButton.GAMEPAD_BUTTON_LEFT_FACE_DOWN))
        {
            _itemIndex = Math.Min(_itemIndex + 1, Math.Max(0, CurrentTab.Items.Count - 1));
        }
        else if (Pressed(KeyboardKey.KEY_UP, GamepadButton.GAMEPAD_BUTTON_LEFT_FACE_UP))
        {
            _itemIndex = Math.Max(0, _itemIndex - 1);
        }
        else if (Pressed(KeyboardKey.KEY_ENTER, GamepadButton.GAMEPAD_BUTTON_RIGHT_FACE_DOWN))
        {
            ActivateSelection();
        }
        else if (Raylib.IsKeyPressed(KeyboardKey.KEY_F5))
        {
            RefreshLibrary();
            _message = "Biblioteca atualizada.";
        }
    }

    private static bool Pressed(KeyboardKey key, GamepadButton button) =>
        Raylib.IsKeyPressed(key) ||
        (Raylib.IsGamepadAvailable(Gamepad) && Raylib.IsGamepadButtonPressed(Gamepad, button));

    private static void ActivateSelection()
    {
        if (CurrentTab.Items.Count == 0)
        {
            _message = CurrentTab.IsPorts
                ? "Adicione jogos .exe em subpastas de /ports/."
                : "Adicione arquivos de jogo à pasta desta plataforma em /roms/.";
            return;
        }

        var selected = CurrentTab.Items[_itemIndex];
        if (CurrentTab.IsPorts)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = selected.FullPath,
                    WorkingDirectory = Path.GetDirectoryName(selected.FullPath)!,
                    UseShellExecute = true
                });
                _message = $"Iniciado: {selected.Name}";
            }
            catch (Exception ex)
            {
                _message = $"Não foi possível abrir o port: {ex.Message}";
            }
        }
        else
        {
            StartCore(selected);
        }
    }

    private static void StartCore(LibraryItem selected)
    {
        ICoreInterface? core = null;
        try
        {
            core = _corePlugins.CreateFor(CurrentTab.Name, selected.FullPath);
            if (core is null)
            {
                _message = $"Nenhum núcleo carregado para {CurrentTab.Name} ({Path.GetExtension(selected.FullPath)}).";
                return;
            }

            if (core.RenderMode == CoreRenderMode.Vulkan)
                throw new NotSupportedException("O backend atual da janela Raylib fornece OpenGL. Vulkan está previsto no contrato de plugin, mas ainda precisa de um backend de janela Vulkan.");
            core.LoadRom(selected.FullPath);

            if (core.RenderMode == CoreRenderMode.Framebuffer)
            {
                EnsureFramebufferTexture(core.FrameWidth, core.FrameHeight);
            }
            else if (_gameTexture.Id != 0)
            {
                Raylib.UnloadTexture(_gameTexture);
                _gameTexture = default;
            }
            _activeCore = core;
            core = null;
            _activeRomName = selected.Name;
            _gameNotice = "";
            PendingAudio.Clear();
            ConfigureAudioStream(_activeCore.AudioSampleRate);
        }
        catch (Exception ex)
        {
            core?.Dispose();
            _message = $"Falha ao carregar núcleo: {ex.Message}";
        }
    }

    private static void HandleGameInput()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.KEY_ESCAPE))
        {
            _activeCore?.Dispose();
            _activeCore = null;
            _message = "Emulação pausada. Selecione outro jogo ou pressione Enter para voltar.";
            if (_audioReady) Raylib.PauseAudioStream(_audioStream);
            return;
        }

        if (Raylib.IsKeyPressed(KeyboardKey.KEY_F5))
        {
            try { _activeCore!.SaveState(1); _gameNotice = "Save state salvo no slot 1."; }
            catch (Exception ex) { _gameNotice = $"Falha ao salvar: {ex.Message}"; }
        }
        if (Raylib.IsKeyPressed(KeyboardKey.KEY_F8))
        {
            try
            {
                _activeCore!.LoadState(1);
                PendingAudio.Clear();
                _gameNotice = "Save state carregado do slot 1.";
            }
            catch (Exception ex) { _gameNotice = $"Falha ao carregar: {ex.Message}"; }
        }

        var input = EmulatorInput.None;
        if (Raylib.IsKeyDown(KeyboardKey.KEY_X) || GamepadDown(GamepadButton.GAMEPAD_BUTTON_RIGHT_FACE_DOWN)) input |= EmulatorInput.A;
        if (Raylib.IsKeyDown(KeyboardKey.KEY_Z) || GamepadDown(GamepadButton.GAMEPAD_BUTTON_RIGHT_FACE_RIGHT)) input |= EmulatorInput.B;
        if (Raylib.IsKeyDown(KeyboardKey.KEY_BACKSPACE) || GamepadDown(GamepadButton.GAMEPAD_BUTTON_MIDDLE_LEFT)) input |= EmulatorInput.Select;
        if (Raylib.IsKeyDown(KeyboardKey.KEY_ENTER) || GamepadDown(GamepadButton.GAMEPAD_BUTTON_MIDDLE_RIGHT)) input |= EmulatorInput.Start;
        if (Raylib.IsKeyDown(KeyboardKey.KEY_RIGHT) || GamepadDown(GamepadButton.GAMEPAD_BUTTON_LEFT_FACE_RIGHT)) input |= EmulatorInput.Right;
        if (Raylib.IsKeyDown(KeyboardKey.KEY_LEFT) || GamepadDown(GamepadButton.GAMEPAD_BUTTON_LEFT_FACE_LEFT)) input |= EmulatorInput.Left;
        if (Raylib.IsKeyDown(KeyboardKey.KEY_UP) || GamepadDown(GamepadButton.GAMEPAD_BUTTON_LEFT_FACE_UP)) input |= EmulatorInput.Up;
        if (Raylib.IsKeyDown(KeyboardKey.KEY_DOWN) || GamepadDown(GamepadButton.GAMEPAD_BUTTON_LEFT_FACE_DOWN)) input |= EmulatorInput.Down;
        if (Raylib.IsKeyDown(KeyboardKey.KEY_S) || GamepadDown(GamepadButton.GAMEPAD_BUTTON_LEFT_TRIGGER_1)) input |= EmulatorInput.L;
        if (Raylib.IsKeyDown(KeyboardKey.KEY_A) || GamepadDown(GamepadButton.GAMEPAD_BUTTON_RIGHT_TRIGGER_1)) input |= EmulatorInput.R;
        _activeCore!.SetInput(input);
    }

    private static bool GamepadDown(GamepadButton button) =>
        Raylib.IsGamepadAvailable(Gamepad) && Raylib.IsGamepadButtonDown(Gamepad, button);

    private static void EnsureFramebufferTexture(int width, int height)
    {
        if (_gameTexture.Id != 0 && width == _textureWidth && height == _textureHeight) return;
        if (_gameTexture.Id != 0) Raylib.UnloadTexture(_gameTexture);
        var image = Raylib.GenImageColor(width, height, Color.Black);
        _gameTexture = Raylib.LoadTextureFromImage(image);
        Raylib.UnloadImage(image);
        Raylib.SetTextureFilter(_gameTexture, TextureFilter.TEXTURE_FILTER_POINT);
        _textureWidth = width;
        _textureHeight = height;
    }

    private static void ConfigureAudioStream(int sampleRate)
    {
        if (!_audioDeviceReady) return;
        sampleRate = Math.Clamp(sampleRate, 8000, 192000);
        if (_audioReady && _audioStreamRate == sampleRate)
        {
            Raylib.ResumeAudioStream(_audioStream);
            return;
        }

        if (_audioReady) Raylib.UnloadAudioStream(_audioStream);
        Raylib.SetAudioStreamBufferSizeDefault(1024);
        _audioStream = Raylib.LoadAudioStream((uint)sampleRate, 16, 2);
        _audioReady = Raylib.IsAudioStreamValid(_audioStream);
        _audioStreamRate = _audioReady ? sampleRate : 0;
        if (_audioReady) Raylib.PlayAudioStream(_audioStream);
    }

    private static unsafe void QueueCoreAudio(ReadOnlyMemory<short> audio)
    {
        if (!_audioReady || audio.IsEmpty) return;
        foreach (var sample in audio.Span) PendingAudio.Enqueue(sample);
        if (!Raylib.IsAudioStreamProcessed(_audioStream) || PendingAudio.Count < 2) return;

        var frameCount = Math.Min(PendingAudio.Count / 2, 1024);
        var samples = new short[frameCount * 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = PendingAudio.Dequeue();
        fixed (short* sampleData = samples)
            Raylib.UpdateAudioStream(_audioStream, sampleData, frameCount);
    }

    private static void Draw()
    {
        var width = Raylib.GetScreenWidth();
        var height = Raylib.GetScreenHeight();
        Raylib.BeginDrawing();
        Raylib.ClearBackground(Background);

        Raylib.DrawText("CONSOLE HUB", 48, 34, 22, Text);
        Raylib.DrawText("BIBLIOTECA", 50, 112, 14, Muted);
        DrawTabs(width);
        DrawLibrary(width, height);
        Raylib.DrawText(_message, 50, height - 64, 16, Muted);
        Raylib.DrawText("F5  Atualizar", width - 170, height - 64, 15, Muted);

        Raylib.EndDrawing();
    }

    private static void DrawGame()
    {
        var width = Raylib.GetScreenWidth();
        var height = Raylib.GetScreenHeight();
        Raylib.BeginDrawing();
        Raylib.ClearBackground(new Color(8, 10, 15, 255));

        var scale = MathF.Min((width - 96f) / _activeCore!.FrameWidth, (height - 170f) / _activeCore.FrameHeight);
        var drawWidth = _activeCore.FrameWidth * scale;
        var drawHeight = _activeCore.FrameHeight * scale;
        var target = new Rectangle((width - drawWidth) / 2, (height - drawHeight) / 2 + 8, drawWidth, drawHeight);
        if (_activeCore.RenderMode == CoreRenderMode.Framebuffer)
        {
            Raylib.DrawTexturePro(_gameTexture,
                new Rectangle(0, 0, _activeCore.FrameWidth, _activeCore.FrameHeight), target,
                new Vector2(0, 0), 0, Color.White);
        }
        Raylib.DrawText(_activeRomName, 32, 24, 18, Text);
        Raylib.DrawText("ESC=menu  F5=save slot 1  F8=load slot 1  •  X=A  Z=B  Enter=Start  Backspace=Select  S/A=L/R",
            32, height - 54, 15, Muted);
        if (!string.IsNullOrWhiteSpace(_activeCore.StatusText))
            Raylib.DrawText(_activeCore.StatusText, 32, height - 80, 14, Text);
        if (!string.IsNullOrEmpty(_gameNotice)) Raylib.DrawText(_gameNotice, 32, height - 30, 14, Text);
        if (_activeCore.RenderMode == CoreRenderMode.OpenGL)
        {
            _activeCore.RunFrame();
            QueueCoreAudio(_activeCore.GetAudioBuffer());
            _activeCore.Render(CreateOpenGlContext());
        }
        Raylib.EndDrawing();
    }

    private static unsafe GraphicsContext CreateOpenGlContext() => new(
        CoreRenderMode.OpenGL,
        (nint)Raylib.GetWindowHandle(),
        NativeOpenGl.GetCurrentContext(),
        NativeOpenGl.GetProcAddress,
        width: Raylib.GetScreenWidth(),
        height: Raylib.GetScreenHeight());

    private static void DrawTabs(int width)
    {
        var x = 48;
        var y = 145;
        for (var i = 0; i < _tabs.Count; i++)
        {
            var label = _tabs[i].Name;
            var tabWidth = Math.Max(108, Raylib.MeasureText(label, 18) + 40);
            if (x + tabWidth > width - 48) break;
            var bounds = new Rectangle(x, y, tabWidth, 44);
            Raylib.DrawRectangleRounded(bounds, 0.25f, 8, i == _tabIndex ? Selected : Panel);
            Raylib.DrawText(label, x + 20, y + 13, 18, Text);
            x += tabWidth + 10;
        }
    }

    private static void DrawLibrary(int width, int height)
    {
        const int left = 48;
        const int top = 216;
        const int rowHeight = 58;
        var panel = new Rectangle(left, top, width - 96, height - top - 112);
        Raylib.DrawRectangleRounded(panel, 0.04f, 8, Panel);

        Raylib.DrawText(CurrentTab.IsPorts ? "PORTS NATIVOS" : $"JOGOS  /  {CurrentTab.Name.ToUpperInvariant()}",
            left + 28, top + 24, 15, Muted);

        var items = CurrentTab.Items;
        if (items.Count == 0)
        {
            var hint = CurrentTab.IsPorts
                ? "Nenhum .exe encontrado. Crie uma subpasta em /ports/ e coloque o jogo nela."
                : $"Nenhum jogo encontrado. Coloque ROMs em /roms/{CurrentTab.Name}/.";
            Raylib.DrawText(hint, left + 28, top + 72, 18, Muted);
            return;
        }

        var visibleRows = Math.Max(1, (height - top - 185) / rowHeight);
        var first = Math.Clamp(_itemIndex - visibleRows + 1, 0, Math.Max(0, items.Count - visibleRows));
        for (var row = 0; row < visibleRows && first + row < items.Count; row++)
        {
            var index = first + row;
            var y = top + 60 + row * rowHeight;
            if (index == _itemIndex)
                Raylib.DrawRectangleRounded(new Rectangle(left + 16, y, width - 128, 48), 0.18f, 8, Selected);

            var name = items[index].Name;
            if (name.Length > 82) name = name[..79] + "...";
            Raylib.DrawText(name, left + 32, y + 14, 18, Text);
        }
    }

    private sealed record LibraryItem(string Name, string FullPath);
    private sealed record SystemTab(string Name, List<LibraryItem> Items, bool IsPorts);

    private static class NativeOpenGl
    {
        private static readonly nint OpenGlLibrary = NativeLibrary.Load("opengl32.dll");

        [DllImport("opengl32.dll", EntryPoint = "wglGetCurrentContext")]
        private static extern nint WglGetCurrentContext();

        [DllImport("opengl32.dll", EntryPoint = "wglGetProcAddress", CharSet = CharSet.Ansi)]
        private static extern nint WglGetProcAddress(string name);

        public static nint GetCurrentContext() => WglGetCurrentContext();

        public static nint GetProcAddress(string name)
        {
            var address = WglGetProcAddress(name);
            var numeric = address.ToInt64();
            if (address != 0 && numeric != 1 && numeric != 2 && numeric != 3 && numeric != -1)
                return address;
            return NativeLibrary.TryGetExport(OpenGlLibrary, name, out var fallback) ? fallback : 0;
        }
    }
}
