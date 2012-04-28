using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

namespace DBFilesClient.NET
{
    public interface IStorage : IDisposable
    {
        /// <summary>
        /// Gets the number of entries present in the
        /// current <see cref="DBFilesClient.NET.IStorage"/>.
        /// </summary>
        int EntriesCount { get; }

        /// <summary>
        /// Gets the size in bytes of an entry in the
        /// current <see cref="DBFilesClient.NET.IStorage"/>.
        /// </summary>
        int EntrySize { get; }

        /// <summary>
        /// Gets an entry for the specified id.
        /// </summary>
        /// <param name="id">
        /// Id of the entry.
        /// </param>
        /// <returns>
        /// Pointer to the entry if found; otherwise, zero.
        /// </returns>
        IntPtr GetEntry(uint id);

        /// <summary>
        /// Adds an entry with the specified id.
        /// </summary>
        /// <param name="id">
        /// Id of the new entry.
        /// </param>
        /// <returns>
        /// Pointer to the new entry.
        /// </returns>
        /// <exception cref="System.ArgumentException">
        /// An entry with the same id is already present in the storage.
        /// </exception>
        IntPtr AddEntry(uint id);

        /// <summary>
        /// Loads the storage from a <see cref="System.IO.Stream"/>.
        /// </summary>
        /// <param name="stream">
        /// The <see cref="System.IO.Stream"/> from which the storage should be loaded.
        /// </param>
        /// <param name="completeLoad">
        /// Indicates whether the whole stream should be loaded into memory or not.
        /// </param>
        /// <param name="ownStream">
        /// Indicates whether the storage owns the stream or not.
        /// </param>
        void Load(Stream stream, bool completeLoad, bool ownStream);

        /// <summary>
        /// Saves the storage into a <see cref="System.IO.Stream"/>.
        /// </summary>
        /// <param name="stream">
        /// The <see cref="System.IO.Stream"/> to which the storage should be saved.
        /// </param>
        void Save(Stream stream);
    }
}
