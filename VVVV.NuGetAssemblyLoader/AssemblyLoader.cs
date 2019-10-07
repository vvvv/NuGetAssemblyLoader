using System;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.Versioning;
using NuGet;
using System.Diagnostics;
using System.Xml.Linq;
using System.Timers;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace VVVV.NuGetAssemblyLoader
{
    public interface IPackageWithPath : IPackage { string Path { get; } }

    public abstract class WatchedPackageRepositoryBase : PackageRepositoryBase
    {
        readonly FileSystemWatcher _watcher;
        Timer _delayTimer = new Timer(2000) { AutoReset = false };

        public WatchedPackageRepositoryBase(DirectoryInfo repositoryFolder)
        {
            _watcher = new FileSystemWatcher(repositoryFolder.FullName);
            _watcher.NotifyFilter = NotifyFilters.DirectoryName;
            _watcher.Created += HandlePathChanged;
            _watcher.Deleted += HandlePathChanged;
            _watcher.Changed += HandlePathChanged;
            _watcher.EnableRaisingEvents = true;

            _delayTimer.Elapsed += _delayTimer_Elapsed;

            void HandlePathChanged(object sender, FileSystemEventArgs e)
            {
                _delayTimer.Stop();
                _delayTimer.Start();
            }

            void _delayTimer_Elapsed(object sender, ElapsedEventArgs e)
            {
                ResetCache();

                AssemblyLoader.InvalidateCache();
            }
        }

        protected abstract void ResetCache();
    }

    public abstract class SrcPackage : IPackageWithPath
    {
        public abstract IEnumerable<IPackageAssemblyReference> AssemblyReferences { get; }
        public abstract IEnumerable<string> Authors { get; }
        public abstract string Copyright { get; }
        public abstract IEnumerable<PackageDependencySet> DependencySets { get; }
        public abstract string Description { get; }
        public abstract bool DevelopmentDependency { get; }
        public abstract int DownloadCount { get; }
        public abstract IEnumerable<FrameworkAssemblyReference> FrameworkAssemblies { get; }
        public abstract Uri IconUrl { get; }
        public abstract string Id { get; }
        public abstract bool IsAbsoluteLatestVersion { get; }
        public abstract bool IsLatestVersion { get; }
        public abstract string Language { get; }
        public abstract Uri LicenseUrl { get; }
        public abstract bool Listed { get; }
        public abstract Version MinClientVersion { get; }
        public abstract IEnumerable<string> Owners { get; }
        public abstract ICollection<PackageReferenceSet> PackageAssemblyReferences { get; }
        public abstract string Path { get; }
        public abstract Uri ProjectUrl { get; }
        public abstract DateTimeOffset? Published { get; }
        public abstract string ReleaseNotes { get; }
        public abstract Uri ReportAbuseUrl { get; }
        public abstract bool RequireLicenseAcceptance { get; }
        public abstract string Summary { get; }
        public abstract string Tags { get; }
        public abstract string Title { get; }
        public abstract SemanticVersion Version { get; }

        public abstract void ExtractContents(IFileSystem fileSystem, string extractPath);
        public abstract IEnumerable<IPackageFile> GetFiles();
        public abstract Stream GetStream();
        public abstract IEnumerable<FrameworkName> GetSupportedFrameworks();
    }

    public class SrcPackageWithNuspec : SrcPackage
    {
        readonly IFileSystem _repositoryFileSystem;
        readonly string _packageName;
        readonly string _nuspecFile;
        PackageBuilder _builder;
        List<PhysicalPackageFile> _files;

        public SrcPackageWithNuspec(IFileSystem repositoryFileSystem, string packageName, string nuspecFile)
        {
            _repositoryFileSystem = repositoryFileSystem;
            _packageName = packageName;
            _nuspecFile = nuspecFile;
        }

        public string NuspecFile => _nuspecFile;

        public override string Path => _repositoryFileSystem.GetFullPath(_packageName);

        internal List<PhysicalPackageFile> Files
        {
            get
            {
                if (_files == null)
                    _files = GetFilesCore().ToList();
                return _files;
            }
        }

        private PackageBuilder Builder
        {
            get
            {
                if (_builder == null)
                {
                    var path = _repositoryFileSystem.GetFullPath(_nuspecFile);
                    try
                    {
                        _builder = new PackageBuilder(path, NullPropertyProvider.Instance, false);
                    }
                    catch (Exception e) when (e is FileNotFoundException || e is DirectoryNotFoundException)
                    {
                        // Create a dummy
                        _builder = new PackageBuilder();
                        Trace.TraceWarning($"The NuSpec file {path} seems to be corrupt: {e.Message}");
                    }
                }
                return _builder;
            }
        }

        public override bool IsAbsoluteLatestVersion => true;
        public override bool IsLatestVersion => true;
        public override bool Listed => false;
        public override DateTimeOffset? Published => null;
        public override IEnumerable<IPackageAssemblyReference> AssemblyReferences => GetAssemblyReferencesCore();
        public override string Title => Builder.Title;
        public override IEnumerable<string> Authors => Builder.Authors;
        public override IEnumerable<string> Owners => Builder.Owners;
        public override Uri IconUrl => Builder.IconUrl;
        public override Uri LicenseUrl => Builder.LicenseUrl;
        public override Uri ProjectUrl => Builder.ProjectUrl;
        public override bool RequireLicenseAcceptance => Builder.RequireLicenseAcceptance;
        public override bool DevelopmentDependency => Builder.DevelopmentDependency;
        public override string Description => Builder.Description;
        public override string Summary => Builder.Summary;
        public override string ReleaseNotes => Builder.ReleaseNotes;
        public override string Language => Builder.Language;
        public override string Tags => string.Join(", ", Builder.Tags);
        public override string Copyright => Builder.Copyright;
        public override IEnumerable<FrameworkAssemblyReference> FrameworkAssemblies => Builder.FrameworkReferences;
        public override ICollection<PackageReferenceSet> PackageAssemblyReferences => Builder.PackageAssemblyReferences;
        public override IEnumerable<PackageDependencySet> DependencySets => Builder.DependencySets;
        public override Version MinClientVersion => Builder.MinClientVersion;
        public override string Id => _packageName;
        public override SemanticVersion Version => Builder.Version ?? new SemanticVersion(0, 0, 0, 0);
        public override Uri ReportAbuseUrl => null;
        public override int DownloadCount => -1;

        public override IEnumerable<FrameworkName> GetSupportedFrameworks()
        {
            return Builder.FrameworkReferences.SelectMany(f => f.SupportedFrameworks)
                .Concat(Files.SelectMany(f => f.SupportedFrameworks))
                .Distinct();
        }

        protected IEnumerable<IPackageAssemblyReference> GetAssemblyReferencesCore()
        {
            foreach (var file in Files)
            {
                if (!AssemblyLoader.IsAssemblyReference(file.Path))
                    continue;
                yield return new PhysicalPackageAssemblyReference
                {
                    SourcePath = file.SourcePath,
                    TargetPath = file.TargetPath
                };
            }
        }

        public override IEnumerable<IPackageFile> GetFiles()
        {
            return Files;
        }

        private IEnumerable<PhysicalPackageFile> GetFilesCore()
        {
            foreach (var file in Builder.Files.OfType<PhysicalPackageFile>())
            {
                if (file.IsAssemblyFile())
                {
                    var sourcePath = file.SourcePath;
                    var sourceDir = System.IO.Path.GetDirectoryName(sourcePath);
                    if (sourceDir.EndsWith("Release") || sourceDir.EndsWith("Debug"))
                    {
                        var fileName = System.IO.Path.GetFileName(sourcePath);
                        var baseDir = System.IO.Path.GetDirectoryName(sourceDir);
                        var debugFile = System.IO.Path.Combine(baseDir, "Debug", fileName);
                        var releaseFile = System.IO.Path.Combine(baseDir, "Release", fileName);
                        var debugTime = File.Exists(debugFile) ? File.GetLastWriteTime(debugFile) : DateTime.MinValue;
                        var releaseTime = File.Exists(releaseFile) ? File.GetLastWriteTime(releaseFile) : DateTime.MinValue;
                        if (releaseTime > debugTime)
                            file.SourcePath = releaseFile;
                        else if (debugTime > releaseTime)
                            file.SourcePath = debugFile;
                    }
                }
                yield return file;
            }
        }

        public override Stream GetStream()
        {
            throw new NotImplementedException();
        }

        public override void ExtractContents(IFileSystem fileSystem, string extractPath)
        {
            throw new NotImplementedException();
        }

        public override string ToString()
        {
            return this.GetFullName();
        }
    }

    public class SrcPackageWithoutNuspec : SrcPackage
    {
        readonly IFileSystem _packageFileSystem;
        readonly string _packageName;
        readonly string _vlFile;
        XDocument _mainFile;
        List<PhysicalPackageFile> _files;

        public SrcPackageWithoutNuspec(IFileSystem repositoryFileSystem, string packageName, string vlFile)
        {
            _packageFileSystem = new PhysicalFileSystem(repositoryFileSystem.GetFullPath(packageName));
            _packageName = packageName;
            _vlFile = repositoryFileSystem.GetFullPath(vlFile);
        }

        public override string Path => _packageFileSystem.Root;

        public XDocument MainFile => _mainFile ?? (_mainFile = XDocument.Load(_vlFile));

        internal List<PhysicalPackageFile> Files
        {
            get
            {
                if (_files == null)
                    _files = GetFilesCore().ToList();
                return _files;
            }
        }

        public override bool IsAbsoluteLatestVersion => true;
        public override bool IsLatestVersion => true;
        public override bool Listed => false;
        public override DateTimeOffset? Published => null;
        public override IEnumerable<IPackageAssemblyReference> AssemblyReferences => GetAssemblyReferencesCore();
        public override string Title => null;
        public override IEnumerable<string> Authors => Enumerable.Empty<string>();
        public override IEnumerable<string> Owners => Enumerable.Empty<string>();
        public override Uri IconUrl => null;
        public override Uri LicenseUrl => null;
        public override Uri ProjectUrl => null;
        public override bool RequireLicenseAcceptance => false;
        public override bool DevelopmentDependency => false;
        public override string Description => null;
        public override string Summary => null;
        public override string ReleaseNotes => null;
        public override string Language => null;
        public override string Tags => null;
        public override string Copyright => null;
        public override IEnumerable<FrameworkAssemblyReference> FrameworkAssemblies => Enumerable.Empty<FrameworkAssemblyReference>();
        public override ICollection<PackageReferenceSet> PackageAssemblyReferences => Array.Empty<PackageReferenceSet>();

        public override IEnumerable<PackageDependencySet> DependencySets
        {
            get
            {
                var dependencies = MainFile.Root.Elements("NugetDependency")
                    .Select(d => d.Attribute("Location") != null ? new PackageDependency(d.Attribute("Location").Value) : null)
                    .Where(d => d != null);
                yield return new PackageDependencySet(AssemblyLoader.ExecutingFrameworkName, dependencies);
            }
        }

        public override Version MinClientVersion => new Version(0, 0, 0);
        public override string Id => _packageName;

        public override SemanticVersion Version
        {
            get
            {
                //var versionAttribute = MainFile.Root.Attribute("Version");
                //if (versionAttribute != null)
                //{
                //    SemanticVersion result;
                //    if (SemanticVersion.TryParse(versionAttribute.Value, out result))
                //        return result;
                //}
                return new SemanticVersion(0, 0, 0, 0);
            }
        }

        public override Uri ReportAbuseUrl => null;
        public override int DownloadCount => -1;

        public override IEnumerable<FrameworkName> GetSupportedFrameworks()
        {
            yield return AssemblyLoader.ExecutingFrameworkName;
        }

        protected IEnumerable<IPackageAssemblyReference> GetAssemblyReferencesCore()
        {
            foreach (var file in Files)
            {
                if (!AssemblyLoader.IsAssemblyReference(file.Path))
                    continue;
                yield return new PhysicalPackageAssemblyReference
                {
                    SourcePath = file.SourcePath,
                    TargetPath = file.TargetPath
                };
            }
        }

        public override IEnumerable<IPackageFile> GetFiles()
        {
            return Files;
        }

        private IEnumerable<PhysicalPackageFile> GetFilesCore()
        {
            foreach (var f in _packageFileSystem.GetFiles("", "*", true))
            {
                yield return new PhysicalPackageFile()
                {
                    SourcePath = _packageFileSystem.GetFullPath(f),
                    TargetPath = f
                };
            }
        }

        public override Stream GetStream()
        {
            throw new NotImplementedException();
        }

        public override void ExtractContents(IFileSystem fileSystem, string extractPath)
        {
            throw new NotImplementedException();
        }

        public override string ToString()
        {
            return this.GetFullName();
        }
    }

    public class InstalledNupkgPackage : ZipPackage, IPackageWithPath
    {
        readonly IFileSystem _packageFileSystem;
        List<PhysicalPackageFile> _files;

        public InstalledNupkgPackage(IFileSystem repositoryFileSystem, string folder, string nupkgFile)
            : base(repositoryFileSystem.GetFullPath(nupkgFile))
        {
            _packageFileSystem = new PhysicalFileSystem(repositoryFileSystem.GetFullPath(folder));
        }

        public string Path => _packageFileSystem.Root;

        protected List<PhysicalPackageFile> Files
        {
            get
            {
                if (_files == null)
                    _files = GetFilesCore().ToList();
                return _files;
            }
        }

        public override IEnumerable<FrameworkName> GetSupportedFrameworks()
        {
            return base.GetSupportedFrameworks();
        }

        protected override IEnumerable<IPackageAssemblyReference> GetAssemblyReferencesCore()
        {
            foreach (var file in Files)
            {
                if (!IsAssemblyReference(file.Path))
                    continue;
                yield return new PhysicalPackageAssemblyReference()
                {
                    SourcePath = file.SourcePath,
                    TargetPath = file.TargetPath
                };
            }
        }

        protected override IEnumerable<IPackageFile> GetFilesBase() => Files;

        private IEnumerable<PhysicalPackageFile> GetFilesCore()
        {
            foreach (var file in _packageFileSystem.GetFiles(string.Empty, "*", true))
            {
                if (PackageHelper.IsManifest(file) || PackageHelper.IsPackageFile(file))
                    continue;
                var packageFile = new PhysicalPackageFile()
                {
                    SourcePath = _packageFileSystem.GetFullPath(file)
                };
                try
                {
                    packageFile.TargetPath = file;
                }
                catch (ArgumentException)
                {
                    // Some packages contain invalid target framework names
                }
                yield return packageFile;
            }
        }
    }

    public class InstalledNuspecPackage : LocalPackage, IPackageWithPath
    {
        readonly IFileSystem _packageFileSystem;
        List<PhysicalPackageFile> _files;

        public InstalledNuspecPackage(IFileSystem repositoryFileSystem, string folder, string nuspecFile)
        {
            _packageFileSystem = new PhysicalFileSystem(repositoryFileSystem.GetFullPath(folder));
            var f = repositoryFileSystem.GetFullPath(nuspecFile);
            using (var stream = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read))
                ReadManifest(stream);
        }

        public string Path => _packageFileSystem.Root;

        protected List<PhysicalPackageFile> Files
        {
            get
            {
                if (_files == null)
                    _files = GetFilesCore().ToList();
                return _files;
            }
        }

        public override IEnumerable<FrameworkName> GetSupportedFrameworks()
        {
            return base.GetSupportedFrameworks();
        }

        protected override IEnumerable<IPackageAssemblyReference> GetAssemblyReferencesCore()
        {
            foreach (var file in Files)
            {
                if (!IsAssemblyReference(file.Path))
                    continue;
                yield return new PhysicalPackageAssemblyReference()
                {
                    SourcePath = file.SourcePath,
                    TargetPath = file.TargetPath
                };
            }
        }

        protected override IEnumerable<IPackageFile> GetFilesBase() => Files;

        private IEnumerable<PhysicalPackageFile> GetFilesCore()
        {
            foreach (var file in _packageFileSystem.GetFiles(string.Empty, "*", true))
            {
                if (PackageHelper.IsManifest(file) || PackageHelper.IsPackageFile(file))
                    continue;
                var packageFile = new PhysicalPackageFile()
                {
                    SourcePath = _packageFileSystem.GetFullPath(file)
                };
                try
                {
                    packageFile.TargetPath = file;
                }
                catch (ArgumentException)
                {
                    // Some packages contain invalid target framework names
                }
                yield return packageFile;
            }
        }

        public override Stream GetStream()
        {
            throw new NotImplementedException();
        }

        public override void ExtractContents(IFileSystem fileSystem, string extractPath)
        {
            throw new NotImplementedException();
        }
    }

    public class LocalPackageRepository : WatchedPackageRepositoryBase
    {
        readonly IFileSystem _fileSystem;
        IQueryable<IPackage> _packages;

        public LocalPackageRepository(DirectoryInfo repositoryFolder) : base(repositoryFolder)
        {
            _fileSystem = new PhysicalFileSystem(repositoryFolder.FullName);
        }

        protected override void ResetCache()
        {
            _packages = null;
        }

        public override string Source => _fileSystem.Root;
        public override bool SupportsPrereleasePackages => true;
        public override IQueryable<IPackage> GetPackages()
        {
            if (_packages == null)
                _packages = GetPackagesCore().ToList().AsQueryable();
            return _packages;
        }

        private IEnumerable<IPackage> GetPackagesCore()
        {
            var hasVersionRegex = new Regex(@"[0-9]+\.[0-9]+\.[0-9]+");
            foreach (var dir in _fileSystem.GetDirectories(string.Empty))
            {
                if (hasVersionRegex.IsMatch(dir))
                {
                    foreach (var file in _fileSystem.GetFiles(dir, "*"))
                    {
                        var pkg = default(IPackage);
                        var ext = Path.GetExtension(file);
                        if (ext == Constants.PackageExtension)
                            pkg = new InstalledNupkgPackage(_fileSystem, dir, file);
                        else if (ext == Constants.ManifestExtension || ext == $"{Constants.ManifestExtension}1")
                            pkg = new InstalledNuspecPackage(_fileSystem, dir, file);
                        if (pkg != null)
                        {
                            if (pkg.MinClientVersion != null && Constants.NuGetVersion < pkg.MinClientVersion)
                                Trace.TraceWarning($"Ignoring package {pkg} because it requires NuGet {pkg.MinClientVersion}");
                            else
                                yield return pkg;
                            break;
                        }
                    }
                }
                else
                {
                    var hasNuspec = false;
                    if (!hasNuspec)
                    {
                        // VL.CoreLib/src/bin/$(Configuration)/$(TFM)/VL.CoreLib.nuspec
                        // VL.CoreLib/bin/$(Configuration)/$(TFM)/VL.CoreLib.nuspec
                        var nuspecFiles = _fileSystem.GetDirectories(Path.Combine(dir, "src", "bin"))
                            .Concat(_fileSystem.GetDirectories(Path.Combine(dir, "bin")))
                            .SelectMany(d => _fileSystem.GetDirectories(d))
                            .SelectMany(d => _fileSystem.GetFiles(d, $"{dir}{Constants.ManifestExtension}"))
                            .Select(f => new { File = f, Modified = File.GetLastWriteTime(_fileSystem.GetFullPath(f)) })
                            .OrderByDescending(f => f.Modified);
                        foreach (var nuspecFile in nuspecFiles)
                        {
                            hasNuspec = true;
                            yield return new SrcPackageWithNuspec(_fileSystem, dir, nuspecFile.File);
                            break;
                        }
                    }
                    if (!hasNuspec)
                    {
                        // VL.CoreLib/VL.CoreLib.nusepc
                        foreach (var nuspecFile in _fileSystem.GetFiles(dir, $"{dir}{Constants.ManifestExtension}"))
                        {
                            hasNuspec = true;
                            yield return new SrcPackageWithNuspec(_fileSystem, dir, nuspecFile);
                            break;
                        }
                    }
                    if (!hasNuspec)
                    {
                        foreach (var vlFile in _fileSystem.GetFiles(dir, $"{dir}.vl"))
                        {
                            hasNuspec = true;
                            yield return new SrcPackageWithoutNuspec(_fileSystem, dir, vlFile);
                            break;
                        }
                    }
                }
            }
        }
    }

    class PreferSourceOverInstalledAggregateRepository : IPackageRepository, IPackageLookup
    {
        struct PackageKey : IEquatable<PackageKey>
        {
            public readonly string PackageId;
            public readonly SemanticVersion Version;

            public PackageKey(string packageId, SemanticVersion version)
            {
                PackageId = packageId;
                Version = version;
            }

            public override bool Equals(object obj)
            {
                if (obj is PackageKey)
                    return Equals((PackageKey)obj);
                return false;
            }

            public bool Equals(PackageKey value) => value.PackageId == PackageId && value.Version == Version;

            public override int GetHashCode()
            {
                return PackageId.GetHashCode();
            }
        }

        readonly AggregateRepository _repository;
        readonly Dictionary<PackageKey, IPackage> _packageCache = new Dictionary<PackageKey, IPackage>();

        public PreferSourceOverInstalledAggregateRepository(IPackageRepositoryFactory repositoryFactory, IEnumerable<string> packageSources) 
        {
            _repository = new AggregateRepository(repositoryFactory, packageSources, ignoreFailingRepositories: true);
        }

        public PackageSaveModes PackageSaveMode
        {
            get { return _repository.PackageSaveMode; }
            set { _repository.PackageSaveMode = value; }
        }

        public string Source => _repository.Source;
        public bool SupportsPrereleasePackages => _repository.SupportsPrereleasePackages;
        public void AddPackage(IPackage package) => _repository.AddPackage(package);
        public IQueryable<IPackage> GetPackages() => GetPackagesPreferingSourceOverInstalled().AsQueryable();
        public void RemovePackage(IPackage package) => _repository.RemovePackage(package);
        public bool Exists(string packageId, SemanticVersion version) => _repository.Exists(packageId, version);

        public IPackage FindPackage(string packageId, SemanticVersion version)
        {
            var result = default(IPackage);
            var key = new PackageKey(packageId, version);
            if (!_packageCache.TryGetValue(key, out result))
            {
                foreach (var repo in _repository.Repositories)
                {
                    var localResult = repo.FindPackage(packageId, version);
                    if (localResult is SrcPackage)
                    {
                        // We found a source package
                        result = localResult;
                        break;
                    }
                    else if (localResult != null)
                    {
                        // Remember the result but continue looking as there could be a source package hiding
                        result = localResult;
                    }
                }
                _packageCache.Add(key, result);
            }
            return result;
        }

        public IEnumerable<IPackage> FindPackagesById(string packageId)
        {
            return _repository.FindPackagesById(packageId).OrderBy(p => p is SrcPackage ? 0 : 1);
        }

        private IEnumerable<IPackage> GetPackagesPreferingSourceOverInstalled()
        {
            var seenPackageIds = new HashSet<string>();
            var packages = _repository.GetPackages().ToList().OrderByDescending(p => p.Version);
            foreach (var package in packages.OfType<SrcPackage>())
            {
                if (seenPackageIds.Add(package.Id))
                    yield return package;
            }
            foreach (var package in packages)
            {
                if (seenPackageIds.Add(package.Id))
                    yield return package;
            }
        }
    }

    class RepositoryFactory : IPackageRepositoryFactory
    {
        public IPackageRepository CreateRepository(string packageSource)
        {
            return new LocalPackageRepository(new DirectoryInfo(packageSource));
        }
    }

    public static class AssemblyLoader
    {
        class DummyLocalPackage : LocalPackage
        {
            public new static bool IsAssemblyReference(string filePath)
            {
                return LocalPackage.IsAssemblyReference(filePath);
            }

            public override void ExtractContents(IFileSystem fileSystem, string extractPath)
            {
                throw new NotImplementedException();
            }

            public override Stream GetStream()
            {
                throw new NotImplementedException();
            }

            protected override IEnumerable<IPackageAssemblyReference> GetAssemblyReferencesCore()
            {
                throw new NotImplementedException();
            }

            protected override IEnumerable<IPackageFile> GetFilesBase()
            {
                throw new NotImplementedException();
            }
        }

        public static bool IsAssemblyReference(string filePath) => DummyLocalPackage.IsAssemblyReference(filePath);

        private const string ResourceAssemblyExtension = ".resources.dll";
        static readonly ConcurrentDictionary<string, IPackage> _packages = new ConcurrentDictionary<string, IPackage>();
        static readonly Dictionary<string, string> _packageAssemblyCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, KeyValuePair<string, IPackage>> _fileCache = new Dictionary<string, KeyValuePair<string, IPackage>>(StringComparer.OrdinalIgnoreCase);
        static Dictionary<string, Assembly> _loadedAssemblyCache;
        static readonly List<string> _packageRepositories = new List<string>();
        static PreferSourceOverInstalledAggregateRepository _repository;
        static FrameworkName _frameworkName;
        static string _frameworkShortName;
        static volatile bool _cacheIsValid;

        static AssemblyLoader()
        {
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
            AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += CurrentDomain_ReflectionOnlyAssemblyResolve;
            AppDomain.CurrentDomain.AssemblyLoad += CurrentDomain_AssemblyLoad;
        }

        private static void CurrentDomain_AssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            var assembly = args.LoadedAssembly;
            if (!assembly.IsDynamic && !assembly.ReflectionOnly)
            {
                // Reset the cache
                _loadedAssemblyCache = null;
            }
        }

        static Dictionary<string, Assembly> LoadedAssemblyCache
        {
            get
            {
                if (_loadedAssemblyCache == null)
                {
                    var result = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var assemblyName = assembly.GetName();
                        var localPath = GetLocalPath(assembly);
                        if (!string.IsNullOrEmpty(localPath) && !assembly.IsDynamic && !assembly.ReflectionOnly)
                        {
                            Assembly existing;
                            var key = assemblyName.Name;
                            if (result.TryGetValue(key, out existing) && assemblyName.Version > existing.GetName().Version)
                                result[key] = assembly;
                            else
                                result[key] = assembly;
                            var fileName = Path.GetFileName(localPath);
                            if (!result.ContainsKey(fileName))
                                result[fileName] = assembly;
                        }
                    }
                    _loadedAssemblyCache = result;
                }
                return _loadedAssemblyCache;
            }
        }

        public static List<string> ParseLines(string[] lines, string key)
        {
            var sourcesIndex = Array.IndexOf(lines, key);
            if (sourcesIndex >= 0 && lines.Length > sourcesIndex + 1)
            {
                var sourcesString = lines[sourcesIndex + 1];
                var paths = sourcesString.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                for (int i = 0; i < paths.Count; i++)
                {
                    paths[i] = paths[i].Trim('"', '\\');
                }
                return paths;
            }
            return new List<string>();
        }

        public static List<string> ParseCommandLine(string key)
        {
            var commandLineArgs = Environment.GetCommandLineArgs();
            return ParseLines(commandLineArgs, key);
        }

        public static IEnumerable<string> PackageRepositories => _packageRepositories;

        public static IPackageRepository Repository
        {
            get
            {
                if (_repository == null)
                    _repository = new PreferSourceOverInstalledAggregateRepository(new RepositoryFactory(), _packageRepositories);
                return _repository;
            }
        }

        // Repository.FindPackage is incredibly slow (~15ms for each query)
        public static IPackage FindPackageAndCacheResult(string id)
        {
            IPackage result;
            if (!_packages.TryGetValue(id, out result))
            {
                result = Repository.FindPackage(id);
                _packages.TryAdd(id, result);
            }
            return result;
        }

        public static FrameworkName ExecutingFrameworkName
        {
            get
            {
                if (_frameworkName == null)
                {
                    var tfn = AppDomain.CurrentDomain.SetupInformation.TargetFrameworkName;
                    if (tfn != null)
                        _frameworkName = new FrameworkName(tfn);
                    else // TargetFrameworkName is null when CLR was created by custom host (CorBindToRuntimeEx)
                        _frameworkName = new FrameworkName(".NETFramework", GetClrVersion());
                }
                return _frameworkName;
            }
            set { _frameworkName = value; }
        }

        public static string ExecutingFrameworkShortName
        {
            get
            {
                if (_frameworkShortName == null)
                    _frameworkShortName = VersionUtility.GetShortFrameworkName(ExecutingFrameworkName);
                return _frameworkShortName;
            }
        }

        static Version GetClrVersion()
        {
            // Will probably only work for .NET 4.5 and up :/
            var versionInfo = FileVersionInfo.GetVersionInfo(typeof(object).Assembly.Location);
            if (versionInfo.FileVersion.Contains("NET472"))
                return new Version(4, 7, 2);
            var buildNumer = int.Parse(versionInfo.FileBuildPart.ToString().Substring(0, 1));
            return new Version(versionInfo.FileMajorPart, versionInfo.FileMinorPart, buildNumer);
        }

        public static void AddPackageRepositories(List<string> packageRepositories)
        {
            foreach (var packageRepository in packageRepositories)
                AddPackageRepository(packageRepository);
        }

        public static void AddPackageRepositories(params string[] packageRepositories)
        {
            foreach (var packageRepository in packageRepositories)
                AddPackageRepository(packageRepository);
        }

        public static void AddPackageRepository(string packageRepository)
        {
            // Normalize the path
            packageRepository = Path.GetFullPath(packageRepository);
            if (!Directory.Exists(packageRepository))
                throw new DirectoryNotFoundException($"The package directory \"{packageRepository}\" doesn't exist!");
            if (_packageRepositories.Contains(packageRepository, StringComparer.OrdinalIgnoreCase))
                return;
            _packageRepositories.Add(packageRepository);
            _repository = null;
            _cacheIsValid = false;
        }

        public static event EventHandler CacheInvalidated;

        private static void EnsureValidCache()
        {
            if (!_cacheIsValid)
                CacheFiles(Repository);
        }

        internal static void InvalidateCache()
        {
            _cacheIsValid = false;
            _packages.Clear();
            _repository = null;
            CacheInvalidated?.Invoke(null, EventArgs.Empty);
        }

        class HighestPackageSorter : PackageSorter
        {
            readonly IPackageRepository _repository;

            public HighestPackageSorter(FrameworkName targetFramework, IPackageRepository repository) 
                : base(targetFramework)
            {
                _repository = repository;
            }

            // NuGet defaults to lowest dependency version
            protected override IPackage ResolveDependency(PackageDependency dependency) => _repository.ResolveDependency(dependency, null, true, true, DependencyVersion.Highest);
        }

        private static void CacheFiles(IPackageRepository repository)
        {
            lock (_packageAssemblyCache)
            {
                lock (_fileCache)
                {
                    _packageAssemblyCache.Clear();
                    _fileCache.Clear();

                    var set = new HashSet<IPackage>();
                    var sorter = new HighestPackageSorter(ExecutingFrameworkName, repository);
                    foreach (var p in sorter.GetPackagesByDependencyOrder(repository))
                    {
                        if (!set.Add(p))
                            continue;
                        CacheFiles(p);
                    }
                    _cacheIsValid = true;
                }
            }
        }

        private static void CacheFiles(IPackage package)
        {
            foreach (var file in package.GetFiles().OfType<PhysicalPackageFile>())
            {
                var fileName = Path.GetFileName(file.Path);
                if (!_fileCache.ContainsKey(fileName))
                    _fileCache.Add(fileName, new KeyValuePair<string, IPackage>(file.SourcePath, package));
            }
            foreach (var assemblyFile in package.GetCompatibleAssemblyFiles())
            {
                if (!_packageAssemblyCache.ContainsKey(assemblyFile.Name))
                    _packageAssemblyCache.Add(assemblyFile.Name, assemblyFile.SourcePath);
                var assemblyName = Path.GetFileNameWithoutExtension(assemblyFile.Name);
                if (!_packageAssemblyCache.ContainsKey(assemblyName))
                    _packageAssemblyCache.Add(assemblyName, assemblyFile.SourcePath);
            }
            foreach (var nativePath in GetNativePaths(package))
            {
                // Skip Debug folders as seen in CNTK packages
                if (nativePath.EndsWith($"{Path.DirectorySeparatorChar}Debug"))
                    continue;
                var PATH = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                if (!PATH.Contains(nativePath))
                    Environment.SetEnvironmentVariable("PATH", PATH + Path.PathSeparator + nativePath);
            }
        }

        static IEnumerable<string> GetNativePaths(IPackage package)
        {
            var set = new HashSet<string>();
            foreach (var nativePath in GetNativePaths(package, "lib-native", true))
                if (set.Add(nativePath))
                    yield return nativePath;
            foreach (var nativePath in GetNativePaths(package, "NativeDlls", true))
                if (set.Add(nativePath))
                    yield return nativePath;

            var platform = "";
            var platformFound = false;
            var osVersion = Environment.OSVersion.Version.ToString();
            //platforms according to: https://stackoverflow.com/questions/21737985/windows-version-in-c-sharp
            if (osVersion.StartsWith("10"))
                platform = "win10";
            else if (osVersion.StartsWith("6.3"))
                platform = "win81";
            else if (osVersion.StartsWith("6.2"))
                platform = "win8";
            else if (osVersion.StartsWith("6.1"))
                platform = "win7";

            foreach (var nativePath in GetNativePaths(package, Path.Combine("runtimes", platform + "-" + (Environment.Is64BitProcess ? "x64" : "x86"), "native"), false))
                if (set.Add(nativePath))
                {
                    platformFound = true;
                    yield return nativePath;
                }

            if (!platformFound)
            {
                foreach (var nativePath in GetNativePaths(package, Path.Combine("runtimes", "win-" + (Environment.Is64BitProcess ? "x64" : "x86"), "native"), false))
                    if (set.Add(nativePath))
                        yield return nativePath;
            }
            
            foreach (var nativePath in GetNativePaths(package, "support", true))
                if (set.Add(nativePath))
                    yield return nativePath;

            // Seen in Microsoft.Azure packages
            if (Environment.Is64BitProcess)
            {
                foreach (var nativePath in GetNativePaths(package, Path.Combine("lib", "native", "amd64", "release"), false))
                    if (set.Add(nativePath))
                        yield return nativePath;
            }
            else
            {
                foreach (var nativePath in GetNativePaths(package, Path.Combine("lib", "native", "x86", "release"), false))
                    if (set.Add(nativePath))
                        yield return nativePath;
            }
        }

        static IEnumerable<string> GetNativePaths(IPackage package, string nativeLibBaseDir, bool appendArchitecture)
        {
            var nativeLibDir = appendArchitecture ? AppendProcessArchitecture(nativeLibBaseDir) : nativeLibBaseDir;
            foreach (var nativeFile in package.GetFiles(nativeLibDir).OfType<PhysicalPackageFile>())
                yield return Path.GetDirectoryName(nativeFile.SourcePath);
        }

        public static void AddPathToBeAwareOfWhenSearchingForNativeDlls(string path)
        {
            var nativePath = Path.Combine(path, AppendProcessArchitecture("lib-native"));
            if (Directory.Exists(nativePath))
            {
                var PATH = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                if (!PATH.Contains(nativePath))
                    Environment.SetEnvironmentVariable("PATH", PATH + Path.PathSeparator + nativePath);
            }
        }

        static string AppendProcessArchitecture(string path) => Path.Combine(path, Environment.Is64BitProcess ? "x64" : "x86");

        private static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            var referencedAssemblyName = new AssemblyName(args.Name);
            if (referencedAssemblyName.Name.EndsWith(".resources"))
                return null;
            var assemblyFile = ProbeAssemblyReference(referencedAssemblyName);
            if (assemblyFile != null)
                return Assembly.LoadFrom(assemblyFile);
            return null;
        }

        private static Assembly CurrentDomain_ReflectionOnlyAssemblyResolve(object sender, ResolveEventArgs args)
        {
            var referencedAssemblyName = new AssemblyName(args.Name);
            if (referencedAssemblyName.Name.EndsWith(".resources"))
                return null;
            var assemblyFile = ProbeAssemblyReference(referencedAssemblyName);
            if (assemblyFile != null)
                return Assembly.ReflectionOnlyLoadFrom(assemblyFile);
            return null;
        }

        public static string ProbeAssemblyReference(AssemblyName referencedAssemblyName)
        {
            return FindAssemblyFile(referencedAssemblyName.Name);
        }

        private static IEnumerable<IPackage> GetAllPackages(string packageId)
        {
            var package = Repository.FindPackage(packageId);
            if (package != null)
                yield return package;
            foreach (var p in Repository.GetPackages())
                yield return p;
        }

        public static string FindAssemblyFile(string assemblyName)
        {
            // Check the loaded assemblies of the CLR host first - we don't want a mix of assemblies in Load and LoadFrom context from different locations!
            Assembly loadedAssembly;
            if (LoadedAssemblyCache.TryGetValue(assemblyName, out loadedAssembly))
                return GetLocalPath(loadedAssembly);

            // Check our packages
            EnsureValidCache();
            lock (_packageAssemblyCache)
            {
                string result;
                if (_packageAssemblyCache.TryGetValue(assemblyName, out result))
                    return result;
            }
            return null;
        }

        public static string GetLocalPath(Assembly assembly)
        {
            if (assembly.IsDynamic)
                return null;

            var loc = assembly.Location;
            if (loc == null) loc = "";
            if (loc.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                Uri u = new Uri(loc, UriKind.Absolute);
                loc = u.LocalPath;
            }
            return loc;
        }

        public static string FindFile(string fileName)
        {
            if (fileName.IsAssemblyFile())
                return FindAssemblyFile(Path.GetFileNameWithoutExtension(fileName));
            IPackage package;
            return FindFile(fileName, out package);
        }

        public static IPackage FindPackageWithFile(string fileName)
        {
            IPackage package;
            FindFile(fileName, out package);
            return package;
        }

        public static string FindFile(string fileName, out IPackage package)
        {
            KeyValuePair<string, IPackage> result;
            EnsureValidCache();
            lock (_fileCache)
            {
                if (!_fileCache.TryGetValue(fileName, out result))
                {
                    var packageId = GuessPackageId(fileName);
                    foreach (var p in GetAllPackages(packageId))
                    {
                        var file = FindFile(p, fileName);
                        if (file != null)
                        {
                            result = new KeyValuePair<string, IPackage>(file, p);
                            break;
                        }
                    }
                    _fileCache.Add(fileName, result);
                }
            }
            package = result.Value;
            return result.Key;
        }

        private static string GuessPackageId(string fileName)
        {
            var vlImportIndex = fileName.IndexOf(".vlimport", StringComparison.OrdinalIgnoreCase);
            if (vlImportIndex > 0)
                fileName = fileName.Substring(0, vlImportIndex);
            return Path.GetFileNameWithoutExtension(fileName);
        }

        public static string FindFile(IPackage package, string fileName)
        {
            var files = package.GetFiles().OfType<PhysicalPackageFile>();
            foreach (var file in files)
            {
                if (string.Equals(file.EffectivePath, fileName, StringComparison.OrdinalIgnoreCase))
                    return file.SourcePath;
            }
            return null;
        }


        public static string GetPathOfPackage(this IPackage package)
        {
            var packageWithPath = package as IPackageWithPath;
            if (packageWithPath != null)
                return packageWithPath.Path;
            foreach (var file in package.GetFiles().OfType<PhysicalPackageFile>())
                if (string.Equals(Path.GetExtension(file.EffectivePath), Constants.ManifestExtension, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetExtension(file.EffectivePath), Constants.PackageExtension, StringComparison.OrdinalIgnoreCase))
                    return Path.GetDirectoryName(file.SourcePath);
            return null;
        }

        public static IEnumerable<PhysicalPackageAssemblyReference> GetCompatibleAssemblyFiles(this IPackage package)
        {
            if (package.Id == "Microsoft.Net.Compilers")
            {
                var toolFiles = package.GetToolFiles().OfType<PhysicalPackageFile>()
                    .Where(f => Path.GetExtension(f.Path) == ".dll")
                    .Select(p => new PhysicalPackageAssemblyReference(p)).ToArray();
                return toolFiles;
            }
            IEnumerable<PhysicalPackageAssemblyReference> compatibleFiles;
            var files = package.AssemblyReferences.OfType<PhysicalPackageAssemblyReference>();
            if (VersionUtility.TryGetCompatibleItems(ExecutingFrameworkName, files, out compatibleFiles))
                return compatibleFiles;
            if (VersionUtility.TryGetCompatibleItems(VersionUtility.DefaultTargetFramework, files, out compatibleFiles))
                return compatibleFiles;
            return files;
        }

        public static string ReadArgument(this CustomAttributeData attribute, int index)
        {
            return (string)attribute.ConstructorArguments[index].Value;
        }

        public static bool IsAssemblyFile(this IPackageFile file)
        {
            return file.Path.IsAssemblyFile();
        }

        public static bool IsAssemblyFile(this string filePath)
        {
            return !filePath.EndsWith(ResourceAssemblyExtension, StringComparison.OrdinalIgnoreCase) &&
                Constants.AssemblyReferencesExtensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase);
        }
    }
}
