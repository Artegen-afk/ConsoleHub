using System.Reflection;
using System.Runtime.Loader;
using ConsoleHub.Gba;
using ConsoleHub.Libretro;

namespace ConsoleHub;

internal sealed class CorePluginRegistry
{
    private readonly List<PluginEntry> _entries = [];
    private readonly List<AssemblyLoadContext> _loadContexts = [];
    private readonly List<string> _errors = [];

    public IReadOnlyList<string> Errors => _errors;
    public int Count => _entries.Select(entry => entry.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    public bool SupportsExtension(string extension) =>
        _entries.Any(entry => entry.Metadata.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase));

    public void LoadFrom(string coresDirectory)
    {
        Directory.CreateDirectory(coresDirectory);
        foreach (var path in Directory.EnumerateFiles(coresDirectory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var pluginAssemblyName = AssemblyName.GetAssemblyName(path);
                if (pluginAssemblyName.Name == typeof(ICoreInterface).Assembly.GetName().Name)
                    continue;
                var loadContext = new CorePluginLoadContext(path);
                var assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(path));
                _loadContexts.Add(loadContext);
                AddAssembly(assembly, Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                if (!TryAddLibretro(path, out var error))
                    _errors.Add($"{Path.GetFileName(path)}: {error ?? ex.GetBaseException().Message}");
            }
        }

        // External cores take precedence over built-ins for the same platform/ROM extension.
        AddAssembly(typeof(GbaCore).Assembly, "built-in");
    }

    public ICoreInterface? CreateFor(string systemId, string romPath)
    {
        var extension = Path.GetExtension(romPath);
        foreach (var entry in _entries
                     .Where(entry => entry.Metadata.SystemId.Equals(systemId, StringComparison.OrdinalIgnoreCase) &&
                                     entry.Metadata.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                     .OrderByDescending(entry => entry.Priority)
                     .ThenBy(entry => entry.Source, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                return entry.Factory();
            }
            catch (Exception ex)
            {
                _errors.Add($"{entry.Source}: {ex.GetBaseException().Message}");
            }
        }

        return null;
    }

    private void AddAssembly(Assembly assembly, string source)
    {
        foreach (var type in GetLoadableTypes(assembly))
        {
            if (type.IsAbstract || type.IsInterface || !typeof(ICoreInterface).IsAssignableFrom(type))
                continue;

            foreach (var metadata in type.GetCustomAttributes<CorePluginAttribute>())
                _entries.Add(new PluginEntry(metadata, source, () =>
                    (ICoreInterface)Activator.CreateInstance(type)!, source == "built-in" ? 0 : 10));
        }
    }

    private bool TryAddLibretro(string path, out string? error)
    {
        error = null;
        try
        {
            var info = LibretroBridgeCore.Inspect(path);
            var added = false;
            foreach (var systemId in info.SystemIds)
            foreach (var extension in info.Extensions)
            {
                var capturedSystemId = systemId;
                var capturedPath = Path.GetFullPath(path);
                var metadata = new CorePluginAttribute(capturedSystemId, extension);
                _entries.Add(new PluginEntry(metadata, Path.GetFileName(path),
                    () => new LibretroBridgeCore(capturedPath), 20));
                added = true;
            }

            if (!added) error = "núcleo libretro encontrado, mas sem extensões de ROM reconhecíveis.";
            return added;
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
            return false;
        }
    }

    private IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            foreach (var loaderException in ex.LoaderExceptions)
                if (loaderException is not null) _errors.Add($"{assembly.GetName().Name}: {loaderException.Message}");
            return ex.Types.OfType<Type>();
        }
    }

    private sealed record PluginEntry(CorePluginAttribute Metadata, string Source, Func<ICoreInterface> Factory, int Priority);

    private sealed class CorePluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver? _resolver;
        private readonly string _pluginDirectory;
        private static readonly Assembly SharedContracts = typeof(ICoreInterface).Assembly;

        public CorePluginLoadContext(string pluginPath) : base($"core:{Path.GetFileNameWithoutExtension(pluginPath)}", isCollectible: false)
        {
            _pluginDirectory = Path.GetDirectoryName(Path.GetFullPath(pluginPath))!;
            try { _resolver = new AssemblyDependencyResolver(Path.GetFullPath(pluginPath)); }
            catch { _resolver = null; }
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name == SharedContracts.GetName().Name) return SharedContracts;
            var path = _resolver?.ResolveAssemblyToPath(assemblyName)
                ?? Path.Combine(_pluginDirectory, $"{assemblyName.Name}.dll");
            if (!File.Exists(path)) return null;
            return LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName)
                ?? Path.Combine(_pluginDirectory, $"{unmanagedDllName}.dll");
            if (!File.Exists(path)) return 0;
            return LoadUnmanagedDllFromPath(path);
        }
    }
}
