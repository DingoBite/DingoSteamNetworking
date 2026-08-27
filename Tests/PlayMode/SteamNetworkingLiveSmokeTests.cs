#if UNITY_EDITOR && UNITY_INCLUDE_TESTS

using System.Diagnostics;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace DingoSteamNetworking.Tests.PlayMode
{
    public class SteamNetworkingLiveSmokeTests
    {
        private const string ENABLE_FILE = "Library/DingoSteamNetworking/enable-live-smoke";
        private const float LOBBY_TIMEOUT_SECONDS = 20;

        [Test]
        [Category("SteamLive")]
        public void InitializeIdentityCreateAndLeaveLobby()
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (!File.Exists(Path.Combine(projectRoot, ENABLE_FILE)))
            {
                Assert.Ignore(
                    $"Live Steam smoke is disabled. See DingoSteamNetworking README; sentinel: {ENABLE_FILE}.");
            }

            Assert.That(
                File.Exists(Path.Combine(projectRoot, "steam_appid.txt")),
                Is.True,
                "Create a temporary project-root steam_appid.txt containing 480 before running the live smoke.");

            var client = new SteamNetworkingClient();
            try
            {
                var configuration = new SteamNetworkingConfiguration(
                    "politics",
                    1,
                    "phase1-live-smoke",
                    4);
                var inLobbyStateObservedWithSnapshot = false;
                client.StateChanged += state =>
                {
                    if (state == SteamNetworkingState.InLobby)
                        inLobbyStateObservedWithSnapshot = client.CurrentLobby != null;
                };

                var initializeResult = client.Initialize(configuration);
                Assert.That(
                    initializeResult.Accepted,
                    Is.True,
                    initializeResult.Error?.ToString());
                Assert.That(client.LocalPeer.Id.IsValid, Is.True);

                var competingClient = new SteamNetworkingClient();
                var competingInitializeResult = competingClient.Initialize(configuration);
                Assert.That(competingInitializeResult.Accepted, Is.False);
                Assert.That(
                    competingInitializeResult.Error.Code,
                    Is.EqualTo(SteamNetworkingErrorCode.AlreadyInitialized));

                var createResult = client.CreateFriendsOnlyLobby();
                Assert.That(createResult.Accepted, Is.True, createResult.Error?.ToString());

                var stopwatch = Stopwatch.StartNew();
                while (client.State == SteamNetworkingState.CreatingLobby
                       && stopwatch.Elapsed.TotalSeconds < LOBBY_TIMEOUT_SECONDS)
                {
                    client.Tick();
                    Thread.Sleep(10);
                }

                Assert.That(client.State, Is.EqualTo(SteamNetworkingState.InLobby));
                Assert.That(client.CurrentLobby, Is.Not.Null);
                Assert.That(client.CurrentLobby.Contains(client.LocalPeer.Id), Is.True);
                Assert.That(inLobbyStateObservedWithSnapshot, Is.True);

                var leaveResult = client.LeaveLobby();
                Assert.That(leaveResult.Accepted, Is.True, leaveResult.Error?.ToString());
                Assert.That(client.State, Is.EqualTo(SteamNetworkingState.Ready));
                Assert.That(client.CurrentLobby, Is.Null);
            }
            finally
            {
                client.Shutdown();
            }
        }
    }
}

#endif
