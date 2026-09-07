using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DingoSteamNetworking
{
    public enum SteamNetworkingState
    {
        Uninitialized,
        Ready,
        CreatingLobby,
        JoiningLobby,
        InLobby,
        ShuttingDown,
        Failed
    }

    public enum SteamNetworkingErrorCode
    {
        None,
        NotInitialized,
        AlreadyInitialized,
        WrongState,
        WrongThread,
        SteamInitializationFailed,
        LobbyCreationFailed,
        LobbyJoinFailed,
        LobbyIncompatible,
        LobbyMetadataFailed,
        PeerNotInLobby,
        InvalidPayload,
        ProtocolViolation,
        TransportFailure,
        SendBufferFull
    }

    public readonly struct SteamPeerId : IEquatable<SteamPeerId>
    {
        public readonly ulong Value;

        public bool IsValid => Value != 0;

        public SteamPeerId(ulong value)
        {
            Value = value;
        }

        public static bool TryParse(string value, out SteamPeerId peerId)
        {
            if (ulong.TryParse(value, out var parsed) && parsed != 0)
            {
                peerId = new SteamPeerId(parsed);
                return true;
            }

            peerId = default;
            return false;
        }

        public bool Equals(SteamPeerId other)
        {
            return Value == other.Value;
        }

        public override bool Equals(object obj)
        {
            return obj is SteamPeerId other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Value.GetHashCode();
        }

        public override string ToString()
        {
            return Value.ToString();
        }

        public static bool operator ==(SteamPeerId left, SteamPeerId right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(SteamPeerId left, SteamPeerId right)
        {
            return !left.Equals(right);
        }
    }

    public readonly struct SteamLobbyId : IEquatable<SteamLobbyId>
    {
        public readonly ulong Value;

        public bool IsValid => Value != 0;

        public SteamLobbyId(ulong value)
        {
            Value = value;
        }

        public static bool TryParse(string value, out SteamLobbyId lobbyId)
        {
            if (ulong.TryParse(value, out var parsed) && parsed != 0)
            {
                lobbyId = new SteamLobbyId(parsed);
                return true;
            }

            lobbyId = default;
            return false;
        }

        public bool Equals(SteamLobbyId other)
        {
            return Value == other.Value;
        }

        public override bool Equals(object obj)
        {
            return obj is SteamLobbyId other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Value.GetHashCode();
        }

        public override string ToString()
        {
            return Value.ToString();
        }

        public static bool operator ==(SteamLobbyId left, SteamLobbyId right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(SteamLobbyId left, SteamLobbyId right)
        {
            return !left.Equals(right);
        }
    }

    public class SteamPeer
    {
        public readonly SteamPeerId Id;
        public readonly string DisplayName;
        public readonly bool IsOwner;
        public readonly bool IsLocal;

        public SteamPeer(SteamPeerId id, string displayName, bool isOwner, bool isLocal)
        {
            Id = id;
            DisplayName = displayName ?? string.Empty;
            IsOwner = isOwner;
            IsLocal = isLocal;
        }
    }

    public class SteamLobbySnapshot
    {
        public readonly SteamLobbyId LobbyId;
        public readonly SteamPeerId OwnerId;
        public readonly ReadOnlyCollection<SteamPeer> Members;

        public SteamLobbySnapshot(
            SteamLobbyId lobbyId,
            SteamPeerId ownerId,
            IReadOnlyList<SteamPeer> members)
        {
            LobbyId = lobbyId;
            OwnerId = ownerId;

            var copy = new SteamPeer[members.Count];
            for (var i = 0; i < members.Count; i++)
            {
                copy[i] = members[i];
            }

            Members = Array.AsReadOnly(copy);
        }

        public bool Contains(SteamPeerId peerId)
        {
            for (var i = 0; i < Members.Count; i++)
            {
                if (Members[i].Id == peerId)
                    return true;
            }

            return false;
        }
    }

    public class SteamNetworkingError
    {
        public readonly SteamNetworkingErrorCode Code;
        public readonly string Message;

        public SteamNetworkingError(SteamNetworkingErrorCode code, string message)
        {
            Code = code;
            Message = message ?? string.Empty;
        }

        public override string ToString()
        {
            return $"{Code}: {Message}";
        }
    }

    public class SteamCommandResult
    {
        public readonly bool Accepted;
        public readonly SteamNetworkingError Error;

        private SteamCommandResult(bool accepted, SteamNetworkingError error)
        {
            Accepted = accepted;
            Error = error;
        }

        public static SteamCommandResult Success()
        {
            return new SteamCommandResult(true, null);
        }

        public static SteamCommandResult Rejected(SteamNetworkingErrorCode code, string message)
        {
            return new SteamCommandResult(false, new SteamNetworkingError(code, message));
        }
    }

    public class SteamPeerMessage
    {
        private readonly byte[] _payload;

        public readonly SteamPeerId SenderId;
        public readonly int Channel;
        public readonly byte MessageKind;

        public ReadOnlyMemory<byte> Payload => _payload;

        public SteamPeerMessage(SteamPeerId senderId, byte messageKind, byte[] payload)
            : this(senderId, 0, messageKind, payload)
        {
        }

        public SteamPeerMessage(
            SteamPeerId senderId,
            int channel,
            byte messageKind,
            byte[] payload)
        {
            SenderId = senderId;
            Channel = channel;
            MessageKind = messageKind;
            _payload = new byte[payload.Length];
            Buffer.BlockCopy(payload, 0, _payload, 0, payload.Length);
        }
    }

    public class SteamPeerSendResult
    {
        public readonly SteamPeerId PeerId;
        public readonly bool Success;
        public readonly SteamNetworkingError Error;

        public SteamPeerSendResult(SteamPeerId peerId, bool success, SteamNetworkingError error)
        {
            PeerId = peerId;
            Success = success;
            Error = error;
        }
    }

    public class SteamBroadcastResult
    {
        public readonly ReadOnlyCollection<SteamPeerSendResult> PeerResults;
        public readonly SteamNetworkingError Error;

        public bool Success
        {
            get
            {
                if (Error != null)
                    return false;

                for (var i = 0; i < PeerResults.Count; i++)
                {
                    if (!PeerResults[i].Success)
                        return false;
                }

                return true;
            }
        }

        public SteamBroadcastResult(
            IReadOnlyList<SteamPeerSendResult> peerResults,
            SteamNetworkingError error = null)
        {
            var copy = new SteamPeerSendResult[peerResults.Count];
            for (var i = 0; i < peerResults.Count; i++)
            {
                copy[i] = peerResults[i];
            }

            PeerResults = Array.AsReadOnly(copy);
            Error = error;
        }
    }
}
