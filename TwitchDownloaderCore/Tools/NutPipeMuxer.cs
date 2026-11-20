using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;

namespace TwitchDownloaderCore.Tools
{
    /// <summary>
    /// Zero-allocation binary muxer for FFmpeg's lightweight NUT (<c>-f nut</c>) container over an unseekable pipe.
    /// Allows streaming only unique raw video frames tagged with exact presentation timestamps (PTS in <c>1/fps</c>
    /// timebase) instead of writing identical multi-megabyte frame buffers repeatedly on every tick.
    /// </summary>
    internal sealed class NutPipeMuxer
    {
        private const ulong MainStartcode = 0x4E4D7A561F5F04ADUL;
        private const ulong StreamStartcode = 0x4E5311405BF2F9DBUL;
        private const ulong SyncpointStartcode = 0x4E4BE4ADEECA4569UL;

        // Frame code 1 flags: FLAG_KEY (1) | FLAG_CODED_PTS (8) | FLAG_SIZE_MSB (32) | FLAG_CHECKSUM (64) = 105
        private const ulong ActiveFrameFlags = 1UL | 8UL | 32UL | 64UL;
        private const ulong InvalidFrameFlags = 4UL;
        private const int MsbPtsShift = 8;
        private const ulong CodedPtsBias = 1UL << MsbPtsShift; // 256

        private static readonly uint[] CrcTable = BuildCrcTable();

        private readonly byte[] _scratch = new byte[128];
        private long _bytesWritten;
        private long _lastSyncpointPos;

        public long BytesWritten => _bytesWritten;

        private static uint[] BuildCrcTable()
        {
            const uint poly = 0x04C11DB7u;
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i << 24;
                for (int bit = 0; bit < 8; bit++)
                {
                    c = (c & 0x80000000u) != 0 ? (c << 1) ^ poly : (c << 1);
                }
                table[i] = c;
            }
            return table;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint ComputeCrc32(ReadOnlySpan<byte> data)
        {
            uint crc = 0;
            var table = CrcTable;
            for (int i = 0; i < data.Length; i++)
            {
                crc = (crc << 8) ^ table[((crc >> 24) ^ data[i]) & 0xFF];
            }
            return crc;
        }

        public static ReadOnlySpan<byte> GetFourCc(string pixFmt)
        {
            if (string.Equals(pixFmt, "yuva420p", StringComparison.OrdinalIgnoreCase))
            {
                return [0x59, 0x34, 0x0B, 0x08]; // Y4\x0B\x08 (FFmpeg NUT planar YUVA 4:2:0 8-bit)
            }
            if (string.Equals(pixFmt, "yuv420p", StringComparison.OrdinalIgnoreCase))
            {
                return "I420"u8;
            }
            if (string.Equals(pixFmt, "bgra", StringComparison.OrdinalIgnoreCase))
            {
                return "BGRA"u8;
            }
            if (string.Equals(pixFmt, "rgba", StringComparison.OrdinalIgnoreCase))
            {
                return "RGBA"u8;
            }
            if (string.Equals(pixFmt, "gray", StringComparison.OrdinalIgnoreCase))
            {
                return "Y800"u8;
            }

            throw new ArgumentException($"Unsupported NUT raw pixel format '{pixFmt}'.", nameof(pixFmt));
        }

        public void WriteHeader(Stream stream, int width, int height, int fps, ReadOnlySpan<byte> fourCc)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
            if (fourCc.Length != 4) throw new ArgumentException("NUT FourCC must be 4 bytes.", nameof(fourCc));

            _bytesWritten = 0;
            _lastSyncpointPos = 0;

            // 1. Official 25-byte NUT file ID header
            ReadOnlySpan<byte> fileId = "nut/multimedia container\0"u8;
            stream.Write(fileId);
            _bytesWritten += fileId.Length;

            // 2. Main Header packet
            Span<byte> payload = stackalloc byte[96];
            int pLen = 0;
            pLen += WriteVarUInt(payload[pLen..], 3);              // version = 3
            pLen += WriteVarUInt(payload[pLen..], 1);              // stream_count = 1
            pLen += WriteVarUInt(payload[pLen..], 65536);          // max_distance = 65536
            pLen += WriteVarUInt(payload[pLen..], 1);              // time_base_count = 1
            pLen += WriteVarUInt(payload[pLen..], 1);              // time_base_num = 1
            pLen += WriteVarUInt(payload[pLen..], (ulong)fps);     // time_base_den = fps

            // Frame code table (256 entries, skipping 0x4E 'N'):
            // Entry 0 (code 0): invalid
            pLen += WriteFrameCodeGroup(payload[pLen..], InvalidFrameFlags, count: 1);
            // Entry 1 (code 1): keyframe + explicit coded PTS + explicit MSB size + 4-byte frame header checksum
            pLen += WriteFrameCodeGroup(payload[pLen..], ActiveFrameFlags, count: 1);
            // Entries 2..255 excluding 'N' (0x4E) => 253 entries: invalid
            pLen += WriteFrameCodeGroup(payload[pLen..], InvalidFrameFlags, count: 253);

            pLen += WriteVarUInt(payload[pLen..], 0);              // header_count_minus1 = 0
            pLen += WriteVarUInt(payload[pLen..], 0);              // main_flags = 0

            WritePacket(stream, MainStartcode, payload[..pLen]);

            // 3. Video Stream Header packet
            pLen = 0;
            pLen += WriteVarUInt(payload[pLen..], 0);              // stream_id = 0
            pLen += WriteVarUInt(payload[pLen..], 0);              // stream_class = 0 (video)
            pLen += WriteVarBytes(payload[pLen..], fourCc);        // fourcc
            pLen += WriteVarUInt(payload[pLen..], 0);              // time_base_id = 0
            pLen += WriteVarUInt(payload[pLen..], MsbPtsShift);    // msb_pts_shift = 8
            pLen += WriteVarUInt(payload[pLen..], 10_000_000);     // max_pts_distance
            pLen += WriteVarUInt(payload[pLen..], 0);              // decode_delay = 0
            pLen += WriteVarUInt(payload[pLen..], 0);              // stream_flags = 0
            pLen += WriteVarUInt(payload[pLen..], 0);              // codec_specific_data length = 0
            pLen += WriteVarUInt(payload[pLen..], (ulong)width);   // width
            pLen += WriteVarUInt(payload[pLen..], (ulong)height);  // height
            pLen += WriteVarUInt(payload[pLen..], 1);              // sample_width = 1
            pLen += WriteVarUInt(payload[pLen..], 1);              // sample_height = 1
            pLen += WriteVarUInt(payload[pLen..], 0);              // colorspace_type = 0 (unknown / signaled by codec)

            WritePacket(stream, StreamStartcode, payload[..pLen]);
        }

        /// <summary>
        /// Writes a single raw video frame at presentation timestamp <paramref name="pts"/> (in <c>1/fps</c> ticks).
        /// Precedes the frame with a compact 17-byte syncpoint and 4-byte frame-header CRC32 (zero pixel-payload CRC).
        /// </summary>
        public void WriteFrame(Stream stream, long pts, ReadOnlySpan<byte> frameBytes)
        {
            if (pts < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pts));
            }

            Span<byte> syncPayload = stackalloc byte[24];
            ulong backPtrDiv16 = _lastSyncpointPos > 0
                ? (ulong)((_bytesWritten - _lastSyncpointPos) >> 4)
                : 0UL;
            _lastSyncpointPos = _bytesWritten;

            int syncPayloadLen = 0;
            syncPayloadLen += WriteVarUInt(syncPayload[syncPayloadLen..], (ulong)pts); // global_key_pts (tb_count == 1)
            syncPayloadLen += WriteVarUInt(syncPayload[syncPayloadLen..], backPtrDiv16);

            Span<byte> scratch = _scratch;
            int offset = FormatPacket(scratch, SyncpointStartcode, syncPayload[..syncPayloadLen]);

            // Frame header: [frame_code = 1][coded_pts = pts + 256][data_size_msb = frameBytes.Length][crc32(frame_header)]
            int frameHdrStart = offset;
            scratch[offset++] = 1;
            offset += WriteVarUInt(scratch[offset..], (ulong)pts + CodedPtsBias);
            offset += WriteVarUInt(scratch[offset..], (ulong)frameBytes.Length);

            uint hdrCrc = ComputeCrc32(scratch[frameHdrStart..offset]);
            BinaryPrimitives.WriteUInt32BigEndian(scratch[offset..], hdrCrc);
            offset += 4;

            stream.Write(scratch[..offset]);
            stream.Write(frameBytes);
            _bytesWritten += offset + frameBytes.Length;
        }

        private static int WriteFrameCodeGroup(Span<byte> dst, ulong flags, ulong count)
        {
            int pos = 0;
            pos += WriteVarUInt(dst[pos..], flags); // tmp_flags
            pos += WriteVarUInt(dst[pos..], 6);     // tmp_fields = 6
            pos += WriteVarSInt(dst[pos..], 0);     // tmp_pts = 0
            pos += WriteVarUInt(dst[pos..], 1);     // tmp_mul = 1
            pos += WriteVarUInt(dst[pos..], 0);     // tmp_stream = 0
            pos += WriteVarUInt(dst[pos..], 0);     // tmp_size = 0
            pos += WriteVarUInt(dst[pos..], 0);     // tmp_res = 0
            pos += WriteVarUInt(dst[pos..], count); // count
            return pos;
        }

        private void WritePacket(Stream stream, ulong startcode, ReadOnlySpan<byte> payload)
        {
            Span<byte> packet = stackalloc byte[128];
            int len = FormatPacket(packet, startcode, payload);
            stream.Write(packet[..len]);
            _bytesWritten += len;
        }

        private static int FormatPacket(Span<byte> dst, ulong startcode, ReadOnlySpan<byte> payload)
        {
            ulong forwardPtr = (ulong)(payload.Length + 4);
            int pos = 0;
            BinaryPrimitives.WriteUInt64BigEndian(dst[pos..], startcode);
            pos += 8;
            pos += WriteVarUInt(dst[pos..], forwardPtr);

            if (forwardPtr > 4096)
            {
                uint headerCrc = ComputeCrc32(dst[..pos]);
                BinaryPrimitives.WriteUInt32BigEndian(dst[pos..], headerCrc);
                pos += 4;
            }

            payload.CopyTo(dst[pos..]);
            pos += payload.Length;

            uint payloadCrc = ComputeCrc32(payload);
            BinaryPrimitives.WriteUInt32BigEndian(dst[pos..], payloadCrc);
            pos += 4;
            return pos;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int WriteVarUInt(Span<byte> dst, ulong value)
        {
            Span<byte> rev = stackalloc byte[10];
            int count = 0;
            rev[count++] = (byte)(value & 0x7FUL);
            value >>= 7;
            while (value > 0)
            {
                rev[count++] = (byte)(0x80UL | (value & 0x7FUL));
                value >>= 7;
            }

            for (int i = 0; i < count; i++)
            {
                dst[i] = rev[count - 1 - i];
            }
            return count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int WriteVarSInt(Span<byte> dst, long value)
        {
            ulong temp = value < 0
                ? (((ulong)(-value)) << 1) - 1UL
                : ((ulong)value) << 1;
            return WriteVarUInt(dst, temp + 1UL);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int WriteVarBytes(Span<byte> dst, ReadOnlySpan<byte> bytes)
        {
            int pos = WriteVarUInt(dst, (ulong)bytes.Length);
            bytes.CopyTo(dst[pos..]);
            return pos + bytes.Length;
        }
    }
}
