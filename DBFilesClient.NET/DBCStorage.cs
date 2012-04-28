using System;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Collections;
using System.Text;
using System.IO;
using DBFilesClient.NET.Structure;

namespace DBFilesClient.NET
{
    public sealed class DBCStorage : DBCMetaInfo, IStorage
    {
        #region Fields
        readonly EntryLayout m_layout;
        uint m_minId;
        uint m_maxId;
        IntPtr[] m_entries;

        // Fully loaded into memory
        IntPtr m_data;

        // Using stream
        SortedSet<IntPtr> m_allocatedEntries;
        Stream m_stream;
        long m_dataPos;
        long m_poolPos;
        long m_poolSize;
        bool m_streamOwned;

        bool m_disposed;
        #endregion

        #region Constructor
        public DBCStorage(EntryLayout layout)
        {
            if (IntPtr.Size != sizeof(uint))
                throw new InvalidOperationException();

            if (layout == null)
                throw new ArgumentNullException("layout");

            if (layout.Size < 4)
                throw new ArgumentException();

            if (!layout.IsFixed)
            {
                m_layout = layout.Clone();
                m_layout.Fix();
            }
            else
                m_layout = layout;
        }
        #endregion

        #region Loading
        public unsafe void Load(Stream stream, bool completeLoad, bool ownStream)
        {
            if (m_disposed || m_stream != null || m_recordsCount != 0)
                throw new InvalidOperationException();

            long streamPos = stream.Position;

            m_minId = uint.MaxValue;
            m_maxId = uint.MinValue;

            var headerSize = 20;
            var header = new byte[headerSize];
            if (stream.Read(header, 0, headerSize) != headerSize)
            {
                this.Dispose();
                throw new EndOfStreamException();
            }

            if (BitConverter.ToUInt32(header, 0) != 0x43424457)
            {
                this.Dispose();
                throw new ArgumentException("The stream does not contain a valid DBC file.");
            }

            m_recordsCount = BitConverter.ToInt32(header, 4);
            if (BitConverter.ToInt32(header, 12) != m_layout.Size)
            {
                this.Dispose();
                throw new ArgumentException(string.Format(
                    "The specified file layout and stream file layout do not match: {0} vs {1} bytes",
                    m_layout.Size, BitConverter.ToInt32(header, 12)));
            }

            m_poolSize = BitConverter.ToInt32(header, 16);

            m_streamOwned = ownStream;
            int payload = m_recordsCount * m_layout.Size;

            if (completeLoad || m_recordsCount == 0)
            {
                int count = (int)(payload + m_poolSize);
                m_data = Marshal.AllocHGlobal(count);

                const int chunkSize = 4096 * 4;
                var buf = new byte[chunkSize];
                int pos = 0;
                while (count > 0)
                {
                    int read = stream.Read(buf, 0, Math.Min(chunkSize, count));
                    if (read <= 0)
                    {
                        this.Dispose();
                        throw new EndOfStreamException();
                    }

                    Marshal.Copy(buf, pos, m_data + pos, read);

                    count -= read;
                    pos += read;
                }

                byte* ptr;

                ptr = (byte*)m_data.ToPointer();
                uint prevId = 0;
                for (int i = 0; i < m_recordsCount; ++i)
                {
                    uint id = *(uint*)ptr;

                    if (m_minId > id)
                        m_minId = id;
                    if (m_maxId < id)
                        m_maxId = id;

                    if (i != 0)
                    {
                        if (prevId >= id)
                        {
                            this.Dispose();
                            throw new FormatException();
                        }
                    }

                    prevId = id;

                    ptr += m_layout.Size;
                }

                ptr = (byte*)m_data.ToPointer();
                m_entries = new IntPtr[m_maxId - m_minId + 1];
                for (int i = 0; i < m_recordsCount; ++i)
                {
                    LoadEntry(new IntPtr(ptr));

                    uint id = *(uint*)ptr;

                    m_entries[id - m_minId] = new IntPtr(ptr);

                    ptr += m_layout.Size;
                }

                if (ownStream)
                    stream.Close();
            }
            else
            {
                if (!stream.CanSeek)
                {
                    this.Dispose();
                    throw new ArgumentException("The stream cannot seak.", "stream");
                }

                m_stream = stream;
                m_allocatedEntries = new SortedSet<IntPtr>(IntPtrComparer.Comparer);
                m_dataPos = stream.Position;
                m_poolPos = m_dataPos + payload;

                var buf = new byte[4];

                uint prevId = 0;
                for (int i = 0; i < m_recordsCount; ++i)
                {
                    if (stream.Read(buf, 0, 4) != 4)
                    {
                        this.Dispose();
                        throw new EndOfStreamException();
                    }

                    uint id = BitConverter.ToUInt32(buf, 0);

                    if (m_minId > id)
                        m_minId = id;
                    if (m_maxId < id)
                        m_maxId = id;

                    if (i != 0)
                    {
                        if (prevId >= id)
                        {
                            this.Dispose();
                            throw new FormatException();
                        }
                    }

                    prevId = id;

                    if (i + 1 < m_recordsCount)
                        m_stream.Position += m_layout.Size - 4;
                }

                stream.Position = m_dataPos;
                m_entries = new IntPtr[m_maxId - m_minId + 1];
                for (int i = 0; i < m_recordsCount; ++i)
                {
                    if (stream.Read(buf, 0, 4) != 4)
                    {
                        this.Dispose();
                        throw new EndOfStreamException();
                    }

                    uint id = BitConverter.ToUInt32(buf, 0);

                    m_entries[id - m_minId] = new AlignedPointerOrUInt32((uint)stream.Position - 4).ToIntPtr();

                    if (i + 1 < m_recordsCount)
                        m_stream.Position += m_layout.Size - 4;
                }

                if (!ownStream)
                    m_stream.Position = streamPos;
            }
        }

        unsafe void LoadEntry(IntPtr entry)
        {
            foreach (var element in m_layout)
            {
                switch (element)
                {
                    case LayoutElement.DoubleWord:
                        entry += 4;
                        break;
                    case LayoutElement.String:
                        if (m_stream != null)
                        {
                            var offset = *(int*)entry.ToPointer();

                            m_stream.Position = m_poolPos + offset;

                            int i = 0;
                            var buf = new byte[4096];

                            while (true)
                            {
                                int b = m_stream.ReadByte();
                                if (b < 0)
                                    throw new EndOfStreamException();

                                if (b == 0)
                                    break;

                                if (i == buf.Length)
                                {
                                    var newBuf = new byte[buf.Length * 2];
                                    for (int j = 0; j < i; j++)
                                        newBuf[j] = buf[i];
                                    buf = newBuf;
                                }

                                buf[i++] = (byte)b;
                            }

                            var ptr = Marshal.AllocHGlobal(i + 1);
                            m_allocatedEntries.Add(ptr);
                            Marshal.Copy(buf, 0, ptr, i);
                            *(byte*)(ptr + i).ToPointer() = 0;
                            *(IntPtr*)entry.ToPointer() = ptr;
                        }
                        else
                        {
                            var offset = *(int*)entry.ToPointer();

                            var ptr = m_data + m_recordsCount * m_layout.Size + offset;

                            *(IntPtr*)entry.ToPointer() = ptr;
                        }

                        entry += IntPtr.Size;
                        break;
                    default:
                        throw new InvalidOperationException();
                }
            }
        }
        #endregion

        public void Save(Stream stream)
        {
            throw new NotImplementedException();
        }

        public unsafe IntPtr GetEntry(uint id)
        {
            this.CheckDisposed();

            if (m_recordsCount == 0)
                return IntPtr.Zero;

            if (id < m_minId || id > m_maxId)
                return IntPtr.Zero;

            if (m_stream != null)
            {
                var ptrOrUInt32 = new AlignedPointerOrUInt32(m_entries[id - m_minId]);

                if (ptrOrUInt32.IsPointer)
                    return new IntPtr(ptrOrUInt32.ToPointer());

                var size = m_layout.Size;
                var buf = new byte[size];

                m_stream.Position = ptrOrUInt32.ToUInt32();
                if (m_stream.Read(buf, 0, size) != size)
                    throw new EndOfStreamException();

                var ptr = Marshal.AllocHGlobal(size);
                m_allocatedEntries.Add(ptr);
                Marshal.Copy(buf, 0, ptr, size);

                m_entries[id - m_minId] = new AlignedPointerOrUInt32((void*)ptr).ToIntPtr();
                LoadEntry(ptr);
                return ptr;
            }
            else
            {
                return m_entries[id - m_minId];
            }
        }

        public IntPtr AddEntry(uint id)
        {
            throw new NotImplementedException();
        }

        public void Dispose()
        {
            if (m_disposed)
                return;

            if (m_stream != null)
            {
                if (m_streamOwned)
                    m_stream.Close();

                m_stream = null;
            }

            if (m_data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(m_data);
                m_data = IntPtr.Zero;
            }

            if (m_allocatedEntries != null)
            {
                foreach (var ptr in m_allocatedEntries)
                    Marshal.FreeHGlobal(ptr);

                m_allocatedEntries.Clear();
                m_allocatedEntries = null;
            }

            m_disposed = true;
        }

        public int EntriesCount { get { return m_recordsCount; } }
        public int EntrySize { get { return m_layout.Size; } }

        public uint MinId
        {
            get
            {
                this.CheckDisposed();

                if (m_recordsCount == 0)
                    throw new InvalidOperationException();

                return m_minId;
            }
        }

        public uint MaxId
        {
            get
            {
                this.CheckDisposed();

                if (m_recordsCount == 0)
                    throw new InvalidOperationException();

                return m_maxId;
            }
        }

        void CheckDisposed()
        {
            if (m_disposed)
                throw new ObjectDisposedException("DBCStorage");
        }
    }
}
