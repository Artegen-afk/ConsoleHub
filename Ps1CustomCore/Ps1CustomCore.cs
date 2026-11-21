using ConsoleHub;
using ConsoleHub.Scaffolds;

namespace Ps1CustomCore;

[CorePlugin("PS1", ".bin", ".cue", ".chd", ".pbp")]
public sealed class Ps1CustomCore : CoreScaffoldBase
{
    public Ps1CustomCore() : base(960, 540, 0xFF71833Fu) { }

    protected override string CoreKey => "ps1-custom-scaffold-v1";
    protected override string PlatformLabel => "PS1 MIPS scaffold";
    protected override string[] Extensions => [".bin", ".cue", ".chd", ".pbp"];
}
