# DingoSteamNetworking

Reusable Unity client layer over Steamworks.NET for Steam identity, friends-only
lobbies and direct peer messages through `ISteamNetworkingMessages`.

## Runtime ownership

Create one `SteamNetworkingClient`, subscribe to its events, call
`Initialize(configuration)` once, call `Tick()` once per Unity frame on the
same thread, and call `Shutdown()` during the owning composition lifecycle.
The module does not create a hidden `GameObject` and does not own product state
or UI.

Steam Lobby is used for membership, owner, metadata and invites. Application
payloads use `SendReliable` or `BroadcastReliable`; lobby chat is not used.
Incoming sessions and packets are accepted only from the active lobby roster.
Requests received while joining wait for the confirmed roster; cancellation
closes them. A session-failure callback is checked against the current native
connection state before it can close a session or notify transport consumers.
This prevents a delayed failure from terminating a later connection to the same
Steam ID. Handshake/control callers may allow Steam's broken-session restart;
established reliable gameplay streams must keep restart disabled.
On lobby departure, native peer closure allows outstanding reliable messages
to drain for up to two seconds. This runs in the existing Tick, without blocking
the UI. New lobby operations cancel these old closures; reappearing members
also invalidate their queued closure. Stop application transports before leaving
the lobby so their final Disconnect can still be sent to its members.

The receive pump owns every native message returned in a batch. A delivery or
release exception is logged without abandoning the remaining messages. Delivery
continues only for the captured lobby generation; shutdown or lobby retirement
stops delivery and releases the remainder of that captured batch. Nested receive
pumps cannot overwrite its owned pointer buffer.

## Dependency

The host Unity project must reference Steamworks.NET commit
`03ffbd8d18d443a084f213615ea84c802e3e8e5d` (release tag `2025.164.0`). The
upstream package manifest at this commit reports version `2025.163.0`; the
commit is the authoritative pin.

## Optional live smoke with App ID 480

The normal EditMode suite does not require Steam. To opt into the `SteamLive`
PlayMode smoke in a host project:

1. Start the Steam desktop client and sign in to an account that can run
   Spacewar.
2. Verify the project-root `steam_appid.txt` contains only `480`.
   Steamworks.NET creates this development file on Editor reload when it is
   absent; keep it out of source control.
3. Create `Library/DingoSteamNetworking/enable-live-smoke` as an empty sentinel.
4. Run the `SteamLive` PlayMode test through Unity Test Runner.
5. Remove `steam_appid.txt` and the sentinel after the check.

Neither file belongs in source control. A two-account peer-message acceptance
requires the host product composition and is intentionally outside this module
smoke.
