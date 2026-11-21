# ConsoleHub v2

## Build and run (Windows 11 / PowerShell)

```powershell
winget install Microsoft.DotNet.SDK.10
Set-Location .\ConsoleHub-v2
dotnet build .\ConsoleHub-v2.sln -c Release
dotnet run --project .\ConsoleHub\ConsoleHub.csproj -c Release --no-build
```

The build produces the seven modular scaffold DLLs directly in `ConsoleHub\cores\`. Add compatible native libretro core DLLs to that same folder and restart the launcher; the registry reads their exported metadata and routes supported ROM extensions to them. Third-party libretro binaries are not included in this ZIP.

## Current implementation boundary

The included Switch, Vita, 3DS, Dolphin, PS2, PS1, and N64 projects are **diagnostic scaffolds**, not playable emulators. They validate extensions, open images without loading the entire file into RAM, inspect a small header, and display a diagnostic framebuffer. Their CPU, GPU, operating system, audio, decryption, and game execution paths remain TODO. Save states intentionally report unsupported until a core has real machine state to serialize.

`SwitchCore_HLE` only checks whether `ConsoleHub\system\switch\prod.keys` and `title.keys` exist. It does not parse or use key contents, decrypt NSP/XCI packages, or emulate Horizon OS. No key files are included.

For playable consoles, place a compatible libretro `.dll` in `ConsoleHub\cores\` (plus its required adjacent dependencies). The bridge currently supports software video/audio/input callbacks and desktop OpenGL hardware-render callbacks. Vulkan, Direct3D, core options, archive extraction, and some core-specific frontend callbacks are not implemented, so DLL discovery does not guarantee that every core can run.

The built-in GBA core and `Core3D_Template` remain included. This project requires the .NET 10 SDK and restores Raylib-cs from NuGet on the first build.

## Android companion app

`ConsoleHub.Android` is a separate `net10.0-android` app that reuses the shared contracts and GBA source, adds on-screen controls, and can load Android libretro `.so` cores through the file picker. Follow [the Android build guide](ConsoleHub.Android/README.md). Keeping it outside `ConsoleHub-v2.sln` preserves the Windows-only build's existing workload requirements.
