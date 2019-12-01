using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace VVVV.NuGetAssemblyLoader
{
    // Taken from https://github.com/microsoft/vs-mef/blob/master/src/Microsoft.VisualStudio.Composition/ByValueEquality+AssemblyNameComparer.cs
    class AssemblyNameComparer : IEqualityComparer<AssemblyName>
    {
        public static readonly AssemblyNameComparer Default = new AssemblyNameComparer();

        public bool Equals(AssemblyName x, AssemblyName y)
        {
            if (x == null ^ y == null)
            {
                return false;
            }

            if (x == null)
            {
                return true;
            }

            // There are some cases where two AssemblyNames who are otherwise equivalent
            // have a null PublicKey but a correct PublicKeyToken, and vice versa. We should
            // compare the PublicKeys first, but then fall back to GetPublicKeyToken(), which
            // will generate a public key token for the AssemblyName that has a public key and
            // return the public key token for the other AssemblyName.
            byte[] xPublicKey = x.GetPublicKey();
            byte[] yPublicKey = y.GetPublicKey();

            // Testing on FullName is horrifically slow.
            // So test directly on its components instead.
            if (xPublicKey != null && yPublicKey != null)
            {
                return x.Name == y.Name
                    && x.Version.Equals(y.Version)
                    && string.Equals(x.CultureName, y.CultureName)
                    && BufferComparer.Default.Equals(xPublicKey, yPublicKey);
            }

            return x.Name == y.Name
                && Equals(x.Version, y.Version)
                && string.Equals(x.CultureName, y.CultureName)
                && BufferComparer.Default.Equals(x.GetPublicKeyToken(), y.GetPublicKeyToken());
        }

        public int GetHashCode(AssemblyName obj)
        {
            return obj.Name.GetHashCode();
        }
    }

    class BufferComparer : IEqualityComparer<byte[]>
    {
        internal static readonly BufferComparer Default = new BufferComparer();

        private BufferComparer()
        {
        }

        public bool Equals(byte[] x, byte[] y)
        {
            if (x == y)
            {
                return true;
            }

            if (x == null ^ y == null)
            {
                return false;
            }

            if (x.Length != y.Length)
            {
                return false;
            }

            for (int i = 0; i < x.Length; i++)
            {
                if (x[i] != y[i])
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(byte[] obj)
        {
            throw new NotImplementedException();
        }
    }
}
