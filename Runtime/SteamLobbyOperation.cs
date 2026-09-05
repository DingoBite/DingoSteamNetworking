namespace DingoSteamNetworking
{
    /// <summary>Separates UI cancellation from completion of an asynchronous Steam call.</summary>
    public class SteamLobbyOperation
    {
        private ulong _version;
        private bool _pending;
        private SteamLobbyId _target;

        public ulong Begin(SteamLobbyId target = default)
        {
            _pending = true;
            _target = target;
            return ++_version;
        }

        public void Cancel()
        {
            _pending = false;
            _target = default;
            _version++;
        }

        public bool Complete(ulong version)
        {
            if (!_pending || version != _version)
            {
                return false;
            }
            _pending = false;
            _target = default;
            return true;
        }

        public bool ShouldLeaveLateLobby(SteamLobbyId entered, SteamLobbyId active)
        {
            return entered.IsValid && entered != active
                   && (!_pending || entered != _target);
        }
    }
}
