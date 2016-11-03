using System;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.Versioning;
using NuGet;
using System.Diagnostics;

namespace NuGetAssemblyLoader
{
    public interface IPackageWithPath : IPackage { string Path { get; } }

    public class SrcPackage : IPackageWithPath
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

        readonly IFileSystem _repositoryFileSystem;
        readonly string _packageName;
        readonly string _nuspecFile;
        PackageBuilder _builder;
        List<PhysicalPackageFile> _files;

        public SrcPackage(IFileSystem repositoryFileSystem, string packageName, string nuspecFile)
        {
            _repositoryFileSystem = repositoryFileSystem;
            _packageName = packageName;
            _nuspecFile = nuspecFile;
        }

        public string Path => _repositoryFileSystem.GetFullPath(_packageName);

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
                    catch (FileNotFoundException e)
                    {
                        // Create a dummy
                        _builder = new PackageBuilder();
                        Trace.TraceWarning($"The NuSpec file {path} seems to be corrupt: {e.Message}");
                    }
                }
                return _builder;
            }
        }

        public bool IsAbsoluteLatestVersion => true;
        public bool IsLatestVersion => true;
        public bool Listed => false;
        public DateTimeOffset? Published => null;
        public IEnumerable<IPackageAssemblyReference> AssemblyReferences => GetAssemblyReferencesCore();
        public string Title => Builder.Title;
        public IEnumerable<string> Authors => Builder.Authors;
        public IEnumerable<string> Owners => Builder.Owners;
        public Uri IconUrl => Builder.IconUrl;
        public Uri LicenseUrl => Builder.LicenseUrl;
        public Uri ProjectUrl => Builder.ProjectUrl;
        public bool RequireLicenseAcceptance => Builder.RequireLicenseAcceptance;
        public bool DevelopmentDependency => Builder.DevelopmentDependency;
        public string Description => Builder.Description;
        public string Summary => Builder.Summary;
        public string ReleaseNotes => Builder.ReleaseNotes;
        public string Language => Builder.Language;
        public string Tags => string.Join(", ", Builder.Tags);
        public string Copyright => Builder.Copyright;
        public IEnumerable<FrameworkAssemblyReference> FrameworkAssemblies => Builder.FrameworkReferences;
        public ICollection<PackageReferenceSet> PackageAssemblyReferences => Builder.PackageAssemblyReferences;
        public IEnumerable<PackageDependencySet> DependencySets => Builder.DependencySets;
        public Version MinClientVersion => Builder.MinClientVersion;
        public string Id => Builder.Id ?? _packageName;
        public SemanticVersion Version => Builder.Version ?? new SemanticVersion(0, 0, 0, 0);
        public Uri ReportAbuseUrl => null;
        public int DownloadCount => -1;

        public IEnumerable<FrameworkName> GetSupportedFrameworks()
        {
            return Builder.FrameworkReferences.SelectMany(f => f.SupportedFrameworks)
                .Concat(Files.SelectMany(f => f.SupportedFrameworks))
                .Distinct();
        }

        protected IEnumerable<IPackageAssemblyReference> GetAssemblyReferencesCore()
        {
            foreach (var file in Files)
            {
                if (!DummyLocalPackage.IsAssemblyReference(file.Path))
                    continue;
                yield return new PhysicalPackageAssemblyReference
                {
                    SourcePath = file.SourcePath,
                    TargetPath = file.TargetPath
                };
            }
        }

        public IEnumerable<IPackageFile> GetFiles()
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

        public Stream GetStream()
        {
            throw new NotImplementedException();
        }

        public void ExtractContents(IFileSystem fileSystem, string extractPath)
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
        List<IPackage> _packages;

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
                    _packages = GetPackagesCore().ToList();
                return _packages.AsQueryable();
            }
        }

        private IEnumerable<IPackage> GetPackagesCore()
        {
            foreach (var dir in _fileSystem.GetDirectories(string.Empty))
            {
                foreach (var nuspecFile in _fileSystem.GetFiles(dir, $"{dir}{Constants.ManifestExtension}"))
                {
                    yield return new SrcPackage(_fileSystem, dir, nuspecFile);
                    break;
                }
            }
        }
    }

    public class InstalledPackage : ZipPackage, IPackageWithPath
    {
        readonly IFileSystem _packageFileSystem;
        List<PhysicalPackageFile> _files;

        public InstalledPackage(IFileSystem repositoryFileSystem, string folder, string nupkgFile)
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

    public class SpecialInstalledPackage : InstalledPackage
    {
        readonly string _specialLibFolder;

        public SpecialInstalledPackage(IFileSystem repositoryFileSystem, string folder, string nupkgFile, string specialLibFolder)
            : base(repositoryFileSystem, folder, nupkgFile)
        {
            _specialLibFolder = specialLibFolder;
        }

        protected override IEnumerable<IPackageAssemblyReference> GetAssemblyReferencesCore()
        {
            foreach (var file in base.GetAssemblyReferencesCore())
                yield return file;
            foreach (var file in Files)
            {
                if (!file.IsAssemblyFile())
                    continue;
                if (!file.EffectivePath.StartsWith(_specialLibFolder))
                    continue;
                yield return new PhysicalPackageAssemblyReference()
                {
                    SourcePath = file.SourcePath,
                    TargetPath = file.TargetPath
                };
            }
        }
    }

    public class InstalledPackageRepository : PackageRepositoryBase
    {
        readonly IFileSystem _fileSystem;
        List<IPackage> _packages;

        public InstalledPackageRepository(DirectoryInfo repositoryFolder)
        {
            _fileSystem = new PhysicalFileSystem(repositoryFolder.FullName);
        }

        public override string Source => _fileSystem.Root;
        public override bool SupportsPrereleasePackages => true;
        public override IQueryable<IPackage> GetPackages()
        {
            if (_packages == null)
                _packages = GetPackagesCore().ToList();
            return _packages.AsQueryable();
        }

        private IEnumerable<IPackage> GetPackagesCore()
        {
            foreach (var dir in _fileSystem.GetDirectories(string.Empty))
            {
                foreach (var nupkgFile in _fileSystem.GetFiles(dir, $"{dir}{Constants.PackageExtension}"))
                {
                    IPackage pkg;
                    if (dir.StartsWith("SharpDX"))
                        pkg = new SpecialInstalledPackage(_fileSystem, dir, nupkgFile, @"Bin\DirectX11-Signed-net40");
                    else
                        pkg = new InstalledPackage(_fileSystem, dir, nupkgFile);
                    if (pkg.MinClientVersion != null && Constants.NuGetVersion < pkg.MinClientVersion)
                        Trace.TraceWarning($"Ignoring package {pkg} because it requires NuGet {pkg.MinClientVersion}");
                    else
                        yield return pkg;
                    break;
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
            var packages = _repository.GetPackages().ToList();
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
            if (fs.GetDirectories(string.Empty).Any(d => fs.GetFiles(d, $"{d}{Constants.PackageExtension}").Any()))
                return new InstalledPackageRepository(new DirectoryInfo(packageSource));
            return new SrcPackageRepository(new DirectoryInfo(packageSource));
        }
    }

    /// <summary>
    /// Manage the environment path variable to find native .dll files
    /// </summary>
    public static class EnvironmentPath
    {
        public static string GetPath => Environment.GetEnvironmentVariable("PATH") ?? String.Empty;
        public static void AddToPath(string path) => Environment.SetEnvironmentVariable("PATH", GetPath + Path.PathSeparator + path);
        static HashSet<IPackage> AddedPackages = new HashSet<IPackage>();

        /// <summary>
        /// Adds the 'lib-native' folder of a package to the 'PATH' environment variable if it exists
        /// and the packages was not already added.
        /// Assumes that 'Source Packages' will be added before 'Installed Packages', does no priority logic.
        /// </summary>
        /// <param name="package">The package to check</param>
        public static void AddPackage(IPackage package)
        {
            if (!AddedPackages.Add(package)) return;

            var nativeFile = package.GetFiles(Path.Combine("lib-native", Environment.Is64BitProcess ? "x64" : "x86")).OfType<PhysicalPackageFile>().FirstOrDefault();
            if (nativeFile != null)
                AddToPath(Path.GetDirectoryName(nativeFile.SourcePath));
        }
    }


    public static class AssemblyLoader
    {
        private const string ResourceAssemblyExtension = ".resources.dll";
        static readonly HashSet<IPackage> _cachedPackages = new HashSet<IPackage>();
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
            AppDomain.CurrentDomain.AssemblyLoad += CurrentDomain_AssemblyLoad;
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
                        }
                    }
                    _loadedAssemblyCache = result;
                }
                return _loadedAssemblyCache;
            }
        }

        private static void CurrentDomain_AssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            // Invalidate cache
            _loadedAssemblyCache = null;

            var assembly = args.LoadedAssembly;
            if (assembly.GlobalAssemblyCache || assembly.IsDynamic) return;

            var package = FindPackageWithFile(Path.GetFileName(assembly.Location));
            if (package != null)
                EnvironmentPath.AddPackage(package);
        }

        public static string[] ParseCommandLine(string key)
        {
            var commandLineArgs = Environment.GetCommandLineArgs();
            var sourcesIndex = Array.IndexOf(commandLineArgs, key);
            if (sourcesIndex >= 0 && commandLineArgs.Length > sourcesIndex + 1)
            {
                var sourcesString = commandLineArgs[sourcesIndex + 1];
                var paths = sourcesString.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i<paths.Length; i++)
                {
                    paths[i] = paths[i].Trim('"', '\\');
                }
                return paths;
            }
            return Array.Empty<string>();
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

        private static void EnsureValidCache()
        {
            if (!_cacheIsValid)
                CacheFiles(Repository);
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
                    var sorter = new HighestPackageSorter(ExecutingFrameworkName, repository);
                    foreach (var p in sorter.GetPackagesByDependencyOrder(repository))
                    {
                        if (!_cachedPackages.Add(p))
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
                var assemblyName = Path.GetFileNameWithoutExtension(assemblyFile.Name);
                if (!_packageAssemblyCache.ContainsKey(assemblyName))
                    _packageAssemblyCache.Add(assemblyName, assemblyFile.SourcePath);
            }
        }

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
            string result;
            EnsureValidCache();
            lock (_packageAssemblyCache)
            {
                if (!_packageAssemblyCache.TryGetValue(assemblyName, out result))
                {
                    foreach (var package in GetAllPackages(assemblyName))
                    {
                        result = FindAssemblyFile(package, assemblyName);
                        if (result != null)
                            break;
                    }
                    _packageAssemblyCache.Add(assemblyName, result);
                }
            }
            return result;
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

        public static string FindAssemblyFile(IPackage package, string assemblyName)
        {
            IEnumerable<PhysicalPackageAssemblyReference> files = package.AssemblyReferences.OfType<PhysicalPackageAssemblyReference>();
            IEnumerable<PhysicalPackageAssemblyReference> compatibleFiles;
            if (!VersionUtility.TryGetCompatibleItems(ExecutingFrameworkName, files, out compatibleFiles))
                compatibleFiles = files;
            return compatibleFiles.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f.Name) == assemblyName)?.SourcePath;
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
            IEnumerable<PhysicalPackageAssemblyReference> compatibleFiles;
            if (VersionUtility.TryGetCompatibleItems(ExecutingFrameworkName, package.AssemblyReferences.OfType<PhysicalPackageAssemblyReference>(), out compatibleFiles))
                return compatibleFiles;
            return Enumerable.Empty<PhysicalPackageAssemblyReference>();
        }

        public static IEnumerable<PhysicalPackageFile> GetCompatibleImportFiles(this IPackage package)
        {
            var importFiles = package.GetFiles().OfType<PhysicalPackageFile>().Where(f => string.Equals(Path.GetExtension(f.Path), ".vlimport", StringComparison.OrdinalIgnoreCase));
            IEnumerable<PhysicalPackageFile> compatibleFiles;
            if (VersionUtility.TryGetCompatibleItems(ExecutingFrameworkName, importFiles, out compatibleFiles))
                return compatibleFiles;
            return Enumerable.Empty<PhysicalPackageFile>();
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
