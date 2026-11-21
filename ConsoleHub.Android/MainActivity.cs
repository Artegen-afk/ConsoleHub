using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using ConsoleHub;
using ConsoleHub.Gba;

namespace ConsoleHub.Android;

[Activity(Label = "ConsoleHub", MainLauncher = true, Exported = true, ScreenOrientation = Android.Content.PM.ScreenOrientation.Landscape)]
public sealed class MainActivity : Activity
{
    private const int PickRomRequest = 101;
    private const int PickCoreRequest = 102;
    private EmulatorView? _emulatorView;
    private ICoreInterface? _core;
    private string? _selectedCorePath;
    private TextView? _status;
    private EmulatorInput _keyboardInput;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.SetFlags(Android.Views.WindowManagerFlags.KeepScreenOn, Android.Views.WindowManagerFlags.KeepScreenOn);

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Android.Graphics.Color.Rgb(18, 22, 32));
        var toolbar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        toolbar.SetPadding(8, 8, 8, 4);
        var coreButton = MakeButton("Abrir núcleo .so");
        coreButton.Click += (_, _) => PickFile(PickCoreRequest, "application/octet-stream");
        var romButton = MakeButton("Abrir ROM");
        romButton.Click += (_, _) => PickFile(PickRomRequest, "*/*");
        _status = new TextView(this) { Text = "Selecione uma ROM .gba ou um núcleo libretro .so", TextSize = 13 };
        _status.SetTextColor(Android.Graphics.Color.White);
        _status.Gravity = GravityFlags.CenterVertical;
        toolbar.AddView(coreButton, new LinearLayout.LayoutParams(0, 48, 1));
        toolbar.AddView(romButton, new LinearLayout.LayoutParams(0, 48, 1));
        toolbar.AddView(_status, new LinearLayout.LayoutParams(0, 48, 2));
        root.AddView(toolbar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 56));

        _emulatorView = new EmulatorView(this, () => _core);
        root.AddView(_emulatorView, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
        SetContentView(root);
    }

    private Button MakeButton(string text)
    {
        var button = new Button(this) { Text = text, TextSize = 12, AllCaps = false };
        return button;
    }

    private void PickFile(int request, string mimeType)
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType(mimeType);
        StartActivityForResult(intent, request);
    }

    protected override async void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (resultCode != Result.Ok || data?.Data is null) return;
        try
        {
            var destinationFolder = requestCode == PickCoreRequest ? "cores" : "roms";
            var folder = Path.Combine(FilesDir!.AbsolutePath, destinationFolder);
            Directory.CreateDirectory(folder);
            var name = GetDisplayName(data.Data) ?? (requestCode == PickCoreRequest ? "libretro.so" : "game.gba");
            var destination = Path.Combine(folder, SanitizeName(name));
            using (var input = ContentResolver!.OpenInputStream(data.Data) ?? throw new IOException("Não foi possível abrir o arquivo selecionado."))
            using (var output = File.Create(destination))
                await input.CopyToAsync(output);

            if (requestCode == PickCoreRequest)
            {
                _selectedCorePath = destination;
                _status!.Text = $"Núcleo: {Path.GetFileName(destination)} — selecione a ROM";
            }
            else
            {
                _core?.Dispose();
                if (_selectedCorePath is not null)
                {
                    var libretro = new LibretroSoBridgeCore(FilesDir!.AbsolutePath);
                    libretro.LoadCore(_selectedCorePath);
                    libretro.LoadRom(destination);
                    _core = libretro;
                }
                else if (Path.GetExtension(destination).Equals(".gba", StringComparison.OrdinalIgnoreCase))
                {
                    var gba = new GbaCore();
                    gba.LoadRom(destination);
                    _core = gba;
                }
                else throw new NotSupportedException("Sem núcleo .so selecionado, o app aceita ROM .gba pelo núcleo GBA incluído.");
                _status!.Text = $"{Path.GetFileName(destination)} — {(_selectedCorePath is null ? "GBA integrado" : Path.GetFileName(_selectedCorePath))}";
                _emulatorView?.Invalidate();
            }
        }
        catch (Exception ex)
        {
            _status!.Text = ex.Message;
        }
    }

    private string? GetDisplayName(Android.Net.Uri uri)
    {
        using var cursor = ContentResolver?.Query(uri, null, null, null, null);
        if (cursor is null || !cursor.MoveToFirst()) return null;
        var index = cursor.GetColumnIndex(Android.Provider.OpenableColumns.DisplayName);
        return index >= 0 ? cursor.GetString(index) : null;
    }

    private static string SanitizeName(string value)
    {
        var name = Path.GetFileName(value);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "imported-file" : name;
    }

    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e is null) return base.DispatchKeyEvent(e);
        var button = e.KeyCode switch
        {
            Keycode.DpadUp => EmulatorInput.Up, Keycode.DpadDown => EmulatorInput.Down,
            Keycode.DpadLeft => EmulatorInput.Left, Keycode.DpadRight => EmulatorInput.Right,
            Keycode.ButtonA => EmulatorInput.A, Keycode.ButtonB => EmulatorInput.B,
            Keycode.ButtonL1 => EmulatorInput.L, Keycode.ButtonR1 => EmulatorInput.R,
            Keycode.ButtonStart => EmulatorInput.Start, Keycode.ButtonSelect => EmulatorInput.Select,
            _ => EmulatorInput.None
        };
        if (button == EmulatorInput.None) return base.DispatchKeyEvent(e);
        if (e.Action == KeyEventActions.Down) _keyboardInput |= button;
        else if (e.Action == KeyEventActions.Up) _keyboardInput &= ~button;
        _emulatorView?.SetKeyboardInput(_keyboardInput);
        return true;
    }

    protected override void OnDestroy()
    {
        _core?.Dispose();
        _core = null;
        base.OnDestroy();
    }
}
