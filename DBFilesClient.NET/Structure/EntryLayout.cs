using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections;
using System.Reflection;

namespace DBFilesClient.NET.Structure
{
    public class EntryLayout : ICollection<LayoutElement>, ICloneable
    {
        #region Fields
        // Size of an entry in bytes.
        List<LayoutElement> m_elements;
        int m_size;
        bool m_fixed;
        #endregion

        #region Constructors
        internal EntryLayout(int dummy)
        {
        }

        /// <summary>
        /// Initialized a new empty instance of <see cref="DBFilesClient.NET.Structure.EntryLayout"/> class.
        /// </summary>
        public EntryLayout()
        {
            m_elements = new List<LayoutElement>();
        }

        /// <summary>
        /// Creates a new instance of <see cref="DBFilesClient.NET.Structure.EntryLayout"/> class
        /// from the specified MaNGOS-style format definition.
        /// </summary>
        /// <param name="fmt">
        /// MaNGOS-style format definition.
        /// </param>
        /// <exception cref="System.ArgumentNullException">
        /// <c>fmt</c> is <c>null</c>.
        /// </exception>
        /// <exception cref="System.ArgumentException">
        /// <c>fmt</c> is not a correct MaNGOS-style format definition.
        /// </exception>
        /// <exception cref="System.NotSupportedException">
        /// <c>fmt</c> contains an element that is not supported.
        /// </exception>
        public EntryLayout(string fmt)
        {
            if (fmt == null)
                throw new ArgumentNullException("fmt");

            int length = fmt.Length;
            m_elements = new List<LayoutElement>(length);
            for (int i = 0; i < length; i++)
			{
                char c = fmt[i];

                switch (c)
                {
                    case 'x':
                    case 'X':
                    case 'b':
                    case 'l':
                        throw new NotSupportedException("Element '" + c + "' is not supported.");
                    case 's':
                        this.AddElement(LayoutElement.String);
                        break;
                    case 'd':
                    case 'n':
                        if (i != 0)
                            throw new NotSupportedException("Element '" + c + "' at non-first position is not supported.");
                        goto case 'i';
                    case 'i':
                    case 'f':
                        this.AddElement(LayoutElement.DoubleWord);
                        break;
                    default:
                        throw new ArgumentException("Invalid element '" + c + "'.");
                }
            }
        }

        public EntryLayout(IList<LayoutElement> elements)
        {
            if (elements == null)
                throw new ArgumentNullException("fmt");

            int length = elements.Count;
            m_elements = new List<LayoutElement>(length);
            for (int i = 0; i < length; i++)
            {
                var e = elements[i];

                switch (e)
                {
                    case LayoutElement.String:
                    case LayoutElement.DoubleWord:
                        this.AddElement(e);
                        break;
                    default:
                        throw new ArgumentException("Invalid element '" + e + "'.");
                }
            }
        }

        public EntryLayout(Type entryType)
        {
            if (entryType == null)
                throw new ArgumentNullException("entryType");

            if (!entryType.IsValueType)
                throw new ArgumentException("entryType must be a value type.");

            // We should be able to take a pointer to our type.
            try
            {
                entryType.MakePointerType();
            }
            catch (Exception e)
            {
                throw new ArgumentException("Cannot take a pointer to entryType.", e);
            }

            var fields = entryType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var length = fields.Length;
            if (length == 0)
                throw new ArgumentException("No fields found for entryType.");

            m_elements = new List<LayoutElement>(length);

            for (int i = 0; i < length; ++i)
            {
                var field = fields[i];
                var type = field.FieldType;

                if (!type.IsValueType)
                    throw new ArgumentException("Field " + field.Name + " must be a value type.");

                // We should be able to take a pointer to field type.
                try
                {
                    type.MakePointerType();
                }
                catch (Exception e)
                {
                    throw new ArgumentException("Cannot take a pointer to field " + field.Name + ".", e);
                }

                if (type == typeof(CString))
                    this.AddElement(LayoutElement.String);
                else
                {
                    switch (Marshal.SizeOf(type))
                    {
                        case 4:
                            this.AddElement(LayoutElement.DoubleWord);
                            break;
                        default:
                            throw new ArgumentException("Unknown field " + field.Name + ".");
                    }
                }
            }
        }
        #endregion

        #region Accessors
        /// <summary>
        /// Gets the entry size in bytes.
        /// </summary>
        public int Size { get { return m_size; } }

        /// <summary>
        /// Gets the number of elements in an entry.
        /// </summary>
        public int ElementsCount { get { return m_elements.Count; } }

        /// <summary>
        /// Gets the value indicating whether the current
        /// <see cref="DBFilesClient.NET.Structure.EntryLayout"/> is fixed or not.
        /// </summary>
        public bool IsFixed { get { return m_fixed; } }
        #endregion

        #region Methods
        /// <summary>
        /// Adds a new <see cref="DBFilesClient.NET.Structure.LayoutElement"/>
        /// into the current
        /// <see cref="DBFilesClient.NET.Structure.EntryLayout"/>.
        /// </summary>
        /// <param name="element">
        /// <c>element</c> is not a valid <see cref="DBFilesClient.NET.Structure.EntryLayout"/>.
        /// </param>
        public void AddElement(LayoutElement element)
        {
            if (!this.TryAddValue(element))
                throw new ArgumentException("element");
        }

        /// <summary>
        /// Fixes the <see cref="DBFilesClient.NET.Structure.EntryLayout"/>
        /// so it can no longer be changed.
        /// </summary>
        /// <returns>
        /// The current instance.
        /// </returns>
        public EntryLayout Fix()
        {
            m_fixed = true;
            return this;
        }

        /// <summary>
        /// Returns an enumerator that iterates through the <see cref="DBFilesClient.NET.Structure.EntryLayout"/>.
        /// </summary>
        /// <returns>
        /// An enumerator for the <see cref="DBFilesClient.NET.Structure.EntryLayout"/>.
        /// </returns>
        public List<LayoutElement>.Enumerator GetEnumerator()
        {
            return m_elements.GetEnumerator();
        }

        /// <summary>
        /// Returns a shallow copy of the current <see cref="DBFilesClient.NET.Structure.EntryLayout"/>.
        /// The new <see cref="DBFilesClient.NET.Structure.EntryLayout"/> is not fixed.
        /// </summary>
        /// <returns>
        /// A shallow copy of the current <see cref="DBFilesClient.NET.Structure.EntryLayout"/>.
        /// </returns>
        public EntryLayout Clone()
        {
            var newEntry = new EntryLayout(0);
            newEntry.m_size = m_size;

            var count = this.ElementsCount;
            var newElements = newEntry.m_elements = new List<LayoutElement>(count);
            foreach (var element in m_elements)
                newElements.Add(element);

            return newEntry;
        }
        #endregion

        #region Internals
        bool TryAddValue(LayoutElement element)
        {
            if (m_fixed)
                throw new InvalidOperationException("The EntryLayout is fixed.");

            switch (element)
            {
                case LayoutElement.DoubleWord:
                    m_size += 4;
                    break;
                case LayoutElement.String:
                    m_size += IntPtr.Size;
                    break;
                default:
                    return false;
            }

            m_elements.Add(element);
            return true;
        }
        #endregion

        #region Explicit Interface Implementations
        int ICollection<LayoutElement>.Count { get { return m_elements.Count; } }
        bool ICollection<LayoutElement>.IsReadOnly { get { return m_fixed; } }
        void ICollection<LayoutElement>.Add(LayoutElement element) { this.AddElement(element); }
        void ICollection<LayoutElement>.Clear() { throw new NotSupportedException(); }
        bool ICollection<LayoutElement>.Contains(LayoutElement element) { return m_elements.Contains(element); }
        bool ICollection<LayoutElement>.Remove(LayoutElement element) { throw new NotSupportedException(); }
        void ICollection<LayoutElement>.CopyTo(LayoutElement[] array, int arrayIndex) { m_elements.CopyTo(array, arrayIndex); }
        IEnumerator<LayoutElement> IEnumerable<LayoutElement>.GetEnumerator() { return ((IEnumerable<LayoutElement>)m_elements).GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() { return ((IEnumerable)m_elements).GetEnumerator(); }
        object ICloneable.Clone() { return this.Clone(); }
        #endregion
    }
}
