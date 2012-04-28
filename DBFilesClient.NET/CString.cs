using System;

namespace DBFilesClient.NET
{
    public struct CString
    {
        IntPtr m_ptr;

        public CString(IntPtr ptr)
        {
            m_ptr = ptr;
        }

        public IntPtr ToIntPtr()
        {
            return m_ptr;
        }

        public unsafe sbyte* ToPointer()
        {
            return (sbyte*)m_ptr.ToPointer();
        }

        public unsafe override string ToString()
        {
            return new string(this.ToPointer());
        }
    }
}
