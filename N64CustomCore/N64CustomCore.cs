using ConsoleHub;
using ConsoleHub.Scaffolds;

namespace N64CustomCore;

[CorePlugin("N64", ".z64", ".n64", ".v64")]
public sealed class N64CustomCore : CoreScaffoldBase
{
    public N64CustomCore() : base(960, 540, 0xFF3678A6u) { }

    protected override string CoreKey => "n64-custom-scaffold-v1";
    protected override string PlatformLabel => "N64 R4300 scaffold";
    protected override string[] Extensions => [".z64", ".n64", ".v64"];

    protected override void OnRomInspected(string romPath, ReadOnlySpan<byte> header)
    {
        if (header.Length >= 4)
        {
            var magic = Convert.ToHexString(header[..4]);
            if (magic is not ("80371240" or "40123780" or "37804012"))
                SetStatus($"N64 scaffold: cabeçalho desconhecido ({magic}); conteúdo não foi emulado.");
        }
    }
}
