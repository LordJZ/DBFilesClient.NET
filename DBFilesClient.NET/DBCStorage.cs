using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace DBFilesClient.NET
{
    public sealed class DBCStorage<T> : StorageBase<T> where T : class, new()
    {
        unsafe delegate void EntryLoader(byte* data, byte[] pool, sbyte* pinnedPool, T entry, bool ignoreLazyCStrings);

        #region Loading Information
        ConstructorInfo m_ctor;
        bool m_haveString;
        bool m_haveLazyCString;

        EntryLoader m_loadMethod;
        #endregion

        #region Constructor
        /// <summary>
        /// Initializes a new instance of <see cref="DBFilesClient.NET.DBCStorage&lt;T&gt;"/> class.
        /// </summary>
        public DBCStorage()
        {
            m_ctor = m_entryType.GetConstructor(Type.EmptyTypes);
            if (m_ctor == null)
                throw new InvalidOperationException("Cannot find default constructor for " + m_entryTypeName);

            var fields = GetFields();
            var properties = GetProperties();

            var fieldCount = fields.Length;
            m_fields = new EntryFieldInfo[fieldCount];
            for (int i = 0; i < fieldCount; i++)
            {
                var attr = fields[i].Value;

                var field = fields[i].Key;
                var fieldName = field.Name;

                var type = field.FieldType;
                if (type.IsEnum)
                    type = type.GetEnumUnderlyingType();

                if (i == 0 && type != s_intType && type != s_uintType)
                    throw new InvalidOperationException("First field of type " + m_entryTypeName + " must be Int32 or UInt32.");

                if (attr != null && attr.Option == StoragePresenceOption.UseProperty)
                {
                    // Property Detected
                    var propertyName = attr.PropertyName;
                    var property = properties.FirstOrDefault(prop => prop.Name == propertyName);
                    if (property == null)
                        throw new InvalidOperationException("Property " + propertyName + " for field " + fieldName
                            + " of class " + m_entryTypeName + " cannot be found.");

                    if (property.PropertyType != field.FieldType)
                        throw new InvalidOperationException("Property " + propertyName + " and field " + fieldName
                            + " of class " + m_entryTypeName + " must be of same types.");

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
                            "Field " + fieldName + " of type " + m_entryTypeName + " must be public. "
                            + "Use " + typeof(StoragePresenceAttribute).Name + " to exclude the field.");
                    }

                    m_fields[i].FieldInfo = field;
                }

                if (type == s_intType)
                {
                    m_fields[i].TypeId = StoredTypeId.Int32;
                    m_entrySize += 4;
                }
                else if (type == s_uintType)
                {
                    m_fields[i].TypeId = StoredTypeId.UInt32;
                    m_entrySize += 4;
                }
                else if (type == s_floatType)
                {
                    m_fields[i].TypeId = StoredTypeId.Single;
                    m_entrySize += 4;
                }
                else if (type == s_stringType)
                {
                    m_fields[i].TypeId = StoredTypeId.String;
                    m_entrySize += 4;
                    m_haveString = true;
                }
                else if (type == s_lazyCStringType)
                {
                    m_fields[i].TypeId = StoredTypeId.LazyCString;
                    m_entrySize += 4;
                    m_haveLazyCString = true;
                }
                else
                    throw new InvalidOperationException(
                        "Unknown field type " + type.FullName + " (field " + fieldName + ") of type " + m_entryTypeName + "."
                        );
            }

            GenerateIdGetter();
        }
        #endregion

        #region Generating Methods
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
                "EntryLoader-" + m_entryTypeName,
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
                            "Setter of property " + propertyInfo.Name + " of class " + m_entryTypeName + " is inaccessible.");
                }
                else
                    throw new InvalidOperationException("Invalid field " + i + " in class " + m_entryTypeName + ".");
            }

            ilgen.Emit(OpCodes.Ret);

            m_loadMethod = (EntryLoader)method.CreateDelegate(typeof(EntryLoader));
        }
        #endregion

        #region Loading
        public override void Load(Stream stream)
        {
            Load(stream, LoadFlags.None);
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
        public unsafe void Load(Stream stream, LoadFlags flags)
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

                if (header->RecordSize != m_entrySize)
                    throw new ArgumentException("This DBC file has wrong record size ("
                        + header->RecordSize + ", expected is " + m_entrySize + ").");

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
                    max = *(uint*)(pdata + m_entrySize * (m_records - 1));
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

                            pdata += m_entrySize;
                        }
                    }
                    catch (FieldAccessException e)
                    {
                        throw new InvalidOperationException("Class " + m_entryTypeName + " must be public.", e);
                    }
                }
            }
        }
        #endregion
    }
}
