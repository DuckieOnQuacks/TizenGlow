using System;
using System.Net;
using System.Net.Sockets;

namespace TizenGlow.Wled
{
    /// <summary>
    /// DDP (Distributed Display Protocol) RGB sender, matching what WLED receives on UDP 4048
    /// (wled00/e131.cpp handleDDPPacket, constants in ESPAsyncE131.h).
    ///
    /// Each packet: a 10-byte header then RGB bytes.
    ///   0     flags: 0x40 = version 1, | 0x01 = PUSH (render now) on the last packet of a frame
    ///   1     sequence number, 1-15 (0 means unused), incremented per packet like WLED's own sender
    ///   2     data type 0x0B = RGB, 8 bits per channel
    ///   3     destination 1 = the display
    ///   4-7   byte offset of this packet's data in the frame, big-endian
    ///   8-9   data length in bytes, big-endian
    /// One socket and one packet buffer are reused for every frame.
    /// </summary>
    public sealed class DdpSender : IDisposable
    {
        public const int HeaderLength = 10;

        // 1440 data bytes = 480 RGB LEDs, the same split WLED's own DDP output uses. With the
        // 10-byte header that is a 1450-byte UDP payload, 1478 bytes with UDP + IPv4 headers,
        // under the 1500-byte Ethernet/Wi-Fi MTU, so packets are never IP-fragmented.
        public const int MaxDataLength = 1440;

        public const byte FlagsVersion1 = 0x40;
        public const byte FlagPush = 0x01;
        public const byte TypeRgb24 = 0x0B;
        public const byte DestinationDisplay = 1;

        private readonly UdpClient _udp = new UdpClient(AddressFamily.InterNetwork);
        private readonly byte[] _packet = new byte[HeaderLength + MaxDataLength];
        private int _sequence;

        public static int PacketsFor(int dataLength)
        {
            return Math.Max(1, (dataLength + MaxDataLength - 1) / MaxDataLength);
        }

        /// <summary>Sends rgb[0..length) as one frame; returns the packet count. Socket errors throw.</summary>
        public int Send(IPEndPoint target, byte[] rgb, int length)
        {
            int packets = 0;
            for (int offset = 0; offset < length; offset += MaxDataLength)
            {
                int chunk = Math.Min(MaxDataLength, length - offset);
                _sequence = _sequence % 15 + 1;
                _packet[0] = (byte)(offset + chunk >= length ? FlagsVersion1 | FlagPush : FlagsVersion1);
                _packet[1] = (byte)_sequence;
                _packet[2] = TypeRgb24;
                _packet[3] = DestinationDisplay;
                _packet[4] = (byte)(offset >> 24);
                _packet[5] = (byte)(offset >> 16);
                _packet[6] = (byte)(offset >> 8);
                _packet[7] = (byte)offset;
                _packet[8] = (byte)(chunk >> 8);
                _packet[9] = (byte)chunk;
                Buffer.BlockCopy(rgb, offset, _packet, HeaderLength, chunk);
                _udp.Send(_packet, HeaderLength + chunk, target);
                packets++;
            }
            return packets;
        }

        public void Dispose() { _udp.Dispose(); }
    }
}
