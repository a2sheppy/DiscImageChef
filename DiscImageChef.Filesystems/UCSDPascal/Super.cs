// /***************************************************************************
// The Disc Image Chef
// ----------------------------------------------------------------------------
//
// Filename       : Super.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : U.C.S.D. Pascal filesystem plugin.
//
// --[ Description ] ----------------------------------------------------------
//
//     Handles mounting and umounting the U.C.S.D. Pascal filesystem.
//
// --[ License ] --------------------------------------------------------------
//
//     This library is free software; you can redistribute it and/or modify
//     it under the terms of the GNU Lesser General Public License as
//     published by the Free Software Foundation; either version 2.1 of the
//     License, or (at your option) any later version.
//
//     This library is distributed in the hope that it will be useful, but
//     WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
//     Lesser General Public License for more details.
//
//     You should have received a copy of the GNU Lesser General Public
//     License along with this library; if not, see <http://www.gnu.org/licenses/>.
//
// ----------------------------------------------------------------------------
// Copyright © 2011-2018 Natalia Portillo
// ****************************************************************************/

using System;
using System.Collections.Generic;
using Schemas;

namespace DiscImageChef.Filesystems.UCSDPascal
{
    // Information from Call-A.P.P.L.E. Pascal Disk Directory Structure
    public partial class PascalPlugin
    {
        public override Errno Mount()
        {
            return Mount(false);
        }

        public override Errno Mount(bool debug)
        {
            this.debug = debug;
            if(device.GetSectors() < 3) return Errno.InvalidArgument;

            multiplier = (uint)(device.ImageInfo.SectorSize == 256 ? 2 : 1);

            // Blocks 0 and 1 are boot code
            catalogBlocks = device.ReadSectors(multiplier * 2, multiplier);

            // On Apple //, it's little endian
            BigEndianBitConverter.IsLittleEndian =
                multiplier == 2 ? !BitConverter.IsLittleEndian : BitConverter.IsLittleEndian;

            mountedVolEntry.FirstBlock = BigEndianBitConverter.ToInt16(catalogBlocks,                 0x00);
            mountedVolEntry.LastBlock  = BigEndianBitConverter.ToInt16(catalogBlocks,                 0x02);
            mountedVolEntry.EntryType  = (PascalFileKind)BigEndianBitConverter.ToInt16(catalogBlocks, 0x04);
            mountedVolEntry.VolumeName = new byte[8];
            Array.Copy(catalogBlocks, 0x06, mountedVolEntry.VolumeName, 0, 8);
            mountedVolEntry.Blocks   = BigEndianBitConverter.ToInt16(catalogBlocks, 0x0E);
            mountedVolEntry.Files    = BigEndianBitConverter.ToInt16(catalogBlocks, 0x10);
            mountedVolEntry.Dummy    = BigEndianBitConverter.ToInt16(catalogBlocks, 0x12);
            mountedVolEntry.LastBoot = BigEndianBitConverter.ToInt16(catalogBlocks, 0x14);
            mountedVolEntry.Tail     = BigEndianBitConverter.ToInt32(catalogBlocks, 0x16);

            if(mountedVolEntry.FirstBlock       != 0                                   ||
               mountedVolEntry.LastBlock        <= mountedVolEntry.FirstBlock          ||
               (ulong)mountedVolEntry.LastBlock > device.ImageInfo.Sectors / multiplier - 2 ||
               mountedVolEntry.EntryType        != PascalFileKind.Volume &&
               mountedVolEntry.EntryType        != PascalFileKind.Secure            ||
               mountedVolEntry.VolumeName[0]    > 7                                 ||
               mountedVolEntry.Blocks           < 0                                 ||
               (ulong)mountedVolEntry.Blocks    != device.ImageInfo.Sectors / multiplier ||
               mountedVolEntry.Files            < 0) return Errno.InvalidArgument;

            catalogBlocks = device.ReadSectors(multiplier                                                         * 2,
                                               (uint)(mountedVolEntry.LastBlock - mountedVolEntry.FirstBlock - 2) *
                                               multiplier);
            int offset = 26;

            fileEntries = new List<PascalFileEntry>();
            while(offset + 26 < catalogBlocks.Length)
            {
                PascalFileEntry entry = new PascalFileEntry
                {
                    Filename         = new byte[16],
                    FirstBlock       = BigEndianBitConverter.ToInt16(catalogBlocks,                 offset + 0x00),
                    LastBlock        = BigEndianBitConverter.ToInt16(catalogBlocks,                 offset + 0x02),
                    EntryType        = (PascalFileKind)BigEndianBitConverter.ToInt16(catalogBlocks, offset + 0x04),
                    LastBytes        = BigEndianBitConverter.ToInt16(catalogBlocks,                 offset + 0x16),
                    ModificationTime = BigEndianBitConverter.ToInt16(catalogBlocks,                 offset + 0x18)
                };
                Array.Copy(catalogBlocks, offset + 0x06, entry.Filename, 0, 16);

                if(entry.Filename[0] <= 15 && entry.Filename[0] > 0) fileEntries.Add(entry);

                offset += 26;
            }

            bootBlocks = device.ReadSectors(0, 2 * multiplier);

            XmlFsType = new FileSystemType
            {
                Bootable       = !ArrayHelpers.ArrayIsNullOrEmpty(bootBlocks),
                Clusters       = mountedVolEntry.Blocks,
                ClusterSize    = (int)device.ImageInfo.SectorSize,
                Files          = mountedVolEntry.Files,
                FilesSpecified = true,
                Type           = "UCSD Pascal",
                VolumeName     = StringHandlers.PascalToString(mountedVolEntry.VolumeName, CurrentEncoding)
            };

            mounted = true;

            return Errno.NoError;
        }

        public override Errno Unmount()
        {
            mounted = false;
            fileEntries = null;
            return Errno.NoError;
        }

        public override Errno StatFs(ref FileSystemInfo stat)
        {
            stat = new FileSystemInfo
            {
                Blocks         = mountedVolEntry.Blocks,
                FilenameLength = 16,
                Files          = (ulong)mountedVolEntry.Files,
                FreeBlocks     = 0,
                PluginId       = PluginUuid,
                Type           = "UCSD Pascal"
            };

            stat.FreeBlocks = mountedVolEntry.Blocks - (mountedVolEntry.LastBlock - mountedVolEntry.FirstBlock);

            foreach(PascalFileEntry entry in fileEntries) stat.FreeBlocks -= entry.LastBlock - entry.FirstBlock;

            return Errno.NotImplemented;
        }
    }
}