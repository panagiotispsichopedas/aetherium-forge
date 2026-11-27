using AetheriumForge.Core.Discovery;
using Microsoft.Win32;

namespace AetheriumForge.Core.Tests.Discovery;

internal sealed class FakeRegistryReader : IRegistryReader
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _subKeys = new(StringComparer.OrdinalIgnoreCase);

    public FakeRegistryReader Set(RegistryHive hive, string subKey, string valueName, string value)
    {
        _values[$@"{hive}\{subKey}|{valueName}"] = value;

        var separator = subKey.LastIndexOf('\\');
        if (separator > 0)
        {
            var parent = $@"{hive}\{subKey[..separator]}";
            if (!_subKeys.TryGetValue(parent, out var children))
            {
                children = [];
                _subKeys[parent] = children;
            }

            var child = subKey[(separator + 1)..];
            if (!children.Contains(child, StringComparer.OrdinalIgnoreCase))
            {
                children.Add(child);
            }
        }

        return this;
    }

    public string? GetString(RegistryHive hive, string subKey, string valueName) =>
        _values.GetValueOrDefault($@"{hive}\{subKey}|{valueName}");

    public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string subKey) =>
        _subKeys.TryGetValue($@"{hive}\{subKey}", out var children) ? children : [];
}

internal sealed class FakeFileSystemProbe : IFileSystemProbe
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Version> _versions = new(StringComparer.OrdinalIgnoreCase);

    public FakeFileSystemProbe AddFile(string path, string content = "")
    {
        _files[path] = content;
        AddDirectory(Path.GetDirectoryName(path)!);

        return this;
    }

    public FakeFileSystemProbe AddDirectory(string path)
    {
        _directories.Add(Path.TrimEndingDirectorySeparator(path));

        return this;
    }

    public FakeFileSystemProbe AddGame(string gameFolder, Version? version = null)
    {
        var exe = Path.Combine(gameFolder, "SkyrimSE.exe");
        AddFile(exe);
        AddDirectory(Path.Combine(gameFolder, "Data"));
        if (version is not null)
        {
            _versions[exe] = version;
        }

        return this;
    }

    public bool FileExists(string path) => _files.ContainsKey(path);

    public bool DirectoryExists(string path) => _directories.Contains(Path.TrimEndingDirectorySeparator(path));

    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default) =>
        _files.TryGetValue(path, out var content) ? Task.FromResult(content) : Task.FromException<string>(new FileNotFoundException(path));

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern)
    {
        var extension = searchPattern.TrimStart('*');

        return _files.Keys
            .Where(file => string.Equals(Path.GetDirectoryName(file), Path.TrimEndingDirectorySeparator(directory), StringComparison.OrdinalIgnoreCase))
            .Where(file => file.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public Version? GetFileVersion(string path) => _versions.GetValueOrDefault(path);
}

internal sealed class FakeKnownFolderProvider(string documents, string localAppData) : IKnownFolderProvider
{
    public string GetDocumentsPath() => documents;

    public string GetLocalApplicationDataPath() => localAppData;
}
