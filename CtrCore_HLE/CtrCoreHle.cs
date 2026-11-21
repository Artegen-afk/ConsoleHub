using ConsoleHub;
using ConsoleHub.Scaffolds;

namespace CtrCore_HLE;

[CorePlugin("3DS", ".3ds", ".cci")]
public sealed class CtrCoreHle : CoreScaffoldBase
{
    public CtrCoreHle() : base(400, 480, 0xFF2D8A73u) { }

    protected override string CoreKey => "3ds-hle-scaffold-v1";
    protected override string PlatformLabel => "3DS HLE (top/bottom framebuffer)";
    protected override string[] Extensions => [".3ds", ".cci"];

    public override void RunFrame()
    {
        base.RunFrame();
        var separatorY = FrameHeight / 2;
        Array.Fill(Pixels, 0xFF050505u, (separatorY - 4) * FrameWidth, 8 * FrameWidth);
    }
}
