using NuGet;
using System.Text;

namespace NuGetAssemblyLoader
{
    public static class PackageHelpers
    {
        public static string GetInfoString(this IPackage package)
        {
            var b = new StringBuilder();
            b.AppendLineIfNotNull(package.Title);
            b.AppendLineIfNotNull(package.Description);
            b.AppendLineIfNotNull(string.Join(", ", package.Authors));
            b.AppendLineIfNotNull(package.Version?.ToNormalizedString());
            b.AppendLineIfNotNull(package.LicenseUrl?.ToString());
            b.AppendLineIfNotNull(package.Copyright);
            return b.ToString();
        }

        private static void AppendLineIfNotNull(this StringBuilder b, string s)
        {
            if (s != null)
                b.AppendLine(s);
        }
    }
}
