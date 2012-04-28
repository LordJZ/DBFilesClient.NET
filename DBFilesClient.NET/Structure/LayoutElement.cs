using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DBFilesClient.NET.Structure
{
    public enum LayoutElement : byte
    {
        /// <summary>
        /// A double-word (32-bit) value.
        /// </summary>
        DoubleWord,

        /// <summary>
        /// A string.
        /// </summary>
        String,
    }
}
