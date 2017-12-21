// /***************************************************************************
// The Disc Image Chef
// ----------------------------------------------------------------------------
//
// Filename       : Command.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : FreeBSD direct device access.
//
// --[ Description ] ----------------------------------------------------------
//
//     Contains a high level representation of the FreeBSD syscalls used to
//     directly interface devices.
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
using System.Runtime.InteropServices;
using DiscImageChef.Console;
using DiscImageChef.Decoders.ATA;
using static DiscImageChef.Devices.FreeBSD.Extern;

namespace DiscImageChef.Devices.FreeBSD
{
    static class Command
    {
        const int CAM_MAX_CDBLEN = 16;

        /// <summary>
        /// Sends a SCSI command (64-bit arch)
        /// </summary>
        /// <returns>0 if no error occurred, otherwise, errno</returns>
        /// <param name="dev">CAM device</param>
        /// <param name="cdb">SCSI CDB</param>
        /// <param name="buffer">Buffer for SCSI command response</param>
        /// <param name="senseBuffer">Buffer with the SCSI sense</param>
        /// <param name="timeout">Timeout in seconds</param>
        /// <param name="direction">SCSI command transfer direction</param>
        /// <param name="duration">Time it took to execute the command in milliseconds</param>
        /// <param name="sense"><c>True</c> if SCSI error returned non-OK status and <paramref name="senseBuffer"/> contains SCSI sense</param>
        internal static int SendScsiCommand64(IntPtr dev, byte[] cdb, ref byte[] buffer, out byte[] senseBuffer,
                                              uint timeout, CcbFlags direction, out double duration, out bool sense)
        {
            senseBuffer = null;
            duration = 0;
            sense = false;

            if(buffer == null) return -1;

            IntPtr ccbPtr = cam_getccb(dev);
            IntPtr cdbPtr = IntPtr.Zero;

            if(ccbPtr.ToInt64() == 0)
            {
                sense = true;
                return Marshal.GetLastWin32Error();
            }

            CcbScsiio64 csio = (CcbScsiio64)Marshal.PtrToStructure(ccbPtr, typeof(CcbScsiio64));
            csio.ccb_h.func_code = XptOpcode.XptScsiIo;
            csio.ccb_h.flags = direction;
            csio.ccb_h.xflags = 0;
            csio.ccb_h.retry_count = 1;
            csio.ccb_h.cbfcnp = IntPtr.Zero;
            csio.ccb_h.timeout = timeout;
            csio.data_ptr = Marshal.AllocHGlobal(buffer.Length);
            csio.dxfer_len = (uint)buffer.Length;
            csio.sense_len = 32;
            csio.cdb_len = (byte)cdb.Length;
            // TODO: Create enum?
            csio.tag_action = 0x20;
            csio.cdb_bytes = new byte[CAM_MAX_CDBLEN];
            if(cdb.Length <= CAM_MAX_CDBLEN) Array.Copy(cdb, 0, csio.cdb_bytes, 0, cdb.Length);
            else
            {
                cdbPtr = Marshal.AllocHGlobal(cdb.Length);
                byte[] cdbPtrBytes = BitConverter.GetBytes(cdbPtr.ToInt64());
                Array.Copy(cdbPtrBytes, 0, csio.cdb_bytes, 0, IntPtr.Size);
                csio.ccb_h.flags |= CcbFlags.CamCdbPointer;
            }
            csio.ccb_h.flags |= CcbFlags.CamDevQfrzdis;

            Marshal.Copy(buffer, 0, csio.data_ptr, buffer.Length);
            Marshal.StructureToPtr(csio, ccbPtr, false);

            DateTime start = DateTime.UtcNow;
            int error = cam_send_ccb(dev, ccbPtr);
            DateTime end = DateTime.UtcNow;

            if(error < 0) error = Marshal.GetLastWin32Error();

            csio = (CcbScsiio64)Marshal.PtrToStructure(ccbPtr, typeof(CcbScsiio64));

            if((csio.ccb_h.status & CamStatus.CamStatusMask) != CamStatus.CamReqCmp &&
               (csio.ccb_h.status & CamStatus.CamStatusMask) != CamStatus.CamScsiStatusError)
            {
                error = Marshal.GetLastWin32Error();
                DicConsole.DebugWriteLine("FreeBSD devices", "CAM status {0} error {1}", csio.ccb_h.status, error);
                sense = true;
            }

            if((csio.ccb_h.status & CamStatus.CamStatusMask) == CamStatus.CamScsiStatusError)
            {
                sense = true;
                senseBuffer = new byte[1];
                senseBuffer[0] = csio.scsi_status;
            }

            if((csio.ccb_h.status & CamStatus.CamAutosnsValid) != 0)
                if(csio.sense_len - csio.sense_resid > 0)
                {
                    sense = (csio.ccb_h.status & CamStatus.CamStatusMask) == CamStatus.CamScsiStatusError;
                    senseBuffer = new byte[csio.sense_len - csio.sense_resid];
                    senseBuffer[0] = csio.sense_data.error_code;
                    Array.Copy(csio.sense_data.sense_buf, 0, senseBuffer, 1, senseBuffer.Length - 1);
                }

            buffer = new byte[csio.dxfer_len];
            cdb = new byte[csio.cdb_len];

            Marshal.Copy(csio.data_ptr, buffer, 0, buffer.Length);
            if(csio.ccb_h.flags.HasFlag(CcbFlags.CamCdbPointer))
                Marshal.Copy(new IntPtr(BitConverter.ToInt64(csio.cdb_bytes, 0)), cdb, 0, cdb.Length);
            else Array.Copy(csio.cdb_bytes, 0, cdb, 0, cdb.Length);
            duration = (end - start).TotalMilliseconds;

            if(csio.ccb_h.flags.HasFlag(CcbFlags.CamCdbPointer)) Marshal.FreeHGlobal(cdbPtr);
            Marshal.FreeHGlobal(csio.data_ptr);
            cam_freeccb(ccbPtr);

            return error;
        }

        /// <summary>
        /// Sends a SCSI command (32-bit arch)
        /// </summary>
        /// <returns>0 if no error occurred, otherwise, errno</returns>
        /// <param name="dev">CAM device</param>
        /// <param name="cdb">SCSI CDB</param>
        /// <param name="buffer">Buffer for SCSI command response</param>
        /// <param name="senseBuffer">Buffer with the SCSI sense</param>
        /// <param name="timeout">Timeout in seconds</param>
        /// <param name="direction">SCSI command transfer direction</param>
        /// <param name="duration">Time it took to execute the command in milliseconds</param>
        /// <param name="sense"><c>True</c> if SCSI error returned non-OK status and <paramref name="senseBuffer"/> contains SCSI sense</param>
        internal static int SendScsiCommand(IntPtr dev, byte[] cdb, ref byte[] buffer, out byte[] senseBuffer,
                                            uint timeout, CcbFlags direction, out double duration, out bool sense)
        {
            senseBuffer = null;
            duration = 0;
            sense = false;

            if(buffer == null) return -1;

            IntPtr ccbPtr = cam_getccb(dev);
            IntPtr cdbPtr = IntPtr.Zero;

            if(ccbPtr.ToInt32() == 0)
            {
                sense = true;
                return Marshal.GetLastWin32Error();
            }

            CcbScsiio csio = (CcbScsiio)Marshal.PtrToStructure(ccbPtr, typeof(CcbScsiio));
            csio.ccb_h.func_code = XptOpcode.XptScsiIo;
            csio.ccb_h.flags = direction;
            csio.ccb_h.xflags = 0;
            csio.ccb_h.retry_count = 1;
            csio.ccb_h.cbfcnp = IntPtr.Zero;
            csio.ccb_h.timeout = timeout;
            csio.data_ptr = Marshal.AllocHGlobal(buffer.Length);
            csio.dxfer_len = (uint)buffer.Length;
            csio.sense_len = 32;
            csio.cdb_len = (byte)cdb.Length;
            // TODO: Create enum?
            csio.tag_action = 0x20;
            csio.cdb_bytes = new byte[CAM_MAX_CDBLEN];
            if(cdb.Length <= CAM_MAX_CDBLEN) Array.Copy(cdb, 0, csio.cdb_bytes, 0, cdb.Length);
            else
            {
                cdbPtr = Marshal.AllocHGlobal(cdb.Length);
                byte[] cdbPtrBytes = BitConverter.GetBytes(cdbPtr.ToInt32());
                Array.Copy(cdbPtrBytes, 0, csio.cdb_bytes, 0, IntPtr.Size);
                csio.ccb_h.flags |= CcbFlags.CamCdbPointer;
            }
            csio.ccb_h.flags |= CcbFlags.CamDevQfrzdis;

            Marshal.Copy(buffer, 0, csio.data_ptr, buffer.Length);
            Marshal.StructureToPtr(csio, ccbPtr, false);

            DateTime start = DateTime.UtcNow;
            int error = cam_send_ccb(dev, ccbPtr);
            DateTime end = DateTime.UtcNow;

            if(error < 0) error = Marshal.GetLastWin32Error();

            csio = (CcbScsiio)Marshal.PtrToStructure(ccbPtr, typeof(CcbScsiio));

            if((csio.ccb_h.status & CamStatus.CamStatusMask) != CamStatus.CamReqCmp &&
               (csio.ccb_h.status & CamStatus.CamStatusMask) != CamStatus.CamScsiStatusError)
            {
                error = Marshal.GetLastWin32Error();
                DicConsole.DebugWriteLine("FreeBSD devices", "CAM status {0} error {1}", csio.ccb_h.status, error);
                sense = true;
            }

            if((csio.ccb_h.status & CamStatus.CamStatusMask) == CamStatus.CamScsiStatusError)
            {
                sense = true;
                senseBuffer = new byte[1];
                senseBuffer[0] = csio.scsi_status;
            }

            if((csio.ccb_h.status & CamStatus.CamAutosnsValid) != 0)
                if(csio.sense_len - csio.sense_resid > 0)
                {
                    sense = (csio.ccb_h.status & CamStatus.CamStatusMask) == CamStatus.CamScsiStatusError;
                    senseBuffer = new byte[csio.sense_len - csio.sense_resid];
                    senseBuffer[0] = csio.sense_data.error_code;
                    Array.Copy(csio.sense_data.sense_buf, 0, senseBuffer, 1, senseBuffer.Length - 1);
                }

            buffer = new byte[csio.dxfer_len];
            cdb = new byte[csio.cdb_len];

            Marshal.Copy(csio.data_ptr, buffer, 0, buffer.Length);
            if(csio.ccb_h.flags.HasFlag(CcbFlags.CamCdbPointer))
                Marshal.Copy(new IntPtr(BitConverter.ToInt32(csio.cdb_bytes, 0)), cdb, 0, cdb.Length);
            else Array.Copy(csio.cdb_bytes, 0, cdb, 0, cdb.Length);
            duration = (end - start).TotalMilliseconds;

            if(csio.ccb_h.flags.HasFlag(CcbFlags.CamCdbPointer)) Marshal.FreeHGlobal(cdbPtr);
            Marshal.FreeHGlobal(csio.data_ptr);
            cam_freeccb(ccbPtr);

            return error;
        }

        static CcbFlags AtaProtocolToCamFlags(AtaProtocol protocol)
        {
            switch(protocol)
            {
                case AtaProtocol.DeviceDiagnostic:
                case AtaProtocol.DeviceReset:
                case AtaProtocol.HardReset:
                case AtaProtocol.NonData:
                case AtaProtocol.SoftReset:
                case AtaProtocol.ReturnResponse: return CcbFlags.CamDirNone;
                case AtaProtocol.PioIn:
                case AtaProtocol.UDmaIn: return CcbFlags.CamDirIn;
                case AtaProtocol.PioOut:
                case AtaProtocol.UDmaOut: return CcbFlags.CamDirOut;
                default: return CcbFlags.CamDirNone;
            }
        }

        internal static int SendAtaCommand(IntPtr dev, AtaRegistersCHS registers,
                                           out AtaErrorRegistersCHS errorRegisters, AtaProtocol protocol,
                                           ref byte[] buffer, uint timeout, out double duration, out bool sense)
        {
            duration = 0;
            sense = false;
            errorRegisters = new AtaErrorRegistersCHS();

            if(buffer == null) return -1;

            IntPtr ccbPtr = cam_getccb(dev);

            CcbAtaio ataio = (CcbAtaio)Marshal.PtrToStructure(ccbPtr, typeof(CcbAtaio));
            ataio.ccb_h.func_code = XptOpcode.XptAtaIo;
            ataio.ccb_h.flags = AtaProtocolToCamFlags(protocol);
            ataio.ccb_h.xflags = 0;
            ataio.ccb_h.retry_count = 1;
            ataio.ccb_h.cbfcnp = IntPtr.Zero;
            ataio.ccb_h.timeout = timeout;
            ataio.data_ptr = Marshal.AllocHGlobal(buffer.Length);
            ataio.dxfer_len = (uint)buffer.Length;
            ataio.ccb_h.flags |= CcbFlags.CamDevQfrzdis;
            ataio.cmd.flags = CamAtaIoFlags.NeedResult;
            switch(protocol)
            {
                case AtaProtocol.Dma:
                case AtaProtocol.DmaQueued:
                case AtaProtocol.UDmaIn:
                case AtaProtocol.UDmaOut:
                    ataio.cmd.flags |= CamAtaIoFlags.Dma;
                    break;
                case AtaProtocol.FpDma:
                    ataio.cmd.flags |= CamAtaIoFlags.Fpdma;
                    break;
            }

            ataio.cmd.command = registers.command;
            ataio.cmd.lba_high = registers.cylinderHigh;
            ataio.cmd.lba_mid = registers.cylinderLow;
            ataio.cmd.device = (byte)(0x40 | registers.deviceHead);
            ataio.cmd.features = registers.feature;
            ataio.cmd.sector_count = registers.sectorCount;
            ataio.cmd.lba_low = registers.sector;

            Marshal.Copy(buffer, 0, ataio.data_ptr, buffer.Length);
            Marshal.StructureToPtr(ataio, ccbPtr, false);

            DateTime start = DateTime.UtcNow;
            int error = cam_send_ccb(dev, ccbPtr);
            DateTime end = DateTime.UtcNow;

            if(error < 0) error = Marshal.GetLastWin32Error();

            ataio = (CcbAtaio)Marshal.PtrToStructure(ccbPtr, typeof(CcbAtaio));

            if((ataio.ccb_h.status & CamStatus.CamStatusMask) != CamStatus.CamReqCmp &&
               (ataio.ccb_h.status & CamStatus.CamStatusMask) != CamStatus.CamScsiStatusError)
            {
                error = Marshal.GetLastWin32Error();
                DicConsole.DebugWriteLine("FreeBSD devices", "CAM status {0} error {1}", ataio.ccb_h.status, error);
                sense = true;
            }

            if((ataio.ccb_h.status & CamStatus.CamStatusMask) == CamStatus.CamAtaStatusError) sense = true;

            errorRegisters.cylinderHigh = ataio.res.lba_high;
            errorRegisters.cylinderLow = ataio.res.lba_mid;
            errorRegisters.deviceHead = ataio.res.device;
            errorRegisters.error = ataio.res.error;
            errorRegisters.sector = ataio.res.lba_low;
            errorRegisters.sectorCount = ataio.res.sector_count;
            errorRegisters.status = ataio.res.status;

            buffer = new byte[ataio.dxfer_len];

            Marshal.Copy(ataio.data_ptr, buffer, 0, buffer.Length);
            duration = (end - start).TotalMilliseconds;

            Marshal.FreeHGlobal(ataio.data_ptr);
            cam_freeccb(ccbPtr);

            sense = errorRegisters.error != 0 || (errorRegisters.status & 0xA5) != 0 || error != 0;

            return error;
        }

        internal static int SendAtaCommand(IntPtr dev, AtaRegistersLBA28 registers,
                                           out AtaErrorRegistersLBA28 errorRegisters, AtaProtocol protocol,
                                           ref byte[] buffer, uint timeout, out double duration, out bool sense)
        {
            duration = 0;
            sense = false;
            errorRegisters = new AtaErrorRegistersLBA28();

            if(buffer == null) return -1;

            IntPtr ccbPtr = cam_getccb(dev);

            CcbAtaio ataio = (CcbAtaio)Marshal.PtrToStructure(ccbPtr, typeof(CcbAtaio));
            ataio.ccb_h.func_code = XptOpcode.XptAtaIo;
            ataio.ccb_h.flags = AtaProtocolToCamFlags(protocol);
            ataio.ccb_h.xflags = 0;
            ataio.ccb_h.retry_count = 1;
            ataio.ccb_h.cbfcnp = IntPtr.Zero;
            ataio.ccb_h.timeout = timeout;
            ataio.data_ptr = Marshal.AllocHGlobal(buffer.Length);
            ataio.dxfer_len = (uint)buffer.Length;
            ataio.ccb_h.flags |= CcbFlags.CamDevQfrzdis;
            ataio.cmd.flags = CamAtaIoFlags.NeedResult;
            switch(protocol)
            {
                case AtaProtocol.Dma:
                case AtaProtocol.DmaQueued:
                case AtaProtocol.UDmaIn:
                case AtaProtocol.UDmaOut:
                    ataio.cmd.flags |= CamAtaIoFlags.Dma;
                    break;
                case AtaProtocol.FpDma:
                    ataio.cmd.flags |= CamAtaIoFlags.Fpdma;
                    break;
            }

            ataio.cmd.command = registers.command;
            ataio.cmd.lba_high = registers.lbaHigh;
            ataio.cmd.lba_mid = registers.lbaMid;
            ataio.cmd.device = (byte)(0x40 | registers.deviceHead);
            ataio.cmd.features = registers.feature;
            ataio.cmd.sector_count = registers.sectorCount;
            ataio.cmd.lba_low = registers.lbaLow;

            Marshal.Copy(buffer, 0, ataio.data_ptr, buffer.Length);
            Marshal.StructureToPtr(ataio, ccbPtr, false);

            DateTime start = DateTime.UtcNow;
            int error = cam_send_ccb(dev, ccbPtr);
            DateTime end = DateTime.UtcNow;

            if(error < 0) error = Marshal.GetLastWin32Error();

            ataio = (CcbAtaio)Marshal.PtrToStructure(ccbPtr, typeof(CcbAtaio));

            if((ataio.ccb_h.status & CamStatus.CamStatusMask) != CamStatus.CamReqCmp &&
               (ataio.ccb_h.status & CamStatus.CamStatusMask) != CamStatus.CamScsiStatusError)
            {
                error = Marshal.GetLastWin32Error();
                DicConsole.DebugWriteLine("FreeBSD devices", "CAM status {0} error {1}", ataio.ccb_h.status, error);
                sense = true;
            }

            if((ataio.ccb_h.status & CamStatus.CamStatusMask) == CamStatus.CamAtaStatusError) sense = true;

            errorRegisters.lbaHigh = ataio.res.lba_high;
            errorRegisters.lbaMid = ataio.res.lba_mid;
            errorRegisters.deviceHead = ataio.res.device;
            errorRegisters.error = ataio.res.error;
            errorRegisters.lbaLow = ataio.res.lba_low;
            errorRegisters.sectorCount = ataio.res.sector_count;
            errorRegisters.status = ataio.res.status;

            buffer = new byte[ataio.dxfer_len];

            Marshal.Copy(ataio.data_ptr, buffer, 0, buffer.Length);
            duration = (end - start).TotalMilliseconds;

            Marshal.FreeHGlobal(ataio.data_ptr);
            cam_freeccb(ccbPtr);

            sense = errorRegisters.error != 0 || (errorRegisters.status & 0xA5) != 0 || error != 0;

            return error;
        }

        internal static int SendAtaCommand(IntPtr dev, AtaRegistersLBA48 registers,
                                           out AtaErrorRegistersLBA48 errorRegisters, AtaProtocol protocol,
                                           ref byte[] buffer, uint timeout, out double duration, out bool sense)
        {
            duration = 0;
            sense = false;
            errorRegisters = new AtaErrorRegistersLBA48();

            // 48-bit ATA CAM commands can crash FreeBSD < 9.2-RELEASE
            if(Environment.Version.Major == 9 && Environment.Version.Minor < 2 ||
               Environment.Version.Major < 9) return -1;

            if(buffer == null) return -1;

            IntPtr ccbPtr = cam_getccb(dev);

            CcbAtaio ataio = (CcbAtaio)Marshal.PtrToStructure(ccbPtr, typeof(CcbAtaio));
            ataio.ccb_h.func_code = XptOpcode.XptAtaIo;
            ataio.ccb_h.flags = AtaProtocolToCamFlags(protocol);
            ataio.ccb_h.xflags = 0;
            ataio.ccb_h.retry_count = 1;
            ataio.ccb_h.cbfcnp = IntPtr.Zero;
            ataio.ccb_h.timeout = timeout;
            ataio.data_ptr = Marshal.AllocHGlobal(buffer.Length);
            ataio.dxfer_len = (uint)buffer.Length;
            ataio.ccb_h.flags |= CcbFlags.CamDevQfrzdis;
            ataio.cmd.flags = CamAtaIoFlags.NeedResult | CamAtaIoFlags.ExtendedCommand;
            switch(protocol)
            {
                case AtaProtocol.Dma:
                case AtaProtocol.DmaQueued:
                case AtaProtocol.UDmaIn:
                case AtaProtocol.UDmaOut:
                    ataio.cmd.flags |= CamAtaIoFlags.Dma;
                    break;
                case AtaProtocol.FpDma:
                    ataio.cmd.flags |= CamAtaIoFlags.Fpdma;
                    break;
            }

            ataio.cmd.lba_high_exp = (byte)((registers.lbaHigh & 0xFF00) >> 8);
            ataio.cmd.lba_mid_exp = (byte)((registers.lbaMid & 0xFF00) >> 8);
            ataio.cmd.features_exp = (byte)((registers.feature & 0xFF00) >> 8);
            ataio.cmd.sector_count_exp = (byte)((registers.sectorCount & 0xFF00) >> 8);
            ataio.cmd.lba_low_exp = (byte)((registers.lbaLow & 0xFF00) >> 8);
            ataio.cmd.lba_high = (byte)(registers.lbaHigh & 0xFF);
            ataio.cmd.lba_mid = (byte)(registers.lbaMid & 0xFF);
            ataio.cmd.features = (byte)(registers.feature & 0xFF);
            ataio.cmd.sector_count = (byte)(registers.sectorCount & 0xFF);
            ataio.cmd.lba_low = (byte)(registers.lbaLow & 0xFF);
            ataio.cmd.command = registers.command;
            ataio.cmd.device = (byte)(0x40 | registers.deviceHead);

            Marshal.Copy(buffer, 0, ataio.data_ptr, buffer.Length);
            Marshal.StructureToPtr(ataio, ccbPtr, false);

            DateTime start = DateTime.UtcNow;
            int error = cam_send_ccb(dev, ccbPtr);
            DateTime end = DateTime.UtcNow;

            if(error < 0) error = Marshal.GetLastWin32Error();

            ataio = (CcbAtaio)Marshal.PtrToStructure(ccbPtr, typeof(CcbAtaio));

            if((ataio.ccb_h.status & CamStatus.CamStatusMask) != CamStatus.CamReqCmp &&
               (ataio.ccb_h.status & CamStatus.CamStatusMask) != CamStatus.CamScsiStatusError)
            {
                error = Marshal.GetLastWin32Error();
                DicConsole.DebugWriteLine("FreeBSD devices", "CAM status {0} error {1}", ataio.ccb_h.status, error);
                sense = true;
            }

            if((ataio.ccb_h.status & CamStatus.CamStatusMask) == CamStatus.CamAtaStatusError) sense = true;

            errorRegisters.sectorCount = (ushort)((ataio.res.sector_count_exp << 8) + ataio.res.sector_count);
            errorRegisters.lbaLow = (ushort)((ataio.res.lba_low_exp << 8) + ataio.res.lba_low);
            errorRegisters.lbaMid = (ushort)((ataio.res.lba_mid_exp << 8) + ataio.res.lba_mid);
            errorRegisters.lbaHigh = (ushort)((ataio.res.lba_high_exp << 8) + ataio.res.lba_high);
            errorRegisters.deviceHead = ataio.res.device;
            errorRegisters.error = ataio.res.error;
            errorRegisters.status = ataio.res.status;

            buffer = new byte[ataio.dxfer_len];

            Marshal.Copy(ataio.data_ptr, buffer, 0, buffer.Length);
            duration = (end - start).TotalMilliseconds;

            Marshal.FreeHGlobal(ataio.data_ptr);
            cam_freeccb(ccbPtr);

            sense = errorRegisters.error != 0 || (errorRegisters.status & 0xA5) != 0 || error != 0;

            return error;
        }
    }
}