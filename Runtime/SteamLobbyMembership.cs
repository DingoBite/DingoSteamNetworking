using System;
using System.Collections.Generic;

namespace DingoSteamNetworking
{
    public class SteamLobbyMembership
    {
        private readonly Dictionary<SteamPeerId, SteamPeer> _members = new();

        public SteamLobbySnapshot Snapshot { get; private set; }

        public bool HasLobby => Snapshot != null;

        public bool Contains(SteamPeerId peerId)
        {
            return _members.ContainsKey(peerId);
        }

        public SteamPeerId[] Replace(
            SteamLobbyId lobbyId,
            SteamPeerId ownerId,
            IReadOnlyList<SteamPeer> members)
        {
            if (!lobbyId.IsValid)
                throw new ArgumentException("Lobby ID must be valid.", nameof(lobbyId));

            var nextMembers = new Dictionary<SteamPeerId, SteamPeer>(members.Count);
            for (var i = 0; i < members.Count; i++)
            {
                var member = members[i];
                if (!member.Id.IsValid)
                    throw new ArgumentException("Lobby members must have valid peer IDs.", nameof(members));
                if (nextMembers.ContainsKey(member.Id))
                    throw new ArgumentException($"Lobby member {member.Id} is duplicated.", nameof(members));

                nextMembers.Add(member.Id, member);
            }

            var departed = new List<SteamPeerId>();
            foreach (var existing in _members.Keys)
            {
                if (!nextMembers.ContainsKey(existing))
                    departed.Add(existing);
            }

            _members.Clear();
            foreach (var pair in nextMembers)
            {
                _members.Add(pair.Key, pair.Value);
            }

            Snapshot = new SteamLobbySnapshot(lobbyId, ownerId, members);
            return departed.ToArray();
        }

        public SteamPeerId[] Clear()
        {
            var peers = new SteamPeerId[_members.Count];
            var index = 0;
            foreach (var peerId in _members.Keys)
            {
                peers[index] = peerId;
                index++;
            }

            _members.Clear();
            Snapshot = null;
            return peers;
        }
    }
}
