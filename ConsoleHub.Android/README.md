# ConsoleHub para Android

Projeto Android nativo em `net10.0-android`, separado da solução Windows para que a compilação desktop não passe a exigir o workload Android. O projeto referencia `ConsoleHub.Contracts` e compila diretamente os três arquivos do `GbaCore` existente.

## Instalar ferramentas (Windows 11 / PowerShell)

Instale o SDK .NET 10 e o workload Android:

```powershell
winget install Microsoft.DotNet.SDK.10
dotnet workload install android
```

O build também precisa do Android SDK e do Java SDK. Se ainda não estiverem instalados, abra o PowerShell na pasta `ConsoleHub.Android` e execute o alvo oficial de instalação de dependências:

```powershell
dotnet build -t:InstallAndroidDependencies -f net10.0-android -p:AcceptAndroidSdkLicenses=True
```

## Gerar APK

Na pasta `ConsoleHub.Android`:

```powershell
dotnet publish -f net10.0-android -c Release
```

O projeto define o formato de pacote como APK. O artefato é publicado em `bin\Release\net10.0-android\publish\`.

## Usar no aparelho

1. Instale e abra o APK.
2. Toque em **Abrir ROM** para escolher um `.gba`; esse caminho usa o núcleo GBA C# incluído.
3. Para libretro, toque em **Abrir núcleo .so**, escolha um núcleo compatível com a arquitetura do aparelho (normalmente `arm64-v8a`) e depois escolha a ROM aceita por esse núcleo.
4. Use os botões virtuais na tela, teclado ou controle conectado. Os callbacks libretro de vídeo de software, áudio, input, diretórios system/save e save states são ligados ao contrato compartilhado.

Os arquivos importados são copiados por streaming para o diretório privado do app (`files/roms`, `files/cores`); assim ROMs grandes não são carregadas inteiras na memória durante a importação. O .so precisa ser uma biblioteca libretro Android para a ABI do aparelho e pode exigir dependências nativas adicionais junto dela. A ponte Android atende o caminho de framebuffer de software; callbacks de renderização OpenGL/Vulkan de hardware ainda não são apresentados ao núcleo.
