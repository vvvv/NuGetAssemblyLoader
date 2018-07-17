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

namespace NuGetAssemblyLoader
{
    public interface IPackageWithPath : IPackage { string Path { get; } }

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

    public class SrcPackageRepository : PackageRepositoryBase
    {
        readonly DirectoryInfo _repositoryFolder;
        readonly IFileSystem _fileSystem;
        IQueryable<IPackage> _packages;

        public SrcPackageRepository(DirectoryInfo repositoryFolder)
        {
            _repositoryFolder = repositoryFolder;
            _fileSystem = new PhysicalFileSystem(repositoryFolder.FullName);
        }

        public override string Source => _repositoryFolder.FullName;

        public override bool SupportsPrereleasePackages => true;

        public override IQueryable<IPackage> GetPackages()
        {
            return Packages;
        }

        private IQueryable<IPackage> Packages
        {
            get
            {
                if (_packages == null)
                    _packages = GetPackagesCore().ToList().AsQueryable();
                return _packages;
            }
        }

        private IEnumerable<IPackage> GetPackagesCore()
        {
            foreach (var dir in _fileSystem.GetDirectories(string.Empty))
            {
                var hasNuspec = false;
                foreach (var nuspecFile in _fileSystem.GetFiles(dir, $"{dir}{Constants.ManifestExtension}"))
                {
                    hasNuspec = true;
                    yield return new SrcPackageWithNuspec(_fileSystem, dir, nuspecFile);
                    break;
                }
                if (!hasNuspec)
                {
                    foreach (var vlFile in _fileSystem.GetFiles(dir, $"{dir}.vl"))
                    {
                        yield return new SrcPackageWithoutNuspec(_fileSystem, dir, vlFile);
                        break;
                    }
                }
            }
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

    public class InstalledPackageRepository : PackageRepositoryBase
    {
        readonly IFileSystem _fileSystem;
        readonly FileSystemWatcher _watcher;
        IQueryable<IPackage> _packages;
        Timer _delayTimer = new Timer(2000) { AutoReset = false };

        public InstalledPackageRepository(DirectoryInfo repositoryFolder)
        {
            _fileSystem = new PhysicalFileSystem(repositoryFolder.FullName);
            _watcher = new FileSystemWatcher(repositoryFolder.FullName);
            _watcher.NotifyFilter = NotifyFilters.DirectoryName;
            _watcher.Created += HandlePathChanged;
            _watcher.Deleted += HandlePathChanged;
            _watcher.Changed += HandlePathChanged;
            _watcher.EnableRaisingEvents = true;

            _delayTimer.Elapsed += _delayTimer_Elapsed;
        }

        private void HandlePathChanged(object sender, FileSystemEventArgs e)
        {
            _delayTimer.Stop();
            _delayTimer.Start();
        }

        private void _delayTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            // Reset the cache
            _packages = null;

            AssemblyLoader.InvalidateCache();
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
            foreach (var dir in _fileSystem.GetDirectories(string.Empty))
            {
                foreach (var file in _fileSystem.GetFiles(dir, "*"))
                {
                    var pkg = default(IPackage);
                    var ext = Path.GetExtension(file);
                    if (ext == Constants.PackageExtension)
                        pkg = new InstalledNupkgPackage(_fileSystem, dir, file);
                    else if (ext == Constants.ManifestExtension)
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
                foreach (var repo in _repository.Repositories.OfType<SrcPackageRepository>())
                {
                    result = repo.FindPackage(packageId, version);
                    if (result != null)
                        break;
                }
                if (result == null)
                {
                    foreach (var repo in _repository.Repositories.Where(r => !(r is SrcPackageRepository)))
                    {
                        result = repo.FindPackage(packageId, version);
                        if (result != null)
                            break;
                    }
                }
                _packageCache.Add(key, result);
            }
            return result;
        }

        public IEnumerable<IPackage> FindPackagesById(string packageId)
        {
            var result = _repository.Repositories.OfType<SrcPackageRepository>().SelectMany(r => r.FindPackagesById(packageId));
            if (result.Any())
                return result;
            return _repository.FindPackagesById(packageId);
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
            var fs = new PhysicalFileSystem(packageSource);
            // package-version/package-version.nupkg
            if (fs.GetDirectories(string.Empty).Any(d => fs.GetFiles(d, $"{d}{Constants.PackageExtension}").Any()))
                return new InstalledPackageRepository(new DirectoryInfo(packageSource));
            // package-version/package.nuspec
            if (fs.GetDirectories(string.Empty).Take(1).Any(d => fs.GetFiles(d, $"*{Constants.ManifestExtension}").Any(f => Path.GetFileName(f) != $"{d}{Constants.ManifestExtension}")))
                return new InstalledPackageRepository(new DirectoryInfo(packageSource));
            // package/package.nuspec or package/package.vl
            return new SrcPackageRepository(new DirectoryInfo(packageSource));
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
        static readonly Dictionary<string, string> _packageAssemblyCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, KeyValuePair<string, IPackage>> _fileCache = new Dictionary<string, KeyValuePair<string, IPackage>>(StringComparer.OrdinalIgnoreCase);
        static Dictionary<string, Assembly> _loadedAssemblyCache;
        static readonly List<string> _packageRepositories = new List<string>();
        static PreferSourceOverInstalledAggregateRepository _repository;
        static FrameworkName _frameworkName;
        static volatile bool _cacheIsValid;

        static AssemblyLoader()
        {
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
            AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += CurrentDomain_ReflectionOnlyAssemblyResolve;
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
                        var localPath = GetLocalPath(assemblyName);
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

        public static string[] ParseLines(string[] lines, string key)
        {
            var sourcesIndex = Array.IndexOf(lines, key);
            if (sourcesIndex >= 0 && lines.Length > sourcesIndex + 1)
            {
                var sourcesString = lines[sourcesIndex + 1];
                var paths = sourcesString.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < paths.Length; i++)
                {
                    paths[i] = paths[i].Trim('"', '\\');
                }
                return paths;
            }
            return Array.Empty<string>();
        }

        public static string[] ParseCommandLine(string key)
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

        static readonly Dictionary<string, IPackage> _packages = new Dictionary<string, IPackage>();

        // Repository.FindPackage is incredibly slow (~15ms for each query)
        public static IPackage FindPackageAndCacheResult(string id)
        {
            IPackage result;
            if (!_packages.TryGetValue(id, out result))
            {
                result = Repository.FindPackage(id);
                _packages.Add(id, result);
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

        static Version GetClrVersion()
        {
            // Will probably only work for .NET 4.5 and up :/
            var versionInfo = FileVersionInfo.GetVersionInfo(typeof(object).Assembly.Location);
            var buildNumer = int.Parse(versionInfo.FileBuildPart.ToString().Substring(0, 1));
            return new Version(versionInfo.FileMajorPart, versionInfo.FileMinorPart, buildNumer);
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
            string nativePath;
            if (TryGetNativePath(package, out nativePath))
            {
                var PATH = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                if (!PATH.Contains(nativePath))
                    Environment.SetEnvironmentVariable("PATH", PATH + Path.PathSeparator + nativePath);
            }
        }

        public static void AddPathToBeAwareOfWhenSearchingForNativeDlls(string path)
        {
            EnsureValidCache();
            var nativePath = Path.Combine(path, AppendProcessArchitecture("lib-native")).ToString();
            if (Directory.Exists(nativePath))
            {
                var PATH = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                if (!PATH.Contains(nativePath))
                    Environment.SetEnvironmentVariable("PATH", PATH + Path.PathSeparator + nativePath);
            }
        }

        static bool TryGetNativePath(IPackage package, out string absoluteNativeLibDir)
        {
            return TryGetNativePath(package, "lib-native", out absoluteNativeLibDir) 
                || TryGetNativePath(package, "NativeDlls", out absoluteNativeLibDir);
        }

        static bool TryGetNativePath(IPackage package, string nativeLibBaseDir, out string absoluteNativeLibDir)
        {
            var nativeLibDir = AppendProcessArchitecture(nativeLibBaseDir);
            var nativeFile = package.GetFiles(nativeLibDir).FirstOrDefault() as PhysicalPackageFile;
            if (nativeFile != null)
            {
                absoluteNativeLibDir = Path.GetDirectoryName(nativeFile.SourcePath);
                return true;
            }
            else
            {
                absoluteNativeLibDir = null;
                return false;
            }
        }

        static string AppendProcessArchitecture(string path) => Path.Combine(path, Environment.Is64BitProcess ? "x64" : "x86");

        private static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            var referencedAssemblyName = new AssemblyName(args.Name);
            if (referencedAssemblyName.Name.EndsWith(".resources"))
                return null;
            var assemblyName = ProbeAssemblyReference(referencedAssemblyName);
            if (assemblyName != null)
                return Assembly.LoadFrom(assemblyName.CodeBase);
            return null;
        }

        private static Assembly CurrentDomain_ReflectionOnlyAssemblyResolve(object sender, ResolveEventArgs args)
        {
            var referencedAssemblyName = new AssemblyName(args.Name);
            if (referencedAssemblyName.Name.EndsWith(".resources"))
                return null;
            var assemblyName = ProbeAssemblyReference(referencedAssemblyName);
            if (assemblyName != null)
                return Assembly.ReflectionOnlyLoadFrom(assemblyName.CodeBase);
            return null;
        }

        public static AssemblyName ProbeAssemblyReference(AssemblyName referencedAssemblyName)
        {
            var assemblyFile = FindAssemblyFile(referencedAssemblyName.Name);
            if (assemblyFile != null)
                return AssemblyName.GetAssemblyName(assemblyFile);
            return null;
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
                return GetLocalPath(loadedAssembly.GetName());

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

        /// <summary>
        /// Returns the CodeBase of the named assembly (which is a URL), except if the URL has the file scheme.
        /// In that case the URL is converted to a local file path that can be used by System.IO.Path methods.
        /// </summary>
        /// <remarks>Taken from https://ccimetadata.codeplex.com </remarks>
        /// <param name="assemblyName">The name of the assembly whose location is desired.</param>
        public static string GetLocalPath(AssemblyName assemblyName)
        {
            var loc = assemblyName.CodeBase;
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
