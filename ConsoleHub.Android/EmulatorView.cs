using Android.Content;
using Android.Graphics;
using Android.Views;
using ConsoleHub;

namespace ConsoleHub.Android;

internal sealed class EmulatorView : View
{
    private readonly Func<ICoreInterface?> _getCore;
    private readonly Paint _paint = new() { FilterBitmap = true, AntiAlias = true };
    private readonly Dictionary<int, EmulatorInput> _touches = new();
    private Bitmap? _bitmap;
    private int[] _pixels = [];
    private EmulatorInput _input;
    private EmulatorInput _keyboardInput;

    public EmulatorView(Context context, Func<ICoreInterface?> getCore) : base(context) => _getCore = getCore;

    public void SetKeyboardInput(EmulatorInput input)
    {
        _keyboardInput = input;
        RebuildInput();
        Invalidate();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        canvas.DrawColor(Color.Rgb(11, 14, 21));
        var core = _getCore();
        if (core is not null)
        {
            try
            {
                core.SetInput(_input);
                core.RunFrame();
                DrawFrame(canvas, core);
            }
            catch
            {
                DrawCentered(canvas, "Erro ao executar o núcleo", Height / 2f);
            }
        }
        else DrawCentered(canvas, "ConsoleHub · carregue uma ROM", Height * 0.48f);
        DrawControls(canvas);
        if (core is not null) PostInvalidateDelayed(16);
    }

    private void DrawFrame(Canvas canvas, ICoreInterface core)
    {
        var width = core.FrameWidth;
        var height = core.FrameHeight;
        if (width <= 0 || height <= 0) return;
        if (_bitmap is null || _bitmap.Width != width || _bitmap.Height != height)
        {
            _bitmap?.Recycle();
            _bitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!);
            _pixels = new int[width * height];
        }
        var source = core.GetFrameBuffer().Span;
        if (source.Length < _pixels.Length) return;
        for (var i = 0; i < _pixels.Length; i++)
        {
            var c = source[i]; // CoreHub framebuffer: 0xAABBGGRR
            _pixels[i] = unchecked((int)((c & 0xFF00FF00u) | ((c & 0x00FF0000u) >> 16) | ((c & 0x000000FFu) << 16)));
        }
        _bitmap!.SetPixels(_pixels, 0, width, 0, 0, width, height);
        var pad = Math.Min(Width, Height) * 0.045f;
        var maxW = Width * 0.70f;
        var maxH = Height * 0.68f;
        var scale = Math.Min(maxW / width, maxH / height);
        var rect = new RectF((Width - width * scale) / 2, (Height - height * scale) / 2 - pad, (Width + width * scale) / 2, (Height + height * scale) / 2 - pad);
        canvas.DrawBitmap(_bitmap, null, rect, _paint);
    }

    private void DrawControls(Canvas canvas)
    {
        var radius = Math.Clamp(Math.Min(Width, Height) * 0.075f, 28f, 62f);
        DrawButton(canvas, Width * 0.13f, Height * 0.72f, radius, "←", EmulatorInput.Left);
        DrawButton(canvas, Width * 0.25f, Height * 0.72f, radius, "→", EmulatorInput.Right);
        DrawButton(canvas, Width * 0.19f, Height * 0.60f, radius, "↑", EmulatorInput.Up);
        DrawButton(canvas, Width * 0.19f, Height * 0.84f, radius, "↓", EmulatorInput.Down);
        DrawButton(canvas, Width * 0.82f, Height * 0.70f, radius, "B", EmulatorInput.B);
        DrawButton(canvas, Width * 0.91f, Height * 0.59f, radius, "A", EmulatorInput.A);
        DrawButton(canvas, Width * 0.43f, Height * 0.90f, radius * 0.68f, "SELECT", EmulatorInput.Select);
        DrawButton(canvas, Width * 0.57f, Height * 0.90f, radius * 0.68f, "START", EmulatorInput.Start);
        DrawButton(canvas, Width * 0.36f, Height * 0.16f, radius * 0.62f, "L", EmulatorInput.L);
        DrawButton(canvas, Width * 0.64f, Height * 0.16f, radius * 0.62f, "R", EmulatorInput.R);
    }

    private void DrawButton(Canvas canvas, float x, float y, float radius, string label, EmulatorInput button)
    {
        var active = (_input & button) != 0;
        _paint.Color = active ? Color.Argb(220, 90, 170, 255) : Color.Argb(135, 235, 242, 250);
        _paint.SetStyle(Paint.Style.Fill);
        canvas.DrawCircle(x, y, radius, _paint);
        _paint.Color = Color.White;
        _paint.TextAlign = Paint.Align.Center;
        _paint.TextSize = label.Length > 2 ? radius * 0.34f : radius * 0.72f;
        canvas.DrawText(label, x, y + _paint.TextSize * 0.34f, _paint);
    }

    private void DrawCentered(Canvas canvas, string text, float y)
    {
        _paint.Color = Color.White;
        _paint.TextAlign = Paint.Align.Center;
        _paint.TextSize = 24;
        canvas.DrawText(text, Width / 2f, y, _paint);
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null) return false;
        var index = e.ActionIndex;
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
            case MotionEventActions.PointerDown:
                _touches[e.GetPointerId(index)] = MapTouch(e.GetX(index), e.GetY(index));
                break;
            case MotionEventActions.Move:
                _touches.Clear();
                for (var i = 0; i < e.PointerCount; i++) _touches[e.GetPointerId(i)] = MapTouch(e.GetX(i), e.GetY(i));
                break;
            case MotionEventActions.Up:
            case MotionEventActions.PointerUp:
            case MotionEventActions.Cancel:
                if (e.ActionMasked == MotionEventActions.Cancel) _touches.Clear();
                else _touches.Remove(e.GetPointerId(index));
                break;
        }
        RebuildInput();
        Invalidate();
        return true;
    }

    private void RebuildInput() => _input = _touches.Values.Aggregate(_keyboardInput, (current, next) => current | next);

    private EmulatorInput MapTouch(float x, float y)
    {
        var rx = Math.Clamp(Math.Min(Width, Height) * 0.075f, 28f, 62f);
        var positions = new (float X, float Y, EmulatorInput Input)[]
        {
            (Width * .13f, Height * .72f, EmulatorInput.Left), (Width * .25f, Height * .72f, EmulatorInput.Right),
            (Width * .19f, Height * .60f, EmulatorInput.Up), (Width * .19f, Height * .84f, EmulatorInput.Down),
            (Width * .82f, Height * .70f, EmulatorInput.B), (Width * .91f, Height * .59f, EmulatorInput.A),
            (Width * .43f, Height * .90f, EmulatorInput.Select), (Width * .57f, Height * .90f, EmulatorInput.Start),
            (Width * .36f, Height * .16f, EmulatorInput.L), (Width * .64f, Height * .16f, EmulatorInput.R)
        };
        foreach (var p in positions)
        {
            var r = p.Input is EmulatorInput.Select or EmulatorInput.Start or EmulatorInput.L or EmulatorInput.R ? rx * .72f : rx;
            if (Math.Abs(x - p.X) <= r && Math.Abs(y - p.Y) <= r) return p.Input;
        }
        return EmulatorInput.None;
    }
}
