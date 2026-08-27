using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Steamworks;

namespace DingoSteamNetworking
{
    public class SteamNetworkingClient : IDisposable
    {
        public const string LOBBY_PRODUCT_KEY = "dingo.product";
        public const string LOBBY_PROTOCOL_KEY = "dingo.protocol";
        public const string LOBBY_BUILD_KEY = "dingo.build";

        private static SteamNetworkingClient _lifecycleOwner;

        private readonly SteamLobbyMembership _membership = new();

        private SteamNetworkingConfiguration _configuration;
        private int _mainThreadId;
        private bool _steamInitialized;
        private CSteamID _activeLobbyId;
        private IntPtr[][] _receivePointerBuffers;
        private int _nextReceiveChannelIndex;

        private Callback<GameLobbyJoinRequested_t> _joinRequestedCallback;
        private Callback<LobbyChatUpdate_t> _lobbyChatUpdateCallback;
        private Callback<LobbyDataUpdate_t> _lobbyDataUpdateCallback;
        private Callback<LobbyKicked_t> _lobbyKickedCallback;
        private Callback<PersonaStateChange_t> _personaStateChangeCallback;
        private Callback<SteamNetworkingMessagesSessionRequest_t> _sessionRequestCallback;
        private Callback<SteamNetworkingMessagesSessionFailed_t> _sessionFailedCallback;
        private CallResult<LobbyCreated_t> _createLobbyCallResult;
        private CallResult<LobbyEnter_t> _joinLobbyCallResult;

        public SteamNetworkingState State { get; private set; } = SteamNetworkingState.Uninitialized;
        public SteamPeer LocalPeer { get; private set; }
        public SteamLobbySnapshot CurrentLobby => _membership.Snapshot;

        public event Action<SteamNetworkingState> StateChanged;
        public event Action<SteamLobbySnapshot> LobbyChanged;
        public event Action<SteamLobbyId> LobbyJoinRequested;
        public event Action<SteamPeerMessage> MessageReceived;
        public event Action<SteamNetworkingError> ErrorRaised;

        public SteamCommandResult Initialize(SteamNetworkingConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));
            if (State != SteamNetworkingState.Uninitialized && State != SteamNetworkingState.Failed)
            {
                return SteamCommandResult.Rejected(
                    SteamNetworkingErrorCode.AlreadyInitialized,
                    "This Steam networking client is already initialized.");
            }
            if (_lifecycleOwner != null && _lifecycleOwner != this)
            {
                return SteamCommandResult.Rejected(
                    SteamNetworkingErrorCode.AlreadyInitialized,
                    "Another Steam networking client owns the Steam API lifecycle.");
            }

            _configuration = configuration;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _lifecycleOwner = this;

            try
            {
                var initResult = SteamAPI.InitEx(out var initMessage);
                if (initResult != ESteamAPIInitResult.k_ESteamAPIInitResult_OK)
                {
                    _lifecycleOwner = null;
                    SetState(SteamNetworkingState.Failed);
                    var error = new SteamNetworkingError(
                        SteamNetworkingErrorCode.SteamInitializationFailed,
                        $"Steam API initialization failed ({initResult}): {initMessage}");
                    ErrorRaised?.Invoke(error);
                    return SteamCommandResult.Rejected(error.Code, error.Message);
                }

                _steamInitialized = true;
                CreateCallbacks();
                _receivePointerBuffers = new IntPtr[
                    _configuration.MaxMessagesPerTick + 1][];
                _receivePointerBuffers[_configuration.MaxMessagesPerTick] =
                    new IntPtr[_configuration.MaxMessagesPerTick];

                var localSteamId = SteamUser.GetSteamID();
                LocalPeer = new SteamPeer(
                    new SteamPeerId((ulong)localSteamId),
                    SteamFriends.GetPersonaName(),
                    false,
                    true);

                SetState(SteamNetworkingState.Ready);
                return SteamCommandResult.Success();
            }
            catch (Exception exception)
            {
                DisposeSteamResources();
                _lifecycleOwner = null;
                SetState(SteamNetworkingState.Failed);
                var error = new SteamNetworkingError(
                    SteamNetworkingErrorCode.SteamInitializationFailed,
                    $"Steam API initialization threw {exception.GetType().Name}: {exception.Message}");
                ErrorRaised?.Invoke(error);
                return SteamCommandResult.Rejected(error.Code, error.Message);
            }
        }

        public void Tick()
        {
            EnsureInitializedOnMainThread();
            SteamAPI.RunCallbacks();
            ReceiveMessages();
        }

        public SteamCommandResult CreateFriendsOnlyLobby()
        {
            var validation = ValidateCommandState(SteamNetworkingState.Ready);
            if (!validation.Accepted)
                return validation;

            SetState(SteamNetworkingState.CreatingLobby);
            var apiCall = SteamMatchmaking.CreateLobby(
                ELobbyType.k_ELobbyTypeFriendsOnly,
                _configuration.LobbyMemberLimit);
            if (apiCall == SteamAPICall_t.Invalid)
            {
                return FailLobbyOperation(
                    SteamNetworkingErrorCode.LobbyCreationFailed,
                    "Steam rejected the create-lobby request before dispatch.");
            }

            _createLobbyCallResult.Set(apiCall);
            return SteamCommandResult.Success();
        }

        public SteamCommandResult JoinLobby(SteamLobbyId lobbyId)
        {
            var validation = ValidateCommandState(SteamNetworkingState.Ready);
            if (!validation.Accepted)
                return validation;
            if (!lobbyId.IsValid)
            {
                return SteamCommandResult.Rejected(
                    SteamNetworkingErrorCode.LobbyJoinFailed,
                    "Lobby ID must be a non-zero Steam ID.");
            }

            SetState(SteamNetworkingState.JoiningLobby);
            var apiCall = SteamMatchmaking.JoinLobby(new CSteamID(lobbyId.Value));
            if (apiCall == SteamAPICall_t.Invalid)
            {
                return FailLobbyOperation(
                    SteamNetworkingErrorCode.LobbyJoinFailed,
                    "Steam rejected the join-lobby request before dispatch.");
            }

            _joinLobbyCallResult.Set(apiCall);
            return SteamCommandResult.Success();
        }

        public SteamCommandResult LeaveLobby()
        {
            var threadValidation = ValidateMainThread();
            if (!threadValidation.Accepted)
                return threadValidation;
            if (!_steamInitialized)
            {
                return SteamCommandResult.Rejected(
                    SteamNetworkingErrorCode.NotInitialized,
                    "Steam networking is not initialized.");
            }

            if (State == SteamNetworkingState.CreatingLobby || State == SteamNetworkingState.JoiningLobby)
            {
                CancelPendingLobbyOperation();
                SetState(SteamNetworkingState.Ready);
                return SteamCommandResult.Success();
            }

            if (State == SteamNetworkingState.Ready)
                return SteamCommandResult.Success();
            if (State != SteamNetworkingState.InLobby)
            {
                return SteamCommandResult.Rejected(
                    SteamNetworkingErrorCode.WrongState,
                    $"Cannot leave a lobby while networking is {State}.");
            }

            ClearActiveLobby(true, true);
            return SteamCommandResult.Success();
        }

        public SteamCommandResult OpenInviteOverlay()
        {
            var validation = ValidateCommandState(SteamNetworkingState.InLobby);
            if (!validation.Accepted)
                return validation;

            SteamFriends.ActivateGameOverlayInviteDialog(_activeLobbyId);
            return SteamCommandResult.Success();
        }

        public SteamPeerSendResult SendReliable(
            SteamPeerId peerId,
            byte messageKind,
            ReadOnlySpan<byte> payload)
        {
            return Send(
                peerId,
                _configuration?.MessageChannel
                ?? SteamNetworkingConfiguration.DEFAULT_MESSAGE_CHANNEL,
                messageKind,
                payload,
                Constants.k_nSteamNetworkingSend_ReliableNoNagle);
        }

        public SteamPeerSendResult SendUnreliableNoDelay(
            SteamPeerId peerId,
            int channel,
            byte messageKind,
            ReadOnlySpan<byte> payload)
        {
            return Send(
                peerId,
                channel,
                messageKind,
                payload,
                Constants.k_nSteamNetworkingSend_UnreliableNoDelay);
        }

        public SteamBroadcastResult BroadcastReliable(byte messageKind, ReadOnlySpan<byte> payload)
        {
            return Broadcast(
                _configuration?.MessageChannel
                ?? SteamNetworkingConfiguration.DEFAULT_MESSAGE_CHANNEL,
                messageKind,
                payload,
                Constants.k_nSteamNetworkingSend_ReliableNoNagle);
        }

        public SteamBroadcastResult BroadcastUnreliableNoDelay(
            int channel,
            byte messageKind,
            ReadOnlySpan<byte> payload)
        {
            return Broadcast(
                channel,
                messageKind,
                payload,
                Constants.k_nSteamNetworkingSend_UnreliableNoDelay);
        }

        private SteamPeerSendResult Send(
            SteamPeerId peerId,
            int channel,
            byte messageKind,
            ReadOnlySpan<byte> payload,
            int sendFlags)
        {
            var validation = ValidateCommandState(SteamNetworkingState.InLobby);
            if (!validation.Accepted)
                return FailedPeerSend(peerId, validation.Error.Code, validation.Error.Message);
            if (!_configuration.HasMessageChannel(channel))
            {
                return FailedPeerSend(
                    peerId,
                    SteamNetworkingErrorCode.InvalidPayload,
                    $"Message channel {channel} is not configured for this client.");
            }
            if (!_membership.Contains(peerId) || peerId == LocalPeer.Id)
            {
                return FailedPeerSend(
                    peerId,
                    SteamNetworkingErrorCode.PeerNotInLobby,
                    $"Peer {peerId} is not a remote member of the active lobby.");
            }
            if (!SteamMessageEnvelopeCodec.TryEncode(
                    _configuration.ProtocolVersion,
                    messageKind,
                    payload,
                    _configuration.MaxPayloadBytes,
                    out var packet,
                    out var envelopeError))
            {
                return FailedPeerSend(
                    peerId,
                    SteamNetworkingErrorCode.InvalidPayload,
                    envelopeError);
            }

            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID(new CSteamID(peerId.Value));
            var pinnedPacket = GCHandle.Alloc(packet, GCHandleType.Pinned);
            try
            {
                var sendResult = SteamNetworkingMessages.SendMessageToUser(
                    ref identity,
                    pinnedPacket.AddrOfPinnedObject(),
                    (uint)packet.Length,
                    sendFlags,
                    channel);

                if (sendResult == EResult.k_EResultNoConnection)
                {
                    sendResult = SteamNetworkingMessages.SendMessageToUser(
                        ref identity,
                        pinnedPacket.AddrOfPinnedObject(),
                        (uint)packet.Length,
                        sendFlags
                        | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession,
                        channel);
                }

                if (sendResult == EResult.k_EResultOK)
                    return new SteamPeerSendResult(peerId, true, null);

                return FailedPeerSend(
                    peerId,
                    SteamNetworkingErrorCode.TransportFailure,
                    $"Steam failed to send to {peerId}: {sendResult}.");
            }
            finally
            {
                pinnedPacket.Free();
            }
        }

        private SteamBroadcastResult Broadcast(
            int channel,
            byte messageKind,
            ReadOnlySpan<byte> payload,
            int sendFlags)
        {
            var validation = ValidateCommandState(SteamNetworkingState.InLobby);
            if (!validation.Accepted)
            {
                return new SteamBroadcastResult(
                    Array.Empty<SteamPeerSendResult>(),
                    validation.Error);
            }
            if (!_configuration.HasMessageChannel(channel))
            {
                return new SteamBroadcastResult(
                    Array.Empty<SteamPeerSendResult>(),
                    new SteamNetworkingError(
                        SteamNetworkingErrorCode.InvalidPayload,
                        $"Message channel {channel} is not configured for this client."));
            }
            if (!SteamMessageEnvelopeCodec.TryEncode(
                    _configuration.ProtocolVersion,
                    messageKind,
                    payload,
                    _configuration.MaxPayloadBytes,
                    out _,
                    out var envelopeError))
            {
                return new SteamBroadcastResult(
                    Array.Empty<SteamPeerSendResult>(),
                    new SteamNetworkingError(
                        SteamNetworkingErrorCode.InvalidPayload,
                        envelopeError));
            }

            var results = new List<SteamPeerSendResult>(CurrentLobby.Members.Count - 1);
            for (var i = 0; i < CurrentLobby.Members.Count; i++)
            {
                var peer = CurrentLobby.Members[i];
                if (!peer.IsLocal)
                {
                    results.Add(Send(
                        peer.Id,
                        channel,
                        messageKind,
                        payload,
                        sendFlags));
                }
            }

            return new SteamBroadcastResult(results);
        }

        public void Shutdown()
        {
            if (!_steamInitialized)
            {
                if (_lifecycleOwner == this)
                    _lifecycleOwner = null;
                SetState(SteamNetworkingState.Uninitialized);
                return;
            }

            EnsureMainThread();
            SetState(SteamNetworkingState.ShuttingDown);
            CancelPendingLobbyOperation();
            ClearActiveLobby(true, false);
            DisposeSteamResources();

            LocalPeer = null;
            _configuration = null;
            _receivePointerBuffers = null;
            _nextReceiveChannelIndex = 0;
            _lifecycleOwner = null;
            SetState(SteamNetworkingState.Uninitialized);
        }

        public void Dispose()
        {
            Shutdown();
        }

        private void CreateCallbacks()
        {
            _joinRequestedCallback = Callback<GameLobbyJoinRequested_t>.Create(OnLobbyJoinRequested);
            _lobbyChatUpdateCallback = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdated);
            _lobbyDataUpdateCallback = Callback<LobbyDataUpdate_t>.Create(OnLobbyDataUpdated);
            _lobbyKickedCallback = Callback<LobbyKicked_t>.Create(OnLobbyKicked);
            _personaStateChangeCallback = Callback<PersonaStateChange_t>.Create(OnPersonaStateChanged);
            _sessionRequestCallback = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequested);
            _sessionFailedCallback = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(OnSessionFailed);
            _createLobbyCallResult = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            _joinLobbyCallResult = CallResult<LobbyEnter_t>.Create(OnLobbyEntered);
        }

        private void OnLobbyJoinRequested(GameLobbyJoinRequested_t callback)
        {
            var lobbyId = new SteamLobbyId((ulong)callback.m_steamIDLobby);
            if (lobbyId.IsValid)
                LobbyJoinRequested?.Invoke(lobbyId);
        }

        private void OnLobbyCreated(LobbyCreated_t callback, bool ioFailure)
        {
            if (State != SteamNetworkingState.CreatingLobby)
                return;
            if (ioFailure || callback.m_eResult != EResult.k_EResultOK || callback.m_ulSteamIDLobby == 0)
            {
                FailLobbyOperation(
                    SteamNetworkingErrorCode.LobbyCreationFailed,
                    $"Steam lobby creation failed: {(ioFailure ? "IO failure" : callback.m_eResult.ToString())}.");
                return;
            }

            var lobbyId = new CSteamID(callback.m_ulSteamIDLobby);
            if (!WriteLobbyMetadata(lobbyId))
            {
                SteamMatchmaking.LeaveLobby(lobbyId);
                FailLobbyOperation(
                    SteamNetworkingErrorCode.LobbyMetadataFailed,
                    "Steam created the lobby but failed to write compatibility metadata.");
                return;
            }

            EnterLobby(lobbyId);
        }

        private void OnLobbyEntered(LobbyEnter_t callback, bool ioFailure)
        {
            if (State != SteamNetworkingState.JoiningLobby)
                return;

            var enterResponse = (EChatRoomEnterResponse)callback.m_EChatRoomEnterResponse;
            if (ioFailure
                || enterResponse != EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess
                || callback.m_ulSteamIDLobby == 0)
            {
                FailLobbyOperation(
                    SteamNetworkingErrorCode.LobbyJoinFailed,
                    $"Steam lobby join failed: {(ioFailure ? "IO failure" : enterResponse.ToString())}.");
                return;
            }

            var lobbyId = new CSteamID(callback.m_ulSteamIDLobby);
            if (!IsLobbyCompatible(lobbyId, out var reason))
            {
                SteamMatchmaking.LeaveLobby(lobbyId);
                FailLobbyOperation(SteamNetworkingErrorCode.LobbyIncompatible, reason);
                return;
            }

            EnterLobby(lobbyId);
        }

        private void OnLobbyChatUpdated(LobbyChatUpdate_t callback)
        {
            if (State == SteamNetworkingState.InLobby && callback.m_ulSteamIDLobby == (ulong)_activeLobbyId)
                RefreshLobbySnapshot();
        }

        private void OnLobbyDataUpdated(LobbyDataUpdate_t callback)
        {
            if (State != SteamNetworkingState.InLobby || callback.m_ulSteamIDLobby != (ulong)_activeLobbyId)
                return;
            if (callback.m_bSuccess == 0)
            {
                ClearActiveLobby(true, true);
                PublishError(
                    SteamNetworkingErrorCode.LobbyIncompatible,
                    "Steam could not refresh lobby metadata.");
                return;
            }

            if (!IsLobbyCompatible(_activeLobbyId, out var reason))
            {
                ClearActiveLobby(true, true);
                PublishError(SteamNetworkingErrorCode.LobbyIncompatible, reason);
            }
        }

        private void OnLobbyKicked(LobbyKicked_t callback)
        {
            if (State != SteamNetworkingState.InLobby || callback.m_ulSteamIDLobby != (ulong)_activeLobbyId)
                return;

            ClearActiveLobby(false, true);
            PublishError(
                SteamNetworkingErrorCode.LobbyJoinFailed,
                $"The local user was removed from lobby {callback.m_ulSteamIDLobby}.");
        }

        private void OnPersonaStateChanged(PersonaStateChange_t callback)
        {
            var peerId = new SteamPeerId(callback.m_ulSteamID);
            if (State == SteamNetworkingState.InLobby && _membership.Contains(peerId))
                RefreshLobbySnapshot();
        }

        private void OnSessionRequested(SteamNetworkingMessagesSessionRequest_t callback)
        {
            var remoteIdentity = callback.m_identityRemote;
            var peerId = new SteamPeerId(remoteIdentity.GetSteamID64());
            if (State == SteamNetworkingState.InLobby
                && peerId != LocalPeer.Id
                && _membership.Contains(peerId))
            {
                SteamNetworkingMessages.AcceptSessionWithUser(ref remoteIdentity);
                return;
            }

            SteamNetworkingMessages.CloseSessionWithUser(ref remoteIdentity);
        }

        private void OnSessionFailed(SteamNetworkingMessagesSessionFailed_t callback)
        {
            var peerId = new SteamPeerId(callback.m_info.m_identityRemote.GetSteamID64());
            if (!_membership.Contains(peerId))
                return;

            ClosePeerSession(peerId);
            PublishError(
                SteamNetworkingErrorCode.TransportFailure,
                $"Steam session with {peerId} failed ({callback.m_info.m_eEndReason}): "
                + callback.m_info.m_szEndDebug);
        }

        private bool WriteLobbyMetadata(CSteamID lobbyId)
        {
            return SteamMatchmaking.SetLobbyData(lobbyId, LOBBY_PRODUCT_KEY, _configuration.ProductId)
                   && SteamMatchmaking.SetLobbyData(
                       lobbyId,
                       LOBBY_PROTOCOL_KEY,
                       _configuration.ProtocolVersionText)
                   && SteamMatchmaking.SetLobbyData(lobbyId, LOBBY_BUILD_KEY, _configuration.BuildId);
        }

        private bool IsLobbyCompatible(CSteamID lobbyId, out string reason)
        {
            var productId = SteamMatchmaking.GetLobbyData(lobbyId, LOBBY_PRODUCT_KEY);
            if (!string.Equals(productId, _configuration.ProductId, StringComparison.Ordinal))
            {
                reason = $"Lobby product '{productId}' does not match '{_configuration.ProductId}'.";
                return false;
            }

            var protocol = SteamMatchmaking.GetLobbyData(lobbyId, LOBBY_PROTOCOL_KEY);
            if (!string.Equals(protocol, _configuration.ProtocolVersionText, StringComparison.Ordinal))
            {
                reason = $"Lobby protocol '{protocol}' does not match '{_configuration.ProtocolVersionText}'.";
                return false;
            }

            var build = SteamMatchmaking.GetLobbyData(lobbyId, LOBBY_BUILD_KEY);
            if (!string.Equals(build, _configuration.BuildId, StringComparison.Ordinal))
            {
                reason = $"Lobby build '{build}' does not match '{_configuration.BuildId}'.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private void EnterLobby(CSteamID lobbyId)
        {
            _activeLobbyId = lobbyId;
            if (!RefreshLobbySnapshot(false))
                return;

            SetState(SteamNetworkingState.InLobby);
            LobbyChanged?.Invoke(_membership.Snapshot);
        }

        private bool RefreshLobbySnapshot(bool publishChanges = true)
        {
            if ((ulong)_activeLobbyId == 0)
                return false;

            var ownerId = new SteamPeerId((ulong)SteamMatchmaking.GetLobbyOwner(_activeLobbyId));
            var memberCount = SteamMatchmaking.GetNumLobbyMembers(_activeLobbyId);
            var members = new List<SteamPeer>(Math.Max(memberCount, 0));
            var localFound = false;
            for (var i = 0; i < memberCount; i++)
            {
                var steamId = SteamMatchmaking.GetLobbyMemberByIndex(_activeLobbyId, i);
                var peerId = new SteamPeerId((ulong)steamId);
                if (!peerId.IsValid)
                    continue;

                var isLocal = peerId == LocalPeer.Id;
                localFound |= isLocal;
                var displayName = isLocal
                    ? SteamFriends.GetPersonaName()
                    : SteamFriends.GetFriendPersonaName(steamId);
                members.Add(new SteamPeer(peerId, displayName, peerId == ownerId, isLocal));
            }

            if (!localFound)
            {
                ClearActiveLobby(true, true);
                PublishError(
                    SteamNetworkingErrorCode.LobbyJoinFailed,
                    "The local user is no longer a member of the active lobby.");
                return false;
            }

            LocalPeer = new SteamPeer(LocalPeer.Id, SteamFriends.GetPersonaName(), LocalPeer.Id == ownerId, true);
            var departedPeers = _membership.Replace(
                new SteamLobbyId((ulong)_activeLobbyId),
                ownerId,
                members);
            for (var i = 0; i < departedPeers.Length; i++)
            {
                if (departedPeers[i] != LocalPeer.Id)
                    ClosePeerSession(departedPeers[i]);
            }

            if (publishChanges)
                LobbyChanged?.Invoke(_membership.Snapshot);

            return true;
        }

        private void ReceiveMessages()
        {
            if (State != SteamNetworkingState.InLobby)
                return;

            var channels = _configuration.MessageChannels;
            var startIndex = _nextReceiveChannelIndex % channels.Count;
            var remaining = _configuration.MaxMessagesPerTick;
            for (var offset = 0; offset < channels.Count && remaining > 0; offset++)
            {
                var channelIndex = (startIndex + offset) % channels.Count;
                var channel = channels[channelIndex];
                var receivePointers = GetReceivePointerBuffer(remaining);
                var receivedCount = SteamNetworkingMessages.ReceiveMessagesOnChannel(
                    channel,
                    receivePointers,
                    receivePointers.Length);
                for (var i = 0; i < receivedCount; i++)
                {
                    var messagePointer = receivePointers[i];
                    try
                    {
                        ReceiveMessage(channel, messagePointer);
                    }
                    finally
                    {
                        SteamNetworkingMessage_t.Release(messagePointer);
                        receivePointers[i] = IntPtr.Zero;
                    }
                }

                remaining -= receivedCount;
            }

            _nextReceiveChannelIndex = (startIndex + 1) % channels.Count;
        }

        private IntPtr[] GetReceivePointerBuffer(int capacity)
        {
            var buffer = _receivePointerBuffers[capacity];
            if (buffer != null)
                return buffer;

            buffer = new IntPtr[capacity];
            _receivePointerBuffers[capacity] = buffer;
            return buffer;
        }

        private void ReceiveMessage(int channel, IntPtr messagePointer)
        {
            var message = SteamNetworkingMessage_t.FromIntPtr(messagePointer);
            var senderId = new SteamPeerId(message.m_identityPeer.GetSteamID64());
            if (senderId == LocalPeer.Id || !_membership.Contains(senderId))
            {
                ClosePeerSession(senderId);
                return;
            }

            var maxPacketSize = SteamMessageEnvelopeCodec.HEADER_SIZE + _configuration.MaxPayloadBytes;
            if (message.m_cbSize < SteamMessageEnvelopeCodec.HEADER_SIZE || message.m_cbSize > maxPacketSize)
            {
                PublishError(
                    SteamNetworkingErrorCode.ProtocolViolation,
                    $"Peer {senderId} sent an invalid packet size: {message.m_cbSize} bytes.");
                return;
            }

            var packet = new byte[message.m_cbSize];
            Marshal.Copy(message.m_pData, packet, 0, packet.Length);
            if (!SteamMessageEnvelopeCodec.TryDecode(
                    packet,
                    _configuration.ProtocolVersion,
                    _configuration.MaxPayloadBytes,
                    out var messageKind,
                    out var payload,
                    out var envelopeError))
            {
                PublishError(
                    SteamNetworkingErrorCode.ProtocolViolation,
                    $"Peer {senderId} sent an invalid envelope: {envelopeError}");
                return;
            }

            MessageReceived?.Invoke(new SteamPeerMessage(
                senderId,
                channel,
                messageKind,
                payload));
        }

        private void ClearActiveLobby(bool notifySteam, bool setReadyState)
        {
            var hadLobby = (ulong)_activeLobbyId != 0 || _membership.HasLobby;
            var lobbyId = _activeLobbyId;
            _activeLobbyId = default;

            var peers = _membership.Clear();
            for (var i = 0; i < peers.Length; i++)
            {
                if (LocalPeer == null || peers[i] != LocalPeer.Id)
                    ClosePeerSession(peers[i]);
            }

            if (notifySteam && (ulong)lobbyId != 0)
                SteamMatchmaking.LeaveLobby(lobbyId);

            if (hadLobby)
                LobbyChanged?.Invoke(null);
            if (setReadyState)
                SetState(SteamNetworkingState.Ready);
        }

        private void ClosePeerSession(SteamPeerId peerId)
        {
            if (!peerId.IsValid || !_steamInitialized)
                return;

            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID(new CSteamID(peerId.Value));
            SteamNetworkingMessages.CloseSessionWithUser(ref identity);
        }

        private void CancelPendingLobbyOperation()
        {
            _createLobbyCallResult?.Cancel();
            _joinLobbyCallResult?.Cancel();
        }

        private SteamCommandResult FailLobbyOperation(SteamNetworkingErrorCode code, string message)
        {
            SetState(SteamNetworkingState.Ready);
            PublishError(code, message);
            return SteamCommandResult.Rejected(code, message);
        }

        private SteamPeerSendResult FailedPeerSend(
            SteamPeerId peerId,
            SteamNetworkingErrorCode code,
            string message)
        {
            var error = new SteamNetworkingError(code, message);
            ErrorRaised?.Invoke(error);
            return new SteamPeerSendResult(peerId, false, error);
        }

        private SteamCommandResult ValidateCommandState(SteamNetworkingState expectedState)
        {
            var threadValidation = ValidateMainThread();
            if (!threadValidation.Accepted)
                return threadValidation;
            if (!_steamInitialized)
            {
                return SteamCommandResult.Rejected(
                    SteamNetworkingErrorCode.NotInitialized,
                    "Steam networking is not initialized.");
            }
            if (State != expectedState)
            {
                return SteamCommandResult.Rejected(
                    SteamNetworkingErrorCode.WrongState,
                    $"Expected networking state {expectedState}, current state is {State}.");
            }

            return SteamCommandResult.Success();
        }

        private SteamCommandResult ValidateMainThread()
        {
            if (_mainThreadId != 0 && Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                return SteamCommandResult.Rejected(
                    SteamNetworkingErrorCode.WrongThread,
                    "Steam networking commands must run on the thread that initialized the client.");
            }

            return SteamCommandResult.Success();
        }

        private void EnsureInitializedOnMainThread()
        {
            if (!_steamInitialized)
                throw new InvalidOperationException("Steam networking is not initialized.");
            EnsureMainThread();
        }

        private void EnsureMainThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                throw new InvalidOperationException(
                    "Steam networking must be pumped and shut down on the thread that initialized it.");
            }
        }

        private void SetState(SteamNetworkingState state)
        {
            if (State == state)
                return;

            State = state;
            StateChanged?.Invoke(State);
        }

        private void PublishError(SteamNetworkingErrorCode code, string message)
        {
            ErrorRaised?.Invoke(new SteamNetworkingError(code, message));
        }

        private void DisposeSteamResources()
        {
            _createLobbyCallResult?.Dispose();
            _joinLobbyCallResult?.Dispose();
            _joinRequestedCallback?.Dispose();
            _lobbyChatUpdateCallback?.Dispose();
            _lobbyDataUpdateCallback?.Dispose();
            _lobbyKickedCallback?.Dispose();
            _personaStateChangeCallback?.Dispose();
            _sessionRequestCallback?.Dispose();
            _sessionFailedCallback?.Dispose();

            _createLobbyCallResult = null;
            _joinLobbyCallResult = null;
            _joinRequestedCallback = null;
            _lobbyChatUpdateCallback = null;
            _lobbyDataUpdateCallback = null;
            _lobbyKickedCallback = null;
            _personaStateChangeCallback = null;
            _sessionRequestCallback = null;
            _sessionFailedCallback = null;

            if (_steamInitialized)
            {
                SteamAPI.Shutdown();
                _steamInitialized = false;
            }
        }
    }
}
