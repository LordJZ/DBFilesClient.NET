using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Reflection.Emit;
using System.IO;

namespace DBFilesClient.NET
{
    public sealed class DBCStorage<T> : IStorage<T> where T : class, new()
    {
        unsafe delegate void EntryLoader(byte* data, byte[] pool, sbyte* pinnedPool, T entry, bool ignoreLazyCStrings);
        delegate uint IdGetter(T entry);

        #region Static Fields
        static readonly Type s_intType = typeof(int);
        static readonly Type s_uintType = typeof(uint);
        static readonly Type s_floatType = typeof(float);
        static readonly Type s_stringType = typeof(string);
        static readonly Type s_lazyCStringType = typeof(LazyCString);
        static readonly ConstructorInfo s_stringCtor = typeof(string).GetConstructor(new[] { typeof(sbyte*) });
        static readonly ConstructorInfo s_lazyCStringCtor =
            typeof(LazyCString).GetConstructor(new[] { typeof(byte[]), typeof(int) });
        #endregion

        #region Entry Fields
        T[] m_entries;
        uint m_minId;
        uint m_maxId;
        int m_records;
        #endregion

        #region Loading Information
        struct EntryFieldInfo
        {
            public FieldInfo FieldInfo;
            public StoredTypeId TypeId;
            public PropertyInfo Property;
            public MethodInfo Getter;
            public MethodInfo Setter;
        }

        Type m_entryType;
        ConstructorInfo m_ctor;
        EntryFieldInfo[] m_fields;
        int m_entryDBCSize;
        bool m_haveString;
        bool m_haveLazyCString;

        EntryLoader m_loadMethod;
        IdGetter m_idGetter;
        #endregion

        #region Properties
        public int Records { get { return m_records; } }
        public uint MinId
        {
            get
            {
                if (m_records == 0)
                    throw new InvalidOperationException("There are no records in the storage.");

                return m_minId;
            }
        }
        public uint MaxId
        {
            get
            {
                if (m_records == 0)
                    throw new InvalidOperationException("There are no records in the storage.");

                return m_maxId;
            }
        }
        #endregion

        #region Constructor
        /// <summary>
        /// Initializes a new instance of <see cref="DBFilesClient.NET.DBCStorage&lt;T&gt;"/> class.
        /// </summary>
        public DBCStorage()
        {
            m_entryType = typeof(T);

            m_ctor = m_entryType.GetConstructor(Type.EmptyTypes);
            if (m_ctor == null)
                throw new InvalidOperationException("Cannot find default constructor for " + m_entryType.Name);

            var fields = m_entryType
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(field => new KeyValuePair<FieldInfo, StoragePresenceAttribute>(
                    field,
                    (StoragePresenceAttribute)field.GetCustomAttributes(StoragePresenceAttribute.Type, false).FirstOrDefault()))
                .ToArray();

            var properties = m_entryType
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .ToArray();

            var fieldCount = fields.Length;
            m_fields = new EntryFieldInfo[fieldCount];
            for (int i = 0; i < fieldCount; i++)
            {
                var attr = fields[i].Value;
                if (attr != null && attr.Option == StoragePresenceOption.Exclude)
                    continue;

                var field = fields[i].Key;
                var fieldName = field.Name;

                if (fieldName.StartsWith("<") && fieldName.EndsWith(">k__BackingField"))
                    continue;

                var type = field.FieldType;
                if (type.IsEnum)
                    type = type.GetEnumUnderlyingType();

                if (i == 0 && type != s_intType && type != s_uintType)
                    throw new InvalidOperationException("First field of type " + m_entryType.Name + " must be Int32 or UInt32.");

                if (attr != null && attr.Option == StoragePresenceOption.UseProperty)
                {
                    // Property Detected
                    var propertyName = attr.PropertyName;
                    var property = properties.FirstOrDefault(prop => prop.Name == propertyName);
                    if (property == null)
                        throw new InvalidOperationException("Property " + propertyName + " for field " + fieldName
                            + " of class " + m_entryType.Name + " cannot be found.");

                    if (property.PropertyType != field.FieldType)
                        throw new InvalidOperationException("Property " + propertyName + " and field " + fieldName
                            + " of class " + m_entryType.Name + " must be of same types.");

                    m_fields[i].Property = property;
                    foreach (var accessor in property.GetAccessors())
                    {
                        if (accessor.ReturnType == field.FieldType)
                            m_fields[i].Getter = accessor;
                        else if (accessor.ReturnType == typeof(void))
                            m_fields[i].Setter = accessor;
                    }
                }
                else
                {
                    if (!field.IsPublic)
                    {
                        throw new InvalidOperationException(
                            "Field " + fieldName + " of type " + m_entryType.Name + " must be public. "
                            + "Use " + typeof(StoragePresenceAttribute).Name + " to exclude the field.");
                    }

                    m_fields[i].FieldInfo = field;
                }

                if (type == s_intType)
                {
                    m_fields[i].TypeId = StoredTypeId.Int32;
                    m_entryDBCSize += 4;
                }
                else if (type == s_uintType)
                {
                    m_fields[i].TypeId = StoredTypeId.UInt32;
                    m_entryDBCSize += 4;
                }
                else if (type == s_floatType)
                {
                    m_fields[i].TypeId = StoredTypeId.Single;
                    m_entryDBCSize += 4;
                }
                else if (type == s_stringType)
                {
                    m_fields[i].TypeId = StoredTypeId.String;
                    m_entryDBCSize += 4;
                    m_haveString = true;
                }
                else if (type == s_lazyCStringType)
                {
                    m_fields[i].TypeId = StoredTypeId.LazyCString;
                    m_entryDBCSize += 4;
                    m_haveLazyCString = true;
                }
                else
                    throw new InvalidOperationException(
                        "Unknown field type " + type.FullName + " (field " + fieldName + ") of type " + m_entryType.Name + "."
                        );
            }

            GenerateIdGetter();
        }
        #endregion

        #region Generating Methods
        void GenerateIdGetter()
        {
            if (m_loadMethod != null)
                return;

            var method = new DynamicMethod(
                "IdGetter-" + m_entryType.Name,
                typeof(uint),
                new Type[] { typeof(T) }
                );

            var ilgen = method.GetILGenerator(20);
            ilgen.Emit(OpCodes.Ldarg_0);

            if (m_fields[0].FieldInfo != null)
                ilgen.Emit(OpCodes.Ldfld, m_fields[0].FieldInfo);
            else if (m_fields[0].Getter != null)
                ilgen.Emit(OpCodes.Callvirt, m_fields[0].Getter);
            else
                throw new InvalidOperationException();

            if (m_fields[0].TypeId != StoredTypeId.UInt32)
                ilgen.Emit(OpCodes.Conv_U4);
            ilgen.Emit(OpCodes.Ret);

            m_idGetter = (IdGetter)method.CreateDelegate(typeof(IdGetter));
        }

        void EmitLoadField(ILGenerator ilgen, FieldInfo field, StoredTypeId id, bool lastField)
        {
            //             0            1            2             3           4
            // args: byte* data, byte[] pool, sbyte* pinnedPool, T entry, bool ignoreLazyCStrings

            switch (id)
            {
                case StoredTypeId.Int32:
                    ilgen.Emit(OpCodes.Ldarg_3);                            // stack = entry
                    ilgen.Emit(OpCodes.Ldarg_0);                            // stack = data, entry
                    ilgen.Emit(OpCodes.Ldind_I4);                           // stack = *(int*)data, entry
                    ilgen.Emit(OpCodes.Stfld, field);                       // stack =
                    break;
                case StoredTypeId.UInt32:
                    ilgen.Emit(OpCodes.Ldarg_3);                            // stack = entry
                    ilgen.Emit(OpCodes.Ldarg_0);                            // stack = data, entry
                    ilgen.Emit(OpCodes.Ldind_U4);                           // stack = *(uint*)data, entry
                    ilgen.Emit(OpCodes.Stfld, field);                       // stack =
                    break;
                case StoredTypeId.Single:
                    ilgen.Emit(OpCodes.Ldarg_3);                            // stack = entry
                    ilgen.Emit(OpCodes.Ldarg_0);                            // stack = data, entry
                    ilgen.Emit(OpCodes.Ldind_R4);                           // stack = *(float*)data, entry
                    ilgen.Emit(OpCodes.Stfld, field);                       // stack =
                    break;
                case StoredTypeId.String:
                    ilgen.Emit(OpCodes.Ldarg_3);                            // stack = entry
                    ilgen.Emit(OpCodes.Ldarg_2);                            // stack = pinnedPool, entry
                    ilgen.Emit(OpCodes.Ldarg_0);                            // stack = data, pinnedPool, entry
                    ilgen.Emit(OpCodes.Ldind_I4);                           // stack = *(int*)data, pinnedPool, entry
                    ilgen.Emit(OpCodes.Conv_I);                             // stack = (IntPtr)*(int*)data, pinnedPool, entry
                    ilgen.Emit(OpCodes.Add);                                // stack = pinnedPool+*(int*)data, entry
                    ilgen.Emit(OpCodes.Newobj, s_stringCtor);               // stack = string, entry
                    ilgen.Emit(OpCodes.Stfld, field);                       // stack =
                    break;
                case StoredTypeId.LazyCString:
                    ilgen.Emit(OpCodes.Ldloca_S, 0);                        // stack = &lazyCString
                    ilgen.Emit(OpCodes.Ldarg_1);                            // stack = pool, &lazyCString
                    ilgen.Emit(OpCodes.Ldarg_0);                            // stack = data, pool, &lazyCString
                    ilgen.Emit(OpCodes.Ldind_I4);                           // stack = *(int*)data, pool, &lazyCString
                    ilgen.Emit(OpCodes.Call, s_lazyCStringCtor);           // stack =
                    ilgen.Emit(OpCodes.Ldarg_S, 4);                         // stack = ignoreLazyCStrings
                    var label = ilgen.DefineLabel();
                    ilgen.Emit(OpCodes.Brfalse, label);                     // stack =
                    ilgen.Emit(OpCodes.Ldloca_S, 0);                        // stack = &lazyCString
                    ilgen.Emit(OpCodes.Call, LazyCString.LoadStringInfo); // stack =
                    ilgen.MarkLabel(label);
                    ilgen.Emit(OpCodes.Ldarg_3);                            // stack = entry
                    ilgen.Emit(OpCodes.Ldloc_0);                            // stack = lazyCString, entry
                    ilgen.Emit(OpCodes.Stfld, field);                       // stack =
                    break;
                default:
                    throw new InvalidOperationException();
            }

            //if (!lastField)
            {
                ilgen.Emit(OpCodes.Ldarg_0);            // stack = data
                ilgen.Emit(OpCodes.Ldc_I4_4);           // stack = 4, data
                ilgen.Emit(OpCodes.Conv_I);             // stack = (IntPtr)4, data
                ilgen.Emit(OpCodes.Add);                // stack = data+4
                ilgen.Emit(OpCodes.Starg_S, 0);         // stack =
            }
        }

        void EmitLoadProperty(ILGenerator ilgen, PropertyInfo property, MethodInfo setter, StoredTypeId id, bool lastField)
        {
            //             0            1            2             3           4
            // args: byte* data, byte[] pool, sbyte* pinnedPool, T entry, bool ignoreLazyCStrings

            switch (id)
            {
                case StoredTypeId.Int32:
                    ilgen.Emit(OpCodes.Ldarg_3);                            // stack = entry
                    ilgen.Emit(OpCodes.Ldarg_0);                            // stack = data, entry
                    ilgen.Emit(OpCodes.Ldind_I4);                           // stack = *(int*)data, entry
                    ilgen.Emit(OpCodes.Callvirt, setter);                   // stack =
                    ilgen.Emit(OpCodes.Nop);                                //
                    break;
                case StoredTypeId.UInt32:
                    ilgen.Emit(OpCodes.Ldarg_3);                            // stack = entry
                    ilgen.Emit(OpCodes.Ldarg_0);                            // stack = data, entry
                    ilgen.Emit(OpCodes.Ldind_U4);                           // stack = *(uint*)data, entry
                    ilgen.Emit(OpCodes.Callvirt, setter);                   // stack =
                    ilgen.Emit(OpCodes.Nop);                                //
                    break;
                case StoredTypeId.Single:
                    ilgen.Emit(OpCodes.Ldarg_3);                            // stack = entry
                    ilgen.Emit(OpCodes.Ldarg_0);                            // stack = data, entry
                    ilgen.Emit(OpCodes.Ldind_R4);                           // stack = *(float*)data, entry
                    ilgen.Emit(OpCodes.Callvirt, setter);                   // stack =
                    ilgen.Emit(OpCodes.Nop);                                //
                    break;
                case StoredTypeId.String:
                    ilgen.Emit(OpCodes.Ldarg_3);                            // stack = entry
                    ilgen.Emit(OpCodes.Ldarg_2);                            // stack = pinnedPool, entry
                    ilgen.Emit(OpCodes.Ldarg_0);                            // stack = data, pinnedPool, entry
                    ilgen.Emit(OpCodes.Ldind_I4);                           // stack = *(int*)data, pinnedPool, entry
                    ilgen.Emit(OpCodes.Conv_I);                             // stack = (IntPtr)*(int*)data, pinnedPool, entry
                    ilgen.Emit(OpCodes.Add);                                // stack = pinnedPool+*(int*)data, entry
                    ilgen.Emit(OpCodes.Newobj, s_stringCtor);               // stack = string, entry
                    ilgen.Emit(OpCodes.Callvirt, setter);                   // stack =
                    ilgen.Emit(OpCodes.Nop);                                //
                    break;
                case StoredTypeId.LazyCString:
                    ilgen.Emit(OpCodes.Ldloca_S, 0);                        // stack = &lazyCString
                    ilgen.Emit(OpCodes.Ldarg_1);                            // stack = pool, &lazyCString
                    ilgen.Emit(OpCodes.Ldarg_0);                            // stack = data, pool, &lazyCString
                    ilgen.Emit(OpCodes.Ldind_I4);                           // stack = *(int*)data, pool, &lazyCString
                    ilgen.Emit(OpCodes.Call, s_lazyCStringCtor);           // stack =
                    ilgen.Emit(OpCodes.Ldarg_S, 4);                         // stack = ignoreLazyCStrings
                    var label = ilgen.DefineLabel();
                    ilgen.Emit(OpCodes.Brfalse, label);                     // stack =
                    ilgen.Emit(OpCodes.Ldloca_S, 0);                        // stack = &lazyCString
                    ilgen.Emit(OpCodes.Call, LazyCString.LoadStringInfo); // stack =
                    ilgen.MarkLabel(label);
                    ilgen.Emit(OpCodes.Ldarg_3);                            // stack = entry
                    ilgen.Emit(OpCodes.Ldloc_0);                            // stack = lazyCString, entry
                    ilgen.Emit(OpCodes.Callvirt, setter);                   // stack =
                    ilgen.Emit(OpCodes.Nop);                                //
                    break;
                default:
                    throw new InvalidOperationException();
            }

            //if (!lastField)
            {
                ilgen.Emit(OpCodes.Ldarg_0);            // stack = data
                ilgen.Emit(OpCodes.Ldc_I4_4);           // stack = 4, data
                ilgen.Emit(OpCodes.Conv_I);             // stack = (IntPtr)4, data
                ilgen.Emit(OpCodes.Add);                // stack = data+4
                ilgen.Emit(OpCodes.Starg_S, 0);         // stack =
            }
        }

        void GenerateLoadMethod()
        {
            if (m_loadMethod != null)
                return;

            var method = new DynamicMethod(
                "EntryLoader-" + m_entryType.Name,
                typeof(void),
                new Type[] { typeof(byte*), typeof(byte[]), typeof(sbyte*), typeof(T), typeof(bool) },
                typeof(T).Module
                );

            var fieldCount = m_fields.Length;

            var ilgen = method.GetILGenerator(fieldCount * 20);

            if (m_haveLazyCString)
                ilgen.DeclareLocal(typeof(LazyCString), true);

            ilgen.Emit(OpCodes.Nop);

            for (int i = 0; i < fieldCount; i++)
            {
                var lastField = i + 1 >= fieldCount;
                var id = m_fields[i].TypeId;
                var fieldInfo = m_fields[i].FieldInfo;
                var propertyInfo = m_fields[i].Property;

                if (fieldInfo != null)
                    EmitLoadField(ilgen, fieldInfo, id, lastField);
                else if (propertyInfo != null)
                {
                    var setter = m_fields[i].Setter;
                    if (setter != null)
                        EmitLoadProperty(ilgen, propertyInfo, setter, id, lastField);
                    else
                        throw new InvalidOperationException(
                            "Setter of property " + propertyInfo.Name + " of class " + m_entryType.Name + " is inaccessible.");
                }
                else
                    throw new InvalidOperationException("Invalid field " + i + " in class " + m_entryType.Name + ".");
            }

            ilgen.Emit(OpCodes.Ret);

            m_loadMethod = (EntryLoader)method.CreateDelegate(typeof(EntryLoader));
        }
        #endregion

        #region Loading
        void IStorage<T>.Load(Stream stream)
        {
            Load(stream);
        }

        /// <summary>
        /// Loads the storage from a <see cref="System.IO.Stream"/>.
        /// </summary>
        /// <param name="stream">
        /// The <see cref="System.IO.Stream"/> from which the storage should be loaded.
        /// </param>
        /// <param name="flags">
        /// The <see cref="DBFilesClient.NET.LoadFlags"/> to be used when loading.
        /// </param>
        public unsafe void Load(Stream stream, LoadFlags flags = LoadFlags.None)
        {
            GenerateLoadMethod();

            byte[] headerBytes;
            byte[] data;
            byte[] pool;

            fixed (byte* headerPtr = headerBytes = new byte[DBCHeader.Size])
            {
                if (stream.Read(headerBytes, 0, DBCHeader.Size) != DBCHeader.Size)
                    throw new IOException("Failed to read the DBC header.");

                var header = (DBCHeader*)headerPtr;
                if (!flags.HasFlag(LoadFlags.IgnoreWrongFourCC) && header->FourCC != 0x43424457)
                    throw new ArgumentException("This is not a valid DBC file.");

                if (header->RecordSize != m_entryDBCSize)
                    throw new ArgumentException("This DBC file has wrong record size ("
                        + header->RecordSize + ", expected is " + m_entryDBCSize + ").");

                m_records = header->Records;

                int index, size;

                index = 0;
                size = header->Records * header->RecordSize;
                data = new byte[size];
                while (index < size)
                    index += stream.Read(data, index, size - index);

                index = 0;
                size = header->StringPoolSize;
                pool = new byte[size];
                while (index < size)
                    index += stream.Read(pool, index, size - index);
            }

            fixed (byte* pdata_ = data)
            {
                byte* pdata = pdata_;

                uint min, max;
                if (m_records > 0)
                {
                    min = *(uint*)pdata;
                    max = *(uint*)(pdata + m_entryDBCSize * (m_records - 1));
                }
                else
                {
                    min = 0;
                    max = 0;
                }

                this.Resize(min, max);

                fixed (byte* ppool = m_haveString ? pool : null)
                {
                    bool ignoreLazyCStrings = !flags.HasFlag(LoadFlags.LazyCStrings);
                    try
                    {
                        for (int i = 0; i < m_records; i++)
                        {
                            var entry = (T)m_ctor.Invoke(null);

                            m_loadMethod(pdata, pool, (sbyte*)ppool, entry, ignoreLazyCStrings);

                            uint id = *(uint*)pdata;
                            m_entries[id - m_minId] = entry;

                            pdata += m_entryDBCSize;
                        }
                    }
                    catch (FieldAccessException e)
                    {
                        throw new InvalidOperationException("Class " + m_entryType.Name + " must be public.", e);
                    }
                }
            }
        }
        #endregion

        #region Collection & Dictionary Method Implementations
        public bool ContainsKey(uint id)
        {
            if (!CheckId(id))
                return false;

            return m_entries[id - m_minId] != null;
        }

        public void Add(uint id, T entry)
        {
            if (entry == null)
                throw new ArgumentNullException("entry");

            if (m_idGetter(entry) != id)
                throw new ArgumentException("id and id of entry must match.");

            if (ContainsKey(id))
                throw new ArgumentException("A record with the same id already present in the storage.");

            if (!CheckId(id))
                this.ResizeToStore(id);

            m_entries[id - m_minId] = entry;
            ++m_records;
        }

        public bool Remove(uint id)
        {
            if (ContainsKey(id))
            {
                m_entries[id - m_minId] = null;
                --m_records;
                return true;
            }

            return false;
        }

        public bool TryGetValue(uint id, out T entry)
        {
            if (ContainsKey(id))
            {
                entry = m_entries[id - m_minId];
                return true;
            }

            entry = null;
            return false;
        }

        public T this[uint id]
        {
            get
            {
                if (!ContainsKey(id))
                    throw new KeyNotFoundException();

                return m_entries[id - m_minId];
            }
            set
            {
                if (value == null)
                    throw new ArgumentNullException("value");

                if (m_idGetter(value) != id)
                    throw new ArgumentException("id and id of entry must match.");

                if (!CheckId(id))
                    this.ResizeToStore(id);

                if (m_entries[id - m_minId] != null)
                    ++m_records;

                m_entries[id - m_minId] = value;
            }
        }
        #endregion

        #region Misc Methods
        bool CheckId(uint id)
        {
            if (id == 0)
                throw new ArgumentOutOfRangeException("id", "id cannot be 0.");

            return m_minId <= id && id <= m_maxId;
        }

        void ResizeToStore(uint id)
        {
            this.Resize(Math.Min(m_minId, id), Math.Max(id, m_maxId));
        }

        void Resize(uint min, uint max)
        {
            if (max < min)
                throw new ArgumentOutOfRangeException("max");

            if (m_entries == null)
            {
                m_entries = new T[max - min + 1];
                m_minId = min;
                m_maxId = max;
                return;
            }

            int index;
            if (min < m_minId)
                index = (int)(m_minId - min);
            else
                index = 0;
            int count = (int)(m_maxId - m_minId + 1);

            var oldEntries = m_entries;
            m_entries = new T[max - min + 1];
            m_maxId = max;
            m_minId = min;

            Array.Copy(oldEntries, 0, m_entries, index, count);
        }
        #endregion
    }
}
