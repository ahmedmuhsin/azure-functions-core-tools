// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Text;

namespace Azure.Functions.Cli.UnitTests.HelperTests
{
    // Builds 64-bit little-endian x86_64 ELF images that have a table of section headers, for the tests that look at the names of
    // the sections. The image is the header, the data of the sections, the string table of the names and the table of section
    // headers. The table starts with the null section and ends with the string table, as it does in a file that a linker writes.
    internal static class ElfTestFile
    {
        internal const int HeaderSize = 64;
        internal const int SectionHeaderSize = 64;

        // Where the fields are in the header and in a section header. The tests patch them to make a file that is wrong in one place.
        internal const int SectionTableOffsetField = 0x28;
        internal const int SectionHeaderSizeField = 0x3A;
        internal const int SectionCountField = 0x3C;
        internal const int StringTableIndexField = 0x3E;
        internal const int SectionNameField = 0;
        internal const int SectionOffsetField = 24;
        internal const int SectionSizeField = 32;

        internal static byte[] Create(params string[] sectionNames)
        {
            return Create(sectionNames.Select(name => (name, Array.Empty<byte>())).ToArray());
        }

        internal static byte[] Create(params (string Name, byte[] Data)[] sections)
        {
            using var image = new MemoryStream();
            image.Write(new byte[HeaderSize]);

            var dataOffsets = new List<long>();
            foreach (var section in sections)
            {
                dataOffsets.Add(image.Position);
                image.Write(section.Data);
            }

            // The string table has an empty name first, then the name of each section and the name of the table itself.
            using var strings = new MemoryStream();
            strings.WriteByte(0);
            var nameOffsets = new List<uint>();
            foreach (var name in sections.Select(section => section.Name).Append(".shstrtab"))
            {
                nameOffsets.Add((uint)strings.Length);
                strings.Write(Encoding.ASCII.GetBytes(name));
                strings.WriteByte(0);
            }

            var stringTableOffset = image.Position;
            image.Write(strings.ToArray());

            while (image.Position % 8 != 0)
            {
                image.WriteByte(0);
            }

            var tableOffset = image.Position;
            image.Write(new byte[SectionHeaderSize]);
            for (var i = 0; i < sections.Length; i++)
            {
                image.Write(SectionHeader(nameOffsets[i], type: 1, dataOffsets[i], sections[i].Data.Length));
            }

            image.Write(SectionHeader(nameOffsets[^1], type: 3, stringTableOffset, strings.Length));

            var bytes = image.ToArray();
            bytes[0] = 0x7F;
            bytes[1] = (byte)'E';
            bytes[2] = (byte)'L';
            bytes[3] = (byte)'F';
            bytes[4] = 2;
            bytes[5] = 1;
            bytes[6] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), 3);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), 0x3E);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(SectionTableOffsetField), (ulong)tableOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x34), HeaderSize);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(SectionHeaderSizeField), SectionHeaderSize);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(SectionCountField), (ushort)(sections.Length + 2));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(StringTableIndexField), (ushort)(sections.Length + 1));
            return bytes;
        }

        // The position of the header of section number index, which is read from the header of the file.
        internal static int SectionHeaderPosition(byte[] file, int index)
        {
            var tableOffset = (int)BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(SectionTableOffsetField));
            return tableOffset + (index * SectionHeaderSize);
        }

        internal static void Patch(byte[] file, int position, ulong value, int size)
        {
            switch (size)
            {
                case 2:
                    BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(position), (ushort)value);
                    break;
                case 4:
                    BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(position), (uint)value);
                    break;
                case 8:
                    BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(position), value);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(size));
            }
        }

        private static byte[] SectionHeader(uint nameOffset, uint type, long dataOffset, long size)
        {
            var header = new byte[SectionHeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(SectionNameField), nameOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), type);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(SectionOffsetField), (ulong)dataOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(SectionSizeField), (ulong)size);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(48), 1);
            return header;
        }
    }
}
