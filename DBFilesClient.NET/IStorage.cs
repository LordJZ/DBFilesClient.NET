using System;
using System.IO;
using System.Collections.Generic;

namespace DBFilesClient.NET
{
    public interface IStorage<T> /*: IDictionary<uint, T>, ICollection<T>*/ where T : class, new()
    {
        /// <summary>
        /// Loads the storage from a <see cref="System.IO.Stream"/>.
        /// </summary>
        /// <param name="stream">
        /// The <see cref="System.IO.Stream"/> from which the storage should be loaded.
        /// </param>
        void Load(Stream stream);

        ///// <summary>
        ///// Saves the storage into a <see cref="System.IO.Stream"/>.
        ///// </summary>
        ///// <param name="stream">
        ///// The <see cref="System.IO.Stream"/> to which the storage should be saved.
        ///// </param>
        //void Save(Stream stream);
    }
}
