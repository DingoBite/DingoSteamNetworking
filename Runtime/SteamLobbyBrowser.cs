using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DingoSteamNetworking
{
    public class SteamLobbyListing
    {
        public readonly SteamLobbyId LobbyId;
        public readonly string Name;
        public readonly string Status;
        public readonly int MemberCount;
        public readonly int MemberLimit;

        public bool HasSpace => MemberCount < MemberLimit;

        public SteamLobbyListing(SteamLobbyId lobbyId, string name,
            string status, int memberCount, int memberLimit)
        {
            LobbyId = lobbyId;
            Name = name ?? string.Empty;
            Status = status ?? string.Empty;
            MemberCount = memberCount;
            MemberLimit = memberLimit;
        }
    }

    public class SteamLobbyBrowserSnapshot
    {
        public readonly bool IsSearching;
        public readonly bool HasSearched;
        public readonly string ErrorText;
        public readonly ReadOnlyCollection<SteamLobbyListing> Lobbies;

        public SteamLobbyBrowserSnapshot(bool isSearching, bool hasSearched,
            IReadOnlyList<SteamLobbyListing> lobbies, string errorText = "")
        {
            IsSearching = isSearching;
            HasSearched = hasSearched;
            ErrorText = errorText ?? string.Empty;
            var copy = new SteamLobbyListing[lobbies.Count];
            for (var i = 0; i < copy.Length; i++)
            {
                copy[i] = lobbies[i];
            }
            Lobbies = Array.AsReadOnly(copy);
        }
    }
}
