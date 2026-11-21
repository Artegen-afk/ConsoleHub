using ConsoleHub;
using ConsoleHub.Scaffolds;

namespace DolphinCustomCore;

[CorePlugin("GameCube", ".iso", ".gcm", ".gcz", ".wbfs", ".rvz", ".wia")]
[CorePlugin("Wii", ".iso", ".gcm", ".gcz", ".wbfs", ".rvz", ".wia")]
public sealed class DolphinCustomCore : CoreScaffoldBase
{
    public DolphinCustomCore() : base(960, 540, 0xFFCF6E2Bu) { }

    protected override string CoreKey => "dolphin-custom-scaffold-v1";
    protected override string PlatformLabel => "Dolphin PowerPC/OpenGL scaffold";
    protected override string[] Extensions => [".iso", ".gcm", ".gcz", ".wbfs", ".rvz", ".wia"];
}
