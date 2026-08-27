using System;
using System.Globalization;

namespace DingoSteamNetworking
{
    public class SteamNetworkingConfiguration
    {
        public const int DEFAULT_LOBBY_MEMBER_LIMIT = 8;
        public const int DEFAULT_MESSAGE_CHANNEL = 0;
        public const int DEFAULT_MAX_PAYLOAD_BYTES = 32 * 1024;
        public const int DEFAULT_MAX_MESSAGES_PER_TICK = 64;

        public readonly string ProductId;
        public readonly ushort ProtocolVersion;
        public readonly string BuildId;
        public readonly int LobbyMemberLimit;
        public readonly int MessageChannel;
        public readonly int MaxPayloadBytes;
        public readonly int MaxMessagesPerTick;

        public string ProtocolVersionText => ProtocolVersion.ToString(CultureInfo.InvariantCulture);

        public SteamNetworkingConfiguration(
            string productId,
            ushort protocolVersion,
            string buildId,
            int lobbyMemberLimit = DEFAULT_LOBBY_MEMBER_LIMIT,
            int messageChannel = DEFAULT_MESSAGE_CHANNEL,
            int maxPayloadBytes = DEFAULT_MAX_PAYLOAD_BYTES,
            int maxMessagesPerTick = DEFAULT_MAX_MESSAGES_PER_TICK)
        {
            if (string.IsNullOrWhiteSpace(productId))
                throw new ArgumentException("Product ID is required.", nameof(productId));
            if (productId.Length > 64)
                throw new ArgumentOutOfRangeException(nameof(productId), "Product ID cannot exceed 64 characters.");
            if (protocolVersion == 0)
                throw new ArgumentOutOfRangeException(nameof(protocolVersion), "Protocol version must be greater than zero.");
            if (string.IsNullOrWhiteSpace(buildId))
                throw new ArgumentException("Build ID is required.", nameof(buildId));
            if (buildId.Length > 64)
                throw new ArgumentOutOfRangeException(nameof(buildId), "Build ID cannot exceed 64 characters.");
            if (lobbyMemberLimit < 2 || lobbyMemberLimit > 250)
                throw new ArgumentOutOfRangeException(nameof(lobbyMemberLimit), "Lobby member limit must be between 2 and 250.");
            if (messageChannel < 0)
                throw new ArgumentOutOfRangeException(nameof(messageChannel), "Message channel cannot be negative.");
            if (maxPayloadBytes < 1 || maxPayloadBytes > SteamMessageEnvelopeCodec.MAX_PAYLOAD_BYTES)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxPayloadBytes),
                    $"Payload limit must be between 1 and {SteamMessageEnvelopeCodec.MAX_PAYLOAD_BYTES} bytes.");
            }
            if (maxMessagesPerTick < 1 || maxMessagesPerTick > 1024)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxMessagesPerTick),
                    "Messages per tick must be between 1 and 1024.");
            }

            ProductId = productId;
            ProtocolVersion = protocolVersion;
            BuildId = buildId;
            LobbyMemberLimit = lobbyMemberLimit;
            MessageChannel = messageChannel;
            MaxPayloadBytes = maxPayloadBytes;
            MaxMessagesPerTick = maxMessagesPerTick;
        }
    }
}
