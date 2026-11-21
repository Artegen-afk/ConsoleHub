using ConsoleHub;
using ConsoleHub.Scaffolds;

namespace SwitchCore_HLE;

[CorePlugin("Switch", ".nsp", ".xci")]
[CorePlugin("NS2", ".nsp", ".xci")]
public sealed class SwitchCoreHle : CoreScaffoldBase
{
    public SwitchCoreHle() : base(960, 540, 0xFF4B2E8Cu) { }

    protected override string CoreKey => "switch-hle-scaffold-v1";
    protected override string PlatformLabel => "Switch / NS2 HLE";
    protected override string[] Extensions => [".nsp", ".xci"];

    protected override void OnRomInspected(string romPath, ReadOnlySpan<byte> header)
    {
        var systemDir = FindConsoleHubRoot();
        var prodKeys = Path.Combine(systemDir, "system", "switch", "prod.keys");
        var titleKeys = Path.Combine(systemDir, "system", "switch", "title.keys");
        SetStatus($"Switch HLE scaffold: keys {(File.Exists(prodKeys) ? "presentes" : "ausentes")}; title keys {(File.Exists(titleKeys) ? "presentes" : "ausentes")}. Nenhuma chave foi lida/exibida; Horizon, descriptografia e CPU ainda não implementados.");
    }

    private static string FindConsoleHubRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ConsoleHub.csproj"))) return current.FullName;
            if (File.Exists(Path.Combine(current.FullName, "ConsoleHub", "ConsoleHub.csproj")))
                return Path.Combine(current.FullName, "ConsoleHub");
            current = current.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
