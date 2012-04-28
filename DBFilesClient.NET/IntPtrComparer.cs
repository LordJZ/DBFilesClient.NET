using System;
using System.Collections.Generic;

namespace DBFilesClient.NET
{
    public struct IntPtrComparer : IComparer<IntPtr>, IEqualityComparer<IntPtr>
    {
        static readonly object s_instance = new IntPtrComparer();

        public static readonly IComparer<IntPtr> Comparer = (IComparer<IntPtr>)s_instance;
        public static readonly IEqualityComparer<IntPtr> EqualityComparer = (IEqualityComparer<IntPtr>)s_instance;

        public static int Compare(IntPtr x, IntPtr y)
        {
            if (Environment.Is64BitProcess)
                return x.ToInt64().CompareTo(y.ToInt64());

            return x.ToInt32().CompareTo(y.ToInt32());
        }

        public static bool Equals(IntPtr x, IntPtr y)
        {
            if (Environment.Is64BitProcess)
                return x.ToInt64() == y.ToInt64();

            return x.ToInt32() == y.ToInt32();
        }

        int IComparer<IntPtr>.Compare(IntPtr x, IntPtr y)
        {
            return Compare(x, y);
        }

        bool IEqualityComparer<IntPtr>.Equals(IntPtr x, IntPtr y)
        {
            return Equals(x, y);
        }

        int IEqualityComparer<IntPtr>.GetHashCode(IntPtr obj)
        {
            return obj.GetHashCode();
        }
    }
}
