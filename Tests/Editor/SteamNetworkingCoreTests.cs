#if UNITY_EDITOR && UNITY_INCLUDE_TESTS

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Steamworks;
using UnityEngine;
using UnityEngine.TestTools;

namespace DingoSteamNetworking.Tests.Editor
{
    public class SteamNetworkingCoreTests
    {
        [Test]
        public void ReceiveBatch_HandlerFailureReleasesEveryMessageAndPreservesLaterDelivery()
        {
            var client = new ScriptedSteamReceiveClient();
            client.SetTestLobby(true);
            client.ChangeState(SteamNetworkingState.InLobby);
            var messages = new[] { new IntPtr(1), new IntPtr(2), new IntPtr(3) };
            client.OnReceive = pointer =>
            {
                if (pointer == new IntPtr(1))
                    throw new InvalidOperationException("scripted receive failure");
            };
            LogAssert.Expect(LogType.Exception, new Regex("scripted receive failure"));

            client.DeliverBatch(messages);

            Assert.That(client.Received, Is.EqualTo(new[] { new IntPtr(1), new IntPtr(2), new IntPtr(3) }));
            Assert.That(client.Released, Is.EqualTo(client.Received));
            Assert.That(messages, Is.EqualTo(new[] { IntPtr.Zero, IntPtr.Zero, IntPtr.Zero }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReceiveBatch_ShutdownCancelsOldDeliveryAndReleasesTheCapturedBatch(bool reenter)
        {
            var client = new ScriptedSteamReceiveClient();
            client.SetTestLobby(true);
            client.ChangeState(SteamNetworkingState.InLobby);
            var messages = new[] { new IntPtr(1), new IntPtr(2), new IntPtr(3) };
            client.OnReceive = _ =>
            {
                client.Shutdown();
                if (reenter)
                {
                    client.SetTestLobby(true);
                    client.ChangeState(SteamNetworkingState.InLobby);
                }
            };

            client.DeliverBatch(messages);

            Assert.That(client.Received, Is.EqualTo(new[] { new IntPtr(1) }));
            Assert.That(client.Released, Is.EqualTo(new[] { new IntPtr(1), new IntPtr(2), new IntPtr(3) }));
            Assert.That(messages, Is.EqualTo(new[] { IntPtr.Zero, IntPtr.Zero, IntPtr.Zero }));
        }

        [Test]
        public void ReceiveBatch_ReceiveAndReleaseFailuresAreBothReportedWithoutAbandoningOwnership()
        {
            var client = new ScriptedSteamReceiveClient();
            client.SetTestLobby(true);
            client.ChangeState(SteamNetworkingState.InLobby);
            var messages = new[] { new IntPtr(1), new IntPtr(2) };
            client.OnReceive = pointer =>
            {
                if (pointer == new IntPtr(1))
                    throw new InvalidOperationException("scripted receive failure");
            };
            client.OnRelease = pointer =>
            {
                if (pointer == new IntPtr(1))
                    throw new InvalidOperationException("scripted release failure");
            };
            LogAssert.Expect(LogType.Exception, new Regex("scripted receive failure"));
            LogAssert.Expect(LogType.Exception, new Regex("scripted release failure"));

            client.DeliverBatch(messages);

            Assert.That(client.Received, Is.EqualTo(new[] { new IntPtr(1), new IntPtr(2) }));
            Assert.That(client.Released, Is.EqualTo(client.Received));
            Assert.That(messages, Is.EqualTo(new[] { IntPtr.Zero, IntPtr.Zero }));
        }

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
        public void ReceivePointerBuffer_MatchesRequestedCapacityAndIsReused()
        {
            var client = new SteamNetworkingClient();
            var clientType = typeof(SteamNetworkingClient);
            var buffersField = clientType.GetField(
                "_receivePointerBuffers",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var getBufferMethod = clientType.GetMethod(
                "GetReceivePointerBuffer",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.That(buffersField, Is.Not.Null);
            Assert.That(getBufferMethod, Is.Not.Null);
            buffersField.SetValue(client, new IntPtr[65][]);

            var fullBuffer = (IntPtr[])getBufferMethod.Invoke(
                client,
                new object[] { 64 });
            var reducedBuffer = (IntPtr[])getBufferMethod.Invoke(
                client,
                new object[] { 61 });
            var reusedReducedBuffer = (IntPtr[])getBufferMethod.Invoke(
                client,
                new object[] { 61 });

            Assert.That(fullBuffer.Length, Is.EqualTo(64));
            Assert.That(reducedBuffer.Length, Is.EqualTo(61));
            Assert.That(reusedReducedBuffer, Is.SameAs(reducedBuffer));
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

        [TestCase(1)]
        [TestCase(2)]
        public void Membership_NameUpdate_PreservesRosterOwnerAndPreviousSnapshot(int changedPeer)
        {
            var membership = new SteamLobbyMembership();
            var owner = new SteamPeerId(1);
            membership.Replace(new SteamLobbyId(100), owner, new[]
            {
                new SteamPeer(owner, "Host", true, true),
                new SteamPeer(new SteamPeerId(2), "Client", false, false),
            });
            var before = membership.Snapshot;
            var peerId = new SteamPeerId((ulong)changedPeer);

            Assert.That(membership.UpdateDisplayName(peerId, "Updated"), Is.True);
            var after = membership.Snapshot;
            Assert.That(after.LobbyId, Is.EqualTo(before.LobbyId));
            Assert.That(after.OwnerId, Is.EqualTo(owner));
            Assert.That(after.Members.Count, Is.EqualTo(2));
            for (var i = 0; i < before.Members.Count; i++)
            {
                Assert.That(after.Members[i].Id, Is.EqualTo(before.Members[i].Id));
                Assert.That(after.Members[i].IsOwner, Is.EqualTo(before.Members[i].IsOwner));
                Assert.That(after.Members[i].IsLocal, Is.EqualTo(before.Members[i].IsLocal));
                Assert.That(membership.Contains(before.Members[i].Id), Is.True);
            }
            Assert.That(after.Members[changedPeer - 1].DisplayName, Is.EqualTo("Updated"));
            Assert.That(before.Members[changedPeer - 1].DisplayName, Is.Not.EqualTo("Updated"));
            Assert.That(membership.UpdateDisplayName(peerId, "Updated"), Is.False);
            Assert.That(membership.UpdateDisplayName(new SteamPeerId(3), "Unknown"), Is.False);
            Assert.That(membership.Snapshot, Is.SameAs(after));
            membership.Clear();
            Assert.That(membership.UpdateDisplayName(peerId, "Late callback"), Is.False);
            Assert.That(membership.Snapshot, Is.Null);
        }

        [TestCase(Steamworks.EPersonaChange.k_EPersonaChangeGoneOffline)]
        [TestCase(Steamworks.EPersonaChange.k_EPersonaChangeStatus)]
        [TestCase(Steamworks.EPersonaChange.k_EPersonaChangeAvatar)]
        public void Client_NonNamePersonaCallback_DoesNotQueryLobbyOrPublishChanges(
            Steamworks.EPersonaChange change)
        {
            var client = new SteamNetworkingClient();
            typeof(SteamNetworkingClient).GetProperty(nameof(SteamNetworkingClient.State))
                .SetValue(client, SteamNetworkingState.InLobby);
            var membership = (SteamLobbyMembership)typeof(SteamNetworkingClient)
                .GetField("_membership", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(client);
            membership.Replace(new SteamLobbyId(100), new SteamPeerId(1), new[]
            {
                new SteamPeer(new SteamPeerId(1), "Host", true, true),
                new SteamPeer(new SteamPeerId(2), "Client", false, false),
            });
            var before = membership.Snapshot;
            var notifications = 0;
            client.LobbyChanged += _ => notifications++;
            typeof(SteamNetworkingClient).GetField("_activeLobbyId",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(client, new Steamworks.CSteamID(100));

            // No native Steam initialization: querying lobby membership here would fail.
            typeof(SteamNetworkingClient).GetMethod("OnPersonaStateChanged",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(client, new object[] { new Steamworks.PersonaStateChange_t
                {
                    m_ulSteamID = 2,
                    m_nChangeFlags = change,
                }});

            Assert.That(notifications, Is.Zero);
            Assert.That(membership.Snapshot, Is.SameAs(before));
            Assert.That(client.State, Is.EqualTo(SteamNetworkingState.InLobby));
        }

        [TestCase(ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_None)]
        [TestCase(ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting)]
        [TestCase(ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_FindingRoute)]
        [TestCase(ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)]
        public void DelayedSessionFailure_AfterRepeatedReentry_DoesNotCloseNewSession(
            ESteamNetworkingConnectionState currentState)
        {
            var client = new ScriptedSteamSessionClient();
            var failures = 0;
            var errors = 0;
            client.PeerSessionFailed += (_, _) => failures++;
            client.ErrorRaised += _ => errors++;
            for (var cycle = 0; cycle < 4; cycle++)
            {
                client.SetTestLobby(false);
                client.ChangeState(SteamNetworkingState.JoiningLobby);
                client.DeliverRequest(2);
                client.SetTestLobby(true);
                client.SessionState = currentState;
                client.ChangeState(SteamNetworkingState.InLobby);
                client.DeliverFailure(2);

                Assert.That(client.Closed, Is.Empty, $"Reconnect {cycle + 1}");
                Assert.That(client.Accepted.Count, Is.EqualTo(cycle + 1));
                Assert.That(client.SessionState, Is.EqualTo(currentState));
                Assert.That(client.State, Is.EqualTo(SteamNetworkingState.InLobby));
            }
            Assert.That(failures, Is.Zero);
            Assert.That(errors, Is.Zero);
        }

        [TestCase(ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer)]
        [TestCase(ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally)]
        public void CurrentSessionFailure_IsClosedAndReportedOnce(ESteamNetworkingConnectionState state)
        {
            var client = new ScriptedSteamSessionClient();
            client.SetTestLobby(true);
            client.ChangeState(SteamNetworkingState.InLobby);
            client.SessionState = state;
            var failures = new List<SteamPeerId>();
            var errors = 0;
            client.PeerSessionFailed += (peer, _) => failures.Add(peer);
            client.ErrorRaised += _ => errors++;
            client.DeliverFailure(2);
            client.DeliverFailure(2);
            Assert.That(client.Closed, Is.EqualTo(new[] { new SteamPeerId(2) }));
            Assert.That(failures, Is.EqualTo(client.Closed));
            Assert.That(errors, Is.EqualTo(1));
        }

        [Test]
        public void SessionRequest_BeforeLobbyEntry_WaitsForConfirmedRoster()
        {
            var client = new ScriptedSteamSessionClient();
            client.ChangeState(SteamNetworkingState.JoiningLobby);
            client.DeliverRequest(2);
            client.DeliverRequest(2);
            client.DeliverRequest(3);
            Assert.That(client.Accepted, Is.Empty);
            Assert.That(client.Closed, Is.Empty);
            client.SetTestLobby(true);
            client.ChangeState(SteamNetworkingState.InLobby);
            Assert.That(client.Accepted, Is.EqualTo(new[] { new SteamPeerId(2) }));
            Assert.That(client.Closed, Is.EqualTo(new[] { new SteamPeerId(3) }));
        }

        [Test]
        public void CancelledLobbyEntry_ClosesDeferredRequestsBeforeNextJoin()
        {
            var client = new ScriptedSteamSessionClient();
            client.ChangeState(SteamNetworkingState.JoiningLobby);
            client.DeliverRequest(2);
            client.ChangeState(SteamNetworkingState.Ready);
            Assert.That(client.Closed, Is.EqualTo(new[] { new SteamPeerId(2) }));
            Assert.That(client.Accepted, Is.Empty);
            client.ChangeState(SteamNetworkingState.JoiningLobby);
            client.SetTestLobby(true);
            client.ChangeState(SteamNetworkingState.InLobby);
            Assert.That(client.Closed.Count, Is.EqualTo(1));
            Assert.That(client.Accepted, Is.Empty);
        }

        [Test]
        public void SessionClose_WaitsForReliableDisconnectDelivery()
        {
            var client = new ScriptedSteamSessionClient { PendingReliable = true };
            client.ScheduleClose(2);
            client.PumpCloses(0);
            Assert.That(client.Closed, Is.Empty);
            client.PendingReliable = false;
            client.PumpCloses(0);
            client.PumpCloses(double.MaxValue);
            Assert.That(client.Closed, Is.EqualTo(new[] { new SteamPeerId(2) }));
        }

        [Test]
        public void SessionClose_StopsWaitingWhenGraceExpires()
        {
            var client = new ScriptedSteamSessionClient { PendingReliable = true };
            client.ScheduleClose(2);
            client.PumpCloses(double.MaxValue);
            Assert.That(client.Closed, Is.EqualTo(new[] { new SteamPeerId(2) }));
        }

        [Test]
        public void SessionClose_DoesNotClosePeerWhoAlreadyRejoined()
        {
            var client = new ScriptedSteamSessionClient { PendingReliable = true };
            client.ScheduleClose(2);
            client.SetTestLobby(true);
            client.ChangeState(SteamNetworkingState.InLobby);
            client.PumpCloses(double.MaxValue);
            client.SetTestLobby(false);
            client.PumpCloses(double.MaxValue);
            Assert.That(client.Closed, Is.Empty);
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

    public class ScriptedSteamReceiveClient : ScriptedSteamSessionClient
    {
        public readonly List<IntPtr> Received = new();
        public readonly List<IntPtr> Released = new();
        public Action<IntPtr> OnReceive;
        public Action<IntPtr> OnRelease;

        public void DeliverBatch(IntPtr[] messages) => ProcessReceivedBatch(0, messages, messages.Length);

        protected override void ReceiveMessage(int channel, IntPtr pointer)
        {
            Received.Add(pointer);
            OnReceive?.Invoke(pointer);
        }

        protected override void ReleaseNativeMessage(IntPtr pointer)
        {
            Released.Add(pointer);
            OnRelease?.Invoke(pointer);
        }
    }

    // Only the native session boundary is scripted; callbacks and entry handling are real.
    public class ScriptedSteamSessionClient : SteamNetworkingClient
    {
        public readonly List<SteamPeerId> Accepted = new();
        public readonly List<SteamPeerId> Closed = new();
        public ESteamNetworkingConnectionState SessionState;
        public bool PendingReliable;

        public void ScheduleClose(ulong peer) => InvokeClient("SchedulePeerSessionClose", new SteamPeerId(peer));
        public void PumpCloses(double now) => InvokeClient("ProcessPendingSessionCloses", now);
        protected override bool HasPendingReliableMessages(SteamPeerId peer) => PendingReliable;

        public void SetTestLobby(bool joined)
        {
            var local = new SteamPeer(new SteamPeerId(1), "Local", false, true);
            typeof(SteamNetworkingClient).GetProperty(nameof(LocalPeer)).SetValue(this, local);
            var membership = (SteamLobbyMembership)typeof(SteamNetworkingClient)
                .GetField("_membership", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(this);
            if (joined)
            {
                membership.Replace(new SteamLobbyId(100), new SteamPeerId(2), new[]
                {
                    local, new SteamPeer(new SteamPeerId(2), "Host", true, false),
                });
            }
            else
            {
                membership.Clear();
            }
        }

        public void ChangeState(SteamNetworkingState state) => InvokeClient("SetState", state);

        public void DeliverRequest(ulong peer)
        {
            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID(new CSteamID(peer));
            InvokeClient("OnSessionRequested", new SteamNetworkingMessagesSessionRequest_t
            {
                m_identityRemote = identity,
            });
        }

        public void DeliverFailure(ulong peer)
        {
            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID(new CSteamID(peer));
            object boxedInfo = new SteamNetConnectionInfo_t
            {
                m_identityRemote = identity,
                m_eState = ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally,
                m_eEndReason = 5003,
            };
            // Native callbacks allocate this fixed UTF-8 buffer during marshalling.
            typeof(SteamNetConnectionInfo_t).GetField("m_szEndDebug_", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(boxedInfo, new byte[Constants.k_cchSteamNetworkingMaxConnectionCloseReason]);
            var info = (SteamNetConnectionInfo_t)boxedInfo;
            info.m_szEndDebug = "Timed out attempting to connect";
            InvokeClient("OnSessionFailed", new SteamNetworkingMessagesSessionFailed_t
            {
                m_info = info,
            });
        }

        protected override ESteamNetworkingConnectionState GetPeerSessionState(SteamPeerId peer) => SessionState;
        protected override void AcceptPeerSession(SteamPeerId peer) => Accepted.Add(peer);
        protected override void ClosePeerSession(SteamPeerId peer)
        {
            Closed.Add(peer);
            SessionState = ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_None;
        }

        private void InvokeClient(string method, object argument)
        {
            typeof(SteamNetworkingClient).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(this, new[] { argument });
        }
    }
}

#endif
