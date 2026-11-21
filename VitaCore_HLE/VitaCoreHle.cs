using ConsoleHub;
using ConsoleHub.Scaffolds;

namespace VitaCore_HLE;

[CorePlugin("PS Vita", ".vpk")]
[CorePlugin("Vita", ".vpk")]
public sealed class VitaCoreHle : CoreScaffoldBase
{
    public VitaCoreHle() : base(960, 544, 0xFF7A3596u) { }

    protected override string CoreKey => "vita-hle-scaffold-v1";
    protected override string PlatformLabel => "PS Vita HLE";
    protected override string[] Extensions => [".vpk"];
}
