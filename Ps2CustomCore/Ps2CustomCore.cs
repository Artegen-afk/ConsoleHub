using ConsoleHub;
using ConsoleHub.Scaffolds;

namespace Ps2CustomCore;

[CorePlugin("PS2", ".iso", ".cso", ".chd")]
public sealed class Ps2CustomCore : CoreScaffoldBase
{
    public Ps2CustomCore() : base(960, 540, 0xFF3568B0u) { }

    protected override string CoreKey => "ps2-custom-scaffold-v1";
    protected override string PlatformLabel => "PS2 Emotion Engine scaffold";
    protected override string[] Extensions => [".iso", ".cso", ".chd"];
}
