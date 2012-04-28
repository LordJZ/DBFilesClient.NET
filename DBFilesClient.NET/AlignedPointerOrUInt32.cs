using System;

namespace DBFilesClient.NET
{
    internal struct AlignedPointerOrUInt32
    {
        IntPtr m_intptr;

        internal AlignedPointerOrUInt32(IntPtr intptr)
        {
            m_intptr = intptr;
        }

        internal unsafe AlignedPointerOrUInt32(void* ptr)
        {
            m_intptr = new IntPtr(ptr);

            if (!this.IsPointer)
                throw new InvalidOperationException();
        }

        internal AlignedPointerOrUInt32(uint value)
        {
            if ((value & (1U << 31)) != 0)
                throw new ArgumentException();

            m_intptr = new IntPtr((value << 1) | 1);

            if (this.IsPointer)
                throw new InvalidOperationException();
        }

        internal bool IsPointer { get { return (m_intptr.ToInt64() & 1) == 0; } }

        internal IntPtr ToIntPtr()
        {
            return m_intptr;
        }

        internal unsafe void* ToPointer()
        {
            if (!this.IsPointer)
                throw new InvalidOperationException();

            return m_intptr.ToPointer();
        }

        internal uint ToUInt32()
        {
            if (this.IsPointer)
                throw new InvalidOperationException();

            return (uint)((ulong)m_intptr.ToInt64() >> 1);
        }
    }
}
