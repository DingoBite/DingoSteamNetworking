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
        public const string LOBBY_NAME_KEY = "dingo.name";
        public const string LOBBY_STATUS_KEY = "dingo.status";
        public const string LOBBY_VISIBILITY_KEY = "dingo.visibility";

        private static SteamNetworkingClient _lifecycleOwner;

        private readonly SteamLobbyMembership _membership = new();
        private readonly HashSet<SteamPeerId> _pendingSessionRequests = new();
        private readonly Dictionary<SteamPeerId, double> _pendingSessionCloses = new();
        private const double SESSION_CLOSE_GRACE_SECONDS = 2;

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
        private readonly Dictionary<ulong, IDisposable> _lobbyOperations = new();
        private readonly SteamLobbyOperation _lobbyOperation = new();
        private CallResult<LobbyMatchList_t> _lobbyListCallResult;
        public SteamLobbyBrowserSnapshot LobbyBrowser { get; private set; } =
            new(false, false, Array.Empty<SteamLobbyListing>());
        public event Action<SteamLobbyBrowserSnapshot> LobbyBrowserChanged;

        public SteamNetworkingState State { get; private set; } = SteamNetworkingState.Uninitialized;
        public SteamPeer LocalPeer { get; private set; }
        public SteamLobbySnapshot CurrentLobby => _membership.Snapshot;

        public event Action<SteamNetworkingState> StateChanged;
        public event Action<SteamLobbySnapshot> LobbyChanged;
        public event Action<SteamLobbyId> LobbyJoinRequested;
        public event Action<SteamPeerMessage> MessageReceived;
        public event Action<SteamNetworkingError> ErrorRaised;
        public event Action<SteamPeerId, SteamNetworkingError>
            PeerSessionFailed;

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
            ProcessPendingSessionCloses(UnityEngine.Time.realtimeSinceStartupAsDouble);
        }

        public SteamCommandResult CreateFriendsOnlyLobby() =>
            CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly);

        public SteamCommandResult CreatePublicLobby() =>
            CreateLobby(ELobbyType.k_ELobbyTypePublic);

        private SteamCommandResult CreateLobby(ELobbyType type)
        {
            var validation = ValidateCommandState(SteamNetworkingState.Ready);
            if (!validation.Accepted)
                return validation;

            SetState(SteamNetworkingState.CreatingLobby);
            _pendingSessionCloses.Clear();
            var apiCall = SteamMatchmaking.CreateLobby(
                type,
                _configuration.LobbyMemberLimit);
            if (apiCall == SteamAPICall_t.Invalid)
            {
                return FailLobbyOperation(
                    SteamNetworkingErrorCode.LobbyCreationFailed,
                    "Steam rejected the create-lobby request before dispatch.");
            }

            var operationId = _lobbyOperation.Begin();
            var result = CallResult<LobbyCreated_t>.Create((callback, failed) =>
            {
                CompleteLobbyOperation(operationId);
                if (!_lobbyOperation.Complete(operationId))
                {
                    ReleaseCancelledLobby(callback.m_ulSteamIDLobby);
                    return;
                }
                OnLobbyCreated(callback, failed, type);
            });
            _lobbyOperations.Add(operationId, result);
            result.Set(apiCall);
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
            // A new lobby operation may reuse the same native peer session.
            // Cleanup from the previous entry must not close it after the new join.
            _pendingSessionCloses.Clear();
            UnityEngine.Debug.Log($"[NETTRACE][SteamLobby] Join requested lobby={lobbyId}.");
            var apiCall = SteamMatchmaking.JoinLobby(new CSteamID(lobbyId.Value));
            if (apiCall == SteamAPICall_t.Invalid)
            {
                return FailLobbyOperation(
                    SteamNetworkingErrorCode.LobbyJoinFailed,
                    "Steam rejected the join-lobby request before dispatch.");
            }

            var operationId = _lobbyOperation.Begin(lobbyId);
            var result = CallResult<LobbyEnter_t>.Create((callback, failed) =>
            {
                CompleteLobbyOperation(operationId);
                if (!_lobbyOperation.Complete(operationId))
                {
                    if (!failed && callback.m_EChatRoomEnterResponse ==
                        (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
                    {
                        ReleaseCancelledLobby(callback.m_ulSteamIDLobby);
                    }
                    return;
                }
                if (failed || callback.m_EChatRoomEnterResponse !=
                    (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
                {
                    ReleaseCancelledLobby(lobbyId.Value);
                }
                OnLobbyEntered(callback, failed);
            });
            _lobbyOperations.Add(operationId, result);
            result.Set(apiCall);
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

        private void CompleteLobbyOperation(ulong operationId)
        {
            if (_lobbyOperations.Remove(operationId, out var operation))
            {
                operation.Dispose();
            }
        }

        private void ReleaseCancelledLobby(ulong lobbyId)
        {
            if (_lobbyOperation.ShouldLeaveLateLobby(new SteamLobbyId(lobbyId),
                new SteamLobbyId((ulong)_activeLobbyId)))
            {
                SteamMatchmaking.LeaveLobby(new CSteamID(lobbyId));
            }
        }

        public SteamCommandResult RequestPublicLobbies()
        {
            var validation = ValidateCommandState(SteamNetworkingState.Ready);
            if (!validation.Accepted)
            {
                return validation;
            }
            if (LobbyBrowser.IsSearching)
            {
                return SteamCommandResult.Success();
            }
            SteamMatchmaking.AddRequestLobbyListStringFilter(LOBBY_PRODUCT_KEY,
                _configuration.ProductId, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListStringFilter(LOBBY_PROTOCOL_KEY,
                _configuration.ProtocolVersionText, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListStringFilter(LOBBY_BUILD_KEY,
                _configuration.BuildId, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListStringFilter(LOBBY_VISIBILITY_KEY,
                "public", ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(
                ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            SteamMatchmaking.AddRequestLobbyListResultCountFilter(50);
            PublishLobbyBrowser(new SteamLobbyBrowserSnapshot(true, true, LobbyBrowser.Lobbies));
            var call = SteamMatchmaking.RequestLobbyList();
            if (call == SteamAPICall_t.Invalid)
            {
                PublishLobbyBrowser(new SteamLobbyBrowserSnapshot(false, true,
                    LobbyBrowser.Lobbies, "Steam отклонил поиск. Попробуйте обновить список."));
                return SteamCommandResult.Rejected(SteamNetworkingErrorCode.LobbyJoinFailed,
                    LobbyBrowser.ErrorText);
            }
            _lobbyListCallResult.Set(call);
            return SteamCommandResult.Success();
        }

        public SteamCommandResult SetLobbyAvailability(string status, bool joinable)
        {
            var validation = ValidateCommandState(SteamNetworkingState.InLobby);
            if (!validation.Accepted)
            {
                return validation;
            }
            if (CurrentLobby.OwnerId != LocalPeer.Id
                || !SteamMatchmaking.SetLobbyData(_activeLobbyId, LOBBY_STATUS_KEY, status)
                || !SteamMatchmaking.SetLobbyJoinable(_activeLobbyId, joinable))
            {
                return SteamCommandResult.Rejected(SteamNetworkingErrorCode.LobbyMetadataFailed,
                    "Не удалось обновить доступность lobby.");
            }
            return SteamCommandResult.Success();
        }

        private void OnLobbyList(LobbyMatchList_t callback, bool failed)
        {
            if (failed)
            {
                PublishLobbyBrowser(new SteamLobbyBrowserSnapshot(false, true,
                    LobbyBrowser.Lobbies, "Не удалось получить лобби. Проверьте Steam и обновите список."));
                return;
            }
            var lobbies = new List<SteamLobbyListing>();
            for (var i = 0; i < callback.m_nLobbiesMatching; i++)
            {
                var lobby = SteamMatchmaking.GetLobbyByIndex(i);
                if ((ulong)lobby == 0 || !IsLobbyCompatible(lobby, out _))
                {
                    continue;
                }
                lobbies.Add(new SteamLobbyListing(new SteamLobbyId((ulong)lobby),
                    SteamMatchmaking.GetLobbyData(lobby, LOBBY_NAME_KEY),
                    SteamMatchmaking.GetLobbyData(lobby, LOBBY_STATUS_KEY),
                    SteamMatchmaking.GetNumLobbyMembers(lobby),
                    SteamMatchmaking.GetLobbyMemberLimit(lobby)));
            }
            PublishLobbyBrowser(new SteamLobbyBrowserSnapshot(false, true, lobbies));
        }

        private void PublishLobbyBrowser(SteamLobbyBrowserSnapshot snapshot)
        {
            LobbyBrowser = snapshot;
            LobbyBrowserChanged?.Invoke(snapshot);
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
                Constants.k_nSteamNetworkingSend_ReliableNoNagle,
                allowSessionRestart: true,
                ignoredIsSuccess: false);
        }

        public SteamPeerSendResult SendReliableNoSessionRestart(
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
                Constants.k_nSteamNetworkingSend_ReliableNoNagle,
                allowSessionRestart: false,
                ignoredIsSuccess: false);
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
                Constants.k_nSteamNetworkingSend_UnreliableNoDelay,
                allowSessionRestart: true,
                ignoredIsSuccess: true);
        }

        public SteamPeerSendResult SendUnreliableNoDelayNoSessionRestart(
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
                Constants.k_nSteamNetworkingSend_UnreliableNoDelay,
                allowSessionRestart: false,
                ignoredIsSuccess: true);
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
            int sendFlags,
            bool allowSessionRestart,
            bool ignoredIsSuccess)
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
                    allowSessionRestart
                        ? sendFlags | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession
                        : sendFlags,
                    channel);

                if (sendResult == EResult.k_EResultOK
                    || (ignoredIsSuccess
                        && sendResult == EResult.k_EResultIgnored))
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
                        sendFlags,
                        allowSessionRestart: true,
                        ignoredIsSuccess: sendFlags
                            == Constants.k_nSteamNetworkingSend_UnreliableNoDelay));
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
            _lobbyListCallResult = CallResult<LobbyMatchList_t>.Create(OnLobbyList);
        }

        private void OnLobbyJoinRequested(GameLobbyJoinRequested_t callback)
        {
            var lobbyId = new SteamLobbyId((ulong)callback.m_steamIDLobby);
            if (lobbyId.IsValid)
                LobbyJoinRequested?.Invoke(lobbyId);
        }

        private void OnLobbyCreated(LobbyCreated_t callback, bool ioFailure, ELobbyType type)
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
            if (!WriteLobbyMetadata(lobbyId)
                || !SteamMatchmaking.SetLobbyData(lobbyId, LOBBY_NAME_KEY, LocalPeer.DisplayName)
                || !SteamMatchmaking.SetLobbyData(lobbyId, LOBBY_STATUS_KEY, "lobby")
                || !SteamMatchmaking.SetLobbyData(lobbyId, LOBBY_VISIBILITY_KEY,
                    type == ELobbyType.k_ELobbyTypePublic ? "public" : "friends"))
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
            UnityEngine.Debug.Log($"[NETTRACE][SteamLobby] Join completed lobby={callback.m_ulSteamIDLobby}, response={callback.m_EChatRoomEnterResponse}, ioFailure={ioFailure}, state={State}.");
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
            const EPersonaChange nameChanges = EPersonaChange.k_EPersonaChangeName
                                              | EPersonaChange.k_EPersonaChangeNameFirstSet;
            if (State != SteamNetworkingState.InLobby || !_membership.Contains(peerId)
                || (callback.m_nChangeFlags & nameChanges) == 0)
            {
                return;
            }

            // Friend status/avatar callbacks are not lobby membership notifications.
            // Rescanning the lobby here can remove a connected peer from a transient cache.
            var isLocal = peerId == LocalPeer.Id;
            var displayName = isLocal
                ? SteamFriends.GetPersonaName()
                : SteamFriends.GetFriendPersonaName(new CSteamID(peerId.Value));
            if (_membership.UpdateDisplayName(peerId, displayName))
            {
                if (isLocal)
                {
                    LocalPeer = new SteamPeer(peerId, displayName, LocalPeer.IsOwner, true);
                }
                LobbyChanged?.Invoke(_membership.Snapshot);
            }
        }

        private void OnSessionRequested(SteamNetworkingMessagesSessionRequest_t callback)
        {
            var peerId = new SteamPeerId(callback.m_identityRemote.GetSteamID64());
            if (State == SteamNetworkingState.JoiningLobby)
            {
                // The host can see our membership before our LobbyEnter callback arrives.
                // Decide only after entry succeeds (or is cancelled), using its roster.
                _pendingSessionRequests.Add(peerId);
                UnityEngine.Debug.Log($"[NETTRACE][SteamSessions] Deferring session request from {peerId} until lobby entry completes.");
                return;
            }

            ResolveSessionRequest(peerId);
        }

        private void ResolveSessionRequest(SteamPeerId peerId)
        {
            if (_pendingSessionCloses.ContainsKey(peerId) && State != SteamNetworkingState.InLobby)
            {
                return;
            }
            if (State == SteamNetworkingState.InLobby
                && peerId != LocalPeer.Id
                && _membership.Contains(peerId))
            {
                UnityEngine.Debug.Log($"[NETTRACE][SteamSessions] Accepting session request from lobby member {peerId}.");
                AcceptPeerSession(peerId);
                return;
            }

            UnityEngine.Debug.Log($"[NETTRACE][SteamSessions] Rejecting session request from {peerId}: state={State}, member={_membership.Contains(peerId)}.");
            ClosePeerSession(peerId);
        }

        private void OnSessionFailed(SteamNetworkingMessagesSessionFailed_t callback)
        {
            var peerId = new SteamPeerId(callback.m_info.m_identityRemote.GetSteamID64());
            if (!_membership.Contains(peerId))
                return;

            // This callback may describe a previous connection to the same Steam ID.
            // Never close a new connection, or report the old failure to its transport.
            var state = GetPeerSessionState(peerId);
            if (state != ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer
                && state != ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally)
            {
                UnityEngine.Debug.Log($"[NETTRACE][SteamSessions] Ignoring obsolete failure for {peerId}: current={state}, callbackReason={callback.m_info.m_eEndReason}.");
                return;
            }

            UnityEngine.Debug.Log($"[NETTRACE][SteamSessions] Closing failed session with {peerId}: current={state}, callbackReason={callback.m_info.m_eEndReason}.");
            ClosePeerSession(peerId);
            var error = new SteamNetworkingError(
                SteamNetworkingErrorCode.TransportFailure,
                $"Steam session with {peerId} failed "
                + $"({callback.m_info.m_eEndReason}): "
                + callback.m_info.m_szEndDebug);
            PeerSessionFailed?.Invoke(peerId, error);
            PublishError(error.Code, error.Message);
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
                    SchedulePeerSessionClose(departedPeers[i]);
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
                    SchedulePeerSessionClose(peers[i]);
            }

            if (notifySteam && (ulong)lobbyId != 0)
                SteamMatchmaking.LeaveLobby(lobbyId);

            if (hadLobby)
                LobbyChanged?.Invoke(null);
            if (setReadyState)
                SetState(SteamNetworkingState.Ready);
        }

        private void SchedulePeerSessionClose(SteamPeerId peerId)
        {
            // CloseSessionWithUser discards reliable sends that have not been delivered.
            // Let a final Disconnect drain without blocking the menu or the Steam pump.
            _pendingSessionCloses[peerId] = UnityEngine.Time.realtimeSinceStartupAsDouble
                + SESSION_CLOSE_GRACE_SECONDS;
        }

        private void ProcessPendingSessionCloses(double now)
        {
            if (_pendingSessionCloses.Count == 0)
            {
                return;
            }
            foreach (var peerId in new List<SteamPeerId>(_pendingSessionCloses.Keys))
            {
                if (_membership.Contains(peerId))
                {
                    _pendingSessionCloses.Remove(peerId);
                    continue;
                }
                if (now < _pendingSessionCloses[peerId] && HasPendingReliableMessages(peerId))
                {
                    continue;
                }
                _pendingSessionCloses.Remove(peerId);
                ClosePeerSession(peerId);
            }
        }

        protected virtual bool HasPendingReliableMessages(SteamPeerId peerId)
        {
            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID(new CSteamID(peerId.Value));
            var state = SteamNetworkingMessages.GetSessionConnectionInfo(ref identity, out _, out var status);
            return (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected
                    || state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting
                    || state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_FindingRoute)
                   && (status.m_cbPendingReliable > 0 || status.m_cbSentUnackedReliable > 0);
        }

        protected virtual ESteamNetworkingConnectionState GetPeerSessionState(SteamPeerId peerId)
        {
            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID(new CSteamID(peerId.Value));
            return SteamNetworkingMessages.GetSessionConnectionInfo(ref identity, out _, out _);
        }

        protected virtual void AcceptPeerSession(SteamPeerId peerId)
        {
            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID(new CSteamID(peerId.Value));
            SteamNetworkingMessages.AcceptSessionWithUser(ref identity);
        }

        protected virtual void ClosePeerSession(SteamPeerId peerId)
        {
            _pendingSessionCloses.Remove(peerId);
            if (!peerId.IsValid || !_steamInitialized)
                return;

            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID(new CSteamID(peerId.Value));
            SteamNetworkingMessages.CloseSessionWithUser(ref identity);
        }

        private void CancelPendingLobbyOperation()
        {
            // Cancelling a CallResult does not cancel the Steam operation.
            // Keep each callback alive so a late successful entry can be left.
            _lobbyOperation.Cancel();
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
            if (state != SteamNetworkingState.JoiningLobby && _pendingSessionRequests.Count > 0)
            {
                var peers = new SteamPeerId[_pendingSessionRequests.Count];
                _pendingSessionRequests.CopyTo(peers);
                _pendingSessionRequests.Clear();
                foreach (var peerId in peers)
                {
                    ResolveSessionRequest(peerId);
                }
            }
            StateChanged?.Invoke(State);
        }

        private void PublishError(SteamNetworkingErrorCode code, string message)
        {
            ErrorRaised?.Invoke(new SteamNetworkingError(code, message));
        }

        private void DisposeSteamResources()
        {
            _pendingSessionCloses.Clear();
            foreach (var operation in _lobbyOperations.Values)
            {
                operation.Dispose();
            }
            _lobbyOperations.Clear();
            _lobbyListCallResult?.Dispose();
            _joinRequestedCallback?.Dispose();
            _lobbyChatUpdateCallback?.Dispose();
            _lobbyDataUpdateCallback?.Dispose();
            _lobbyKickedCallback?.Dispose();
            _personaStateChangeCallback?.Dispose();
            _sessionRequestCallback?.Dispose();
            _sessionFailedCallback?.Dispose();

            _lobbyListCallResult = null;
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
