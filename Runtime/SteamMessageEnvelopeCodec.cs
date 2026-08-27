using System;

namespace DingoSteamNetworking
{
    public static class SteamMessageEnvelopeCodec
    {
        public const byte ENVELOPE_VERSION = 1;
        public const int HEADER_SIZE = 12;
        public const int MAX_PAYLOAD_BYTES = 256 * 1024;

        private const byte MAGIC_D = 0x44;
        private const byte MAGIC_S = 0x53;
        private const byte MAGIC_N = 0x4E;
        private const byte MAGIC_1 = 0x31;

        public static bool TryEncode(
            ushort protocolVersion,
            byte messageKind,
            ReadOnlySpan<byte> payload,
            int maxPayloadBytes,
            out byte[] packet,
            out string error)
        {
            if (protocolVersion == 0)
            {
                packet = null;
                error = "Protocol version must be greater than zero.";
                return false;
            }

            if (!IsPayloadLimitValid(maxPayloadBytes))
            {
                packet = null;
                error = $"Payload limit must be between 1 and {MAX_PAYLOAD_BYTES} bytes.";
                return false;
            }

            if (payload.Length > maxPayloadBytes)
            {
                packet = null;
                error = $"Payload is {payload.Length} bytes; limit is {maxPayloadBytes}.";
                return false;
            }

            packet = new byte[HEADER_SIZE + payload.Length];
            packet[0] = MAGIC_D;
            packet[1] = MAGIC_S;
            packet[2] = MAGIC_N;
            packet[3] = MAGIC_1;
            packet[4] = ENVELOPE_VERSION;
            WriteUInt16(packet, 5, protocolVersion);
            packet[7] = messageKind;
            WriteInt32(packet, 8, payload.Length);
            payload.CopyTo(packet.AsSpan(HEADER_SIZE));
            error = string.Empty;
            return true;
        }

        public static bool TryDecode(
            ReadOnlySpan<byte> packet,
            ushort expectedProtocolVersion,
            int maxPayloadBytes,
            out byte messageKind,
            out byte[] payload,
            out string error)
        {
            messageKind = default;
            payload = null;

            if (!IsPayloadLimitValid(maxPayloadBytes))
            {
                error = $"Payload limit must be between 1 and {MAX_PAYLOAD_BYTES} bytes.";
                return false;
            }

            if (packet.Length < HEADER_SIZE)
            {
                error = "Packet is smaller than the envelope header.";
                return false;
            }

            if (packet[0] != MAGIC_D || packet[1] != MAGIC_S || packet[2] != MAGIC_N || packet[3] != MAGIC_1)
            {
                error = "Packet magic does not match DingoSteamNetworking.";
                return false;
            }

            if (packet[4] != ENVELOPE_VERSION)
            {
                error = $"Envelope version {packet[4]} is not supported.";
                return false;
            }

            var protocolVersion = ReadUInt16(packet, 5);
            if (protocolVersion != expectedProtocolVersion)
            {
                error = $"Protocol version {protocolVersion} does not match {expectedProtocolVersion}.";
                return false;
            }

            var payloadLength = ReadInt32(packet, 8);
            if (payloadLength < 0 || payloadLength > maxPayloadBytes)
            {
                error = $"Envelope payload length {payloadLength} is outside the allowed range.";
                return false;
            }

            if (packet.Length != HEADER_SIZE + payloadLength)
            {
                error = "Envelope payload length does not match packet size.";
                return false;
            }

            messageKind = packet[7];
            payload = packet.Slice(HEADER_SIZE, payloadLength).ToArray();
            error = string.Empty;
            return true;
        }

        private static bool IsPayloadLimitValid(int maxPayloadBytes)
        {
            return maxPayloadBytes >= 1 && maxPayloadBytes <= MAX_PAYLOAD_BYTES;
        }

        private static void WriteUInt16(byte[] target, int offset, ushort value)
        {
            target[offset] = (byte)value;
            target[offset + 1] = (byte)(value >> 8);
        }

        private static ushort ReadUInt16(ReadOnlySpan<byte> source, int offset)
        {
            return (ushort)(source[offset] | source[offset + 1] << 8);
        }

        private static void WriteInt32(byte[] target, int offset, int value)
        {
            target[offset] = (byte)value;
            target[offset + 1] = (byte)(value >> 8);
            target[offset + 2] = (byte)(value >> 16);
            target[offset + 3] = (byte)(value >> 24);
        }

        private static int ReadInt32(ReadOnlySpan<byte> source, int offset)
        {
            return source[offset]
                   | source[offset + 1] << 8
                   | source[offset + 2] << 16
                   | source[offset + 3] << 24;
        }
    }
}
