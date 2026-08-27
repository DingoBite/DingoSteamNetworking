#if UNITY_EDITOR && UNITY_INCLUDE_TESTS

using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace DingoSteamNetworking.Tests.Editor
{
    public class SteamNetworkingCoreTests
    {
        [Test]
        public void Configuration_ValidValues_ArePreserved()
        {
            var configuration = new SteamNetworkingConfiguration(
                "politics",
                3,
                "dev-build",
                6,
                12,
                4096,
                32,
                new[] { 4, 12, 27 });

            Assert.That(configuration.ProductId, Is.EqualTo("politics"));
            Assert.That(configuration.ProtocolVersion, Is.EqualTo(3));
            Assert.That(configuration.ProtocolVersionText, Is.EqualTo("3"));
            Assert.That(configuration.BuildId, Is.EqualTo("dev-build"));
            Assert.That(configuration.LobbyMemberLimit, Is.EqualTo(6));
            Assert.That(configuration.MessageChannel, Is.EqualTo(12));
            Assert.That(configuration.MessageChannels, Is.EqualTo(new[] { 12, 4, 27 }));
            Assert.That(configuration.HasMessageChannel(27), Is.True);
            Assert.That(configuration.HasMessageChannel(99), Is.False);
            Assert.That(configuration.MaxPayloadBytes, Is.EqualTo(4096));
            Assert.That(configuration.MaxMessagesPerTick, Is.EqualTo(32));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void Configuration_MissingProduct_Throws(string productId)
        {
            Assert.Throws<ArgumentException>(() =>
                new SteamNetworkingConfiguration(productId, 1, "build"));
        }

        [Test]
        public void Configuration_InvalidLimits_Throw()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new SteamNetworkingConfiguration("politics", 0, "build"));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new SteamNetworkingConfiguration("politics", 1, "build", lobbyMemberLimit: 1));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new SteamNetworkingConfiguration("politics", 1, "build", messageChannel: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new SteamNetworkingConfiguration("politics", 1, "build", maxPayloadBytes: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new SteamNetworkingConfiguration("politics", 1, "build", maxMessagesPerTick: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new SteamNetworkingConfiguration(
                    "politics",
                    1,
                    "build",
                    additionalMessageChannels: new[] { -1 }));
        }

        [Test]
        public void Ids_OnlyParseNonZeroUnsignedValues()
        {
            Assert.That(SteamLobbyId.TryParse("480", out var lobbyId), Is.True);
            Assert.That(lobbyId.Value, Is.EqualTo(480));
            Assert.That(SteamLobbyId.TryParse("0", out _), Is.False);
            Assert.That(SteamLobbyId.TryParse("not-a-number", out _), Is.False);

            Assert.That(SteamPeerId.TryParse("76561198000000000", out var peerId), Is.True);
            Assert.That(peerId.Value, Is.EqualTo(76561198000000000));
            Assert.That(SteamPeerId.TryParse("-1", out _), Is.False);
        }

        [Test]
        public void Envelope_RoundTrip_PreservesKindAndPayload()
        {
            var source = new byte[] { 1, 2, 3, 4, 5 };

            var encoded = SteamMessageEnvelopeCodec.TryEncode(
                7,
                42,
                source,
                1024,
                out var packet,
                out var encodeError);
            var decoded = SteamMessageEnvelopeCodec.TryDecode(
                packet,
                7,
                1024,
                out var messageKind,
                out var payload,
                out var decodeError);

            Assert.That(encoded, Is.True, encodeError);
            Assert.That(decoded, Is.True, decodeError);
            Assert.That(messageKind, Is.EqualTo(42));
            Assert.That(payload, Is.EqualTo(source));
        }

        [Test]
        public void Envelope_InvalidMagicVersionProtocolAndLength_AreRejected()
        {
            SteamMessageEnvelopeCodec.TryEncode(
                2,
                1,
                new byte[] { 9, 8, 7 },
                128,
                out var packet,
                out _);

            var invalidMagic = (byte[])packet.Clone();
            invalidMagic[0] = 0;
            Assert.That(TryDecode(invalidMagic, 2, 128), Is.False);

            var invalidVersion = (byte[])packet.Clone();
            invalidVersion[4]++;
            Assert.That(TryDecode(invalidVersion, 2, 128), Is.False);

            Assert.That(TryDecode(packet, 3, 128), Is.False);

            var invalidLength = (byte[])packet.Clone();
            invalidLength[8]++;
            Assert.That(TryDecode(invalidLength, 2, 128), Is.False);
        }

        [Test]
        public void Envelope_OversizedPayload_IsRejectedBeforeEncoding()
        {
            var payload = new byte[17];

            var success = SteamMessageEnvelopeCodec.TryEncode(
                1,
                1,
                payload,
                16,
                out var packet,
                out var error);

            Assert.That(success, Is.False);
            Assert.That(packet, Is.Null);
            Assert.That(error, Does.Contain("limit"));
        }

        [Test]
        public void Membership_Replace_UpdatesOwnerAndReportsDepartedPeers()
        {
            var membership = new SteamLobbyMembership();
            var lobbyId = new SteamLobbyId(100);
            var localId = new SteamPeerId(1);
            var firstRemoteId = new SteamPeerId(2);
            var secondRemoteId = new SteamPeerId(3);
            membership.Replace(
                lobbyId,
                localId,
                new List<SteamPeer>
                {
                    new(localId, "Local", true, true),
                    new(firstRemoteId, "First", false, false)
                });

            var departed = membership.Replace(
                lobbyId,
                secondRemoteId,
                new List<SteamPeer>
                {
                    new(localId, "Local", false, true),
                    new(secondRemoteId, "Second", true, false)
                });

            Assert.That(departed, Is.EqualTo(new[] { firstRemoteId }));
            Assert.That(membership.Contains(firstRemoteId), Is.False);
            Assert.That(membership.Contains(secondRemoteId), Is.True);
            Assert.That(membership.Snapshot.OwnerId, Is.EqualTo(secondRemoteId));
            Assert.That(membership.Snapshot.Members.Count, Is.EqualTo(2));
        }

        [Test]
        public void Membership_Clear_ReturnsEveryPeerAndRemovesSnapshot()
        {
            var membership = new SteamLobbyMembership();
            membership.Replace(
                new SteamLobbyId(100),
                new SteamPeerId(1),
                new List<SteamPeer>
                {
                    new(new SteamPeerId(1), "Local", true, true),
                    new(new SteamPeerId(2), "Remote", false, false)
                });

            var closedPeers = membership.Clear();

            Assert.That(closedPeers, Is.EquivalentTo(new[] { new SteamPeerId(1), new SteamPeerId(2) }));
            Assert.That(membership.HasLobby, Is.False);
            Assert.That(membership.Snapshot, Is.Null);
        }

        private static bool TryDecode(byte[] packet, ushort protocolVersion, int maxPayloadBytes)
        {
            return SteamMessageEnvelopeCodec.TryDecode(
                packet,
                protocolVersion,
                maxPayloadBytes,
                out _,
                out _,
                out _);
        }
    }
}

#endif
