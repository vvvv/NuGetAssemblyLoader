using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace VVVV.NuGetAssemblyLoader.TestApp
{
    class Program
    {
        static void Main(string[] args)
        {
            var repoPath = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(Assembly.GetEntryAssembly().Location), "..", "..", "..", ".."));
            AssemblyLoader.AddPackageRepository(repoPath);
            foreach (var package in AssemblyLoader.Repository.GetPackages().OfType<SrcPackageWithNuspec>())
                Console.WriteLine($"Found {package} in {package.NuspecFile}");
            Console.ReadLine();
        }
    }
}
