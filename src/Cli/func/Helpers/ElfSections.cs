// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Text;

namespace Azure.Functions.Cli.Helpers
{
    /// <summary>
    /// Reads the names of the sections of a 64-bit little-endian ELF file, which is the file that Rust makes for x86_64 Linux.
    /// It reads the header, the table of section headers and the table of the names, and never the content of a section.
    /// Layout reference: https://refspecs.linuxfoundation.org/elf/gabi4+/ch4.eheader.html and ch4.sheader.html
    /// </summary>
    internal static class ElfSections
    {
        private const int HeaderSize = 64;
        private const int SectionHeaderSize = 64;

        // The names of the sections take a few hundred bytes. A table that says it is bigger than this is not read.
        private const int MaxNameTableSize = 1 << 20;

        // Where the fields are, from the start of the header (e_shoff, e_shentsize, e_shnum and e_shstrndx) and from the start of a
        // section header (sh_name, sh_offset and sh_size).
        private const int SectionTableOffsetField = 0x28;
        private const int SectionHeaderSizeField = 0x3A;
        private const int SectionCountField = 0x3C;
        private const int NameTableIndexField = 0x3E;
        private const int SectionNameField = 0;
        private const int SectionOffsetField = 24;
        private const int SectionSizeField = 32;

        /// <summary>
        /// Tells whether <paramref name="file"/> has a section called <paramref name="name"/>. The name is read from the table of the names,
        /// at the offset that the section header gives, and the whole name has to be equal. The text of a name can also be in the data of
        /// a section, for example in a binary that can print a backtrace, and that is not a section. A file that is not a 64-bit
        /// little-endian ELF file, or that is cut short or inconsistent anywhere that is read, has no sections, and the answer is
        /// <c>false</c>. A file with 65280 sections or more, which keeps its count in the first section header, is not read either.
        /// </summary>
        internal static bool HasSection(Stream file, string name)
        {
            var length = file.Length;
            if (length < HeaderSize)
            {
                return false;
            }

            var header = new byte[HeaderSize];
            if (!ReadAt(file, 0, header))
            {
                return false;
            }

            // The magic 0x7F 'E' 'L' 'F', then EI_CLASS 2 (64 bits) and EI_DATA 1 (little endian).
            var isElf64LittleEndian = header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F' && header[4] == 2 && header[5] == 1;
            if (!isElf64LittleEndian)
            {
                return false;
            }

            var tableOffset = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(SectionTableOffsetField));
            var entrySize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(SectionHeaderSizeField));
            var count = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(SectionCountField));
            var nameTableIndex = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(NameTableIndexField));

            // No table, entries of another size, a count that does not fit the field (the real one would be in the first entry),
            // and an index for the names that is the null section, the escape value or past the table, are all files not to read.
            if (tableOffset == 0 || entrySize != SectionHeaderSize || count == 0 || nameTableIndex == 0 || nameTableIndex >= count)
            {
                return false;
            }

            var tableSize = count * SectionHeaderSize;
            if (!FitsInFile(tableOffset, (ulong)tableSize, length))
            {
                return false;
            }

            var table = new byte[tableSize];
            if (!ReadAt(file, (long)tableOffset, table))
            {
                return false;
            }

            var nameTable = table.AsSpan(nameTableIndex * SectionHeaderSize, SectionHeaderSize);
            var namesOffset = BinaryPrimitives.ReadUInt64LittleEndian(nameTable[SectionOffsetField..]);
            var namesSize = BinaryPrimitives.ReadUInt64LittleEndian(nameTable[SectionSizeField..]);
            if (namesSize == 0 || namesSize > MaxNameTableSize || !FitsInFile(namesOffset, namesSize, length))
            {
                return false;
            }

            var names = new byte[namesSize];
            if (!ReadAt(file, (long)namesOffset, names))
            {
                return false;
            }

            var wanted = Encoding.ASCII.GetBytes(name);
            for (var index = 1; index < count; index++)
            {
                var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan((index * SectionHeaderSize) + SectionNameField));
                if (nameOffset >= names.Length)
                {
                    continue;
                }

                // The name is equal when it has the wanted text and the zero byte that ends a name comes right after it, inside the table of
                // the names. Only that many bytes are looked at, so a table of names without a zero byte costs no more than a short one.
                var candidate = names.AsSpan((int)nameOffset);
                if (candidate.Length > wanted.Length && candidate[wanted.Length] == 0 && candidate[..wanted.Length].SequenceEqual(wanted))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool FitsInFile(ulong offset, ulong size, long fileLength)
        {
            return offset <= (ulong)fileLength && size <= (ulong)fileLength - offset;
        }

        private static bool ReadAt(Stream file, long position, byte[] buffer)
        {
            file.Seek(position, SeekOrigin.Begin);
            return file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) == buffer.Length;
        }
    }
}
