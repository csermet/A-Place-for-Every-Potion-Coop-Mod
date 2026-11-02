using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Il2Cpp;
using Il2CppCodeBase.Components.Potions;
using Il2CppCodeBase.Components.Vats;
using Newtonsoft.Json.Linq;
using PotionCoop.Net;
using UnityEngine;

namespace PotionCoop.Game
{
    /// <summary>Session controller: connection, handshake, change tracking, healing and the ghost.</summary>
    internal static class Coop
    {
        const float PlayerSendInterval = 0.05f;
        const float PingInterval = 2f;
        const float DigestInterval = 4f;
        const int ScanChunk = 150;
        const float SettleTimeout = 8f;

        static TcpSession _session;
        static DiscoveryResponder _responder;
        static bool _ready;
        static string _remoteName = "";

        // change tracking
        static readonly HashSet<int> DirtyPotions = new();
        static readonly HashSet<int> DirtyVats = new();
        static readonly Dictionary<int, float> PendingSettle = new();
        static readonly HashSet<int> SentFalling = new();
        static int _tossLogs;
        static readonly int[] FruitlessResyncs = new int[Protocol.BucketCount];
        static HashSet<int> _lastHeld = new();

        // stale-state guard (see StatesMsg.Ack): which of our states messages last carried a change of each vessel
        static int _sendSeq;
        static int _seenSeq;
        static readonly Dictionary<int, int> SentPotionAt = new();
        static readonly Dictionary<int, int> SentVatAt = new();
        static bool _initialWorldPending;
        static int _staleDropped;
        static int _otherLabDropped;

        // world messages wait here while the level is loading
        static readonly List<(string type, JObject body)> Deferred = new();

        // digest scanning
        static PotionFacade[] _scanList;
        static int[] _scanUids;
        static Dictionary<int, int> _scanSlotOf;
        static double _scanCostMs;
        static double _scanWorstFrameMs;
        static float _nextScanReport;
        static int _scanIndex = -1;
        static uint[] _scanAcc;
        static HashSet<int> _scanHeld;
        static float _nextScan;
        static uint[] _myDigest;
        static readonly int[] MismatchStreak = new int[Protocol.BucketCount];
        /// <summary>When a vessel of each bucket last changed by a live state (ours or theirs).</summary>
        static readonly float[] BucketActivity = new float[Protocol.BucketCount];

        static void Touch(StatesMsg msg)
        {
            foreach (var potion in msg.Potions)
                BucketActivity[Protocol.BucketOf(potion.Uid)] = Now;
            if (msg.Vats.Count > 0)
                BucketActivity[0] = Now;
        }
        static bool _initialSyncRequested;
        static float _nextLiquidLog;

        // visible-level safety net (see ScanStep)
        static bool _visualNetOn = true;
        static HashSet<int> _visualSuspects = new();
        static HashSet<int> _visualNext = new();
        static int _visualChecked;
        static int _visualFixed;

        static Ghost _ghost;
        static float _nextPlayerSend;
        static float _nextPing;

        // guest: what the host told us about its lab, and where we are on the way in
        public enum GuestStage { None, WaitingForHost, Choosing, Entering }
        public static GuestStage Stage { get; private set; }
        static string _hostId;
        static NetWorldInfo _hostWorld;
        static NetPersonal _hostPersonal;
        static string[] _hostBuckets;
        // host: the world was sent for the lab we are in
        static bool _worldSentThisLab;

        public static bool IsHost => _session != null && _session.IsHost;
        public static bool IsConnected => _session != null && _session.IsConnected && _ready;
        public static bool IsRunning => _session != null;
        public const string StatusOff = "off";
        public static string Status { get; private set; } = StatusOff;
        public static string RemoteName => _remoteName;
        public static double PingMs { get; private set; } = -1;
        /// <summary>Client: the host's vat capacities (they follow the host's skills, see <see cref="VatCapacityPatch"/>).</summary>
        public static readonly Dictionary<int, int> HostVatCapacity = new();
        /// <summary>Guest: the host's separator rules (see <see cref="SeparatorRulesPatches"/>), null until known.</summary>
        public static NetSeparatorRules HostSeparator { get; private set; }

        /// <summary>Guest: our separator follows the host's.</summary>
        public static bool FollowHostSeparator => HostSeparator != null && IsConnected && !IsHost;

        static NetSeparatorRules ReadSeparatorRules()
        {
            try
            {
                var mediator = GameFlow.Mediator?.Separator;
                if (mediator == null)
                    return null;
                var usable = new bool[16];
                for (var i = 0; i < usable.Length; i++)
                    usable[i] = mediator.IsHolderUsable(i);
                return new NetSeparatorRules
                {
                    Unlocked = mediator.IsUnlocked,
                    Capacity = mediator.Capacity,
                    LoopRanks = mediator.LoopRanksBought,
                    HolderRanks = mediator.HolderRanksBought,
                    HolderUsable = usable,
                };
            }
            catch (Exception e)
            {
                CoopMod.Log.Warning($"separator rules: {e.Message}");
                return null;
            }
        }
        /// <summary>null = unknown, true = worlds match, false = healing.</summary>
        public static bool? InSync { get; private set; }
        public static Ghost Ghost => _ghost;

        static float Now => Time.realtimeSinceStartup;

        // ---------- lifecycle ----------

        public static void StartHost()
        {
            Stop();
            try
            {
                _session = TcpSession.Host(Settings.Port);
                _responder = new DiscoveryResponder(Settings.DiscoveryPort, Settings.Port, Settings.Name);
                Status = $"hosting, waiting for a player (port {Settings.Port})";
                CoopMod.Log.Msg($"hosting on TCP {Settings.Port}, discovery UDP {Settings.DiscoveryPort}");
                // From the main menu, hosting means playing: straight into the lab.
                if (GameFlow.InLobby)
                    MainThread.Post(GameFlow.HostEnterLab);
            }
            catch (Exception e)
            {
                var inUse = e is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse };
                Stop();
                Status = inUse ? "another game on this PC is already hosting" : $"could not start hosting: {e.Message}";
                CoopMod.Log.Warning(Status);
            }
        }

        /// <summary>The player chose to end the session (Co-op window). A guest goes back to the main menu.</summary>
        public static void Leave()
        {
            var guest = IsRunning && !IsHost;
            Stop();
            if (guest)
            {
                Status = "you left the session";
                GuestLeave();
            }
        }

        // ---------- guest: into the host's lab and out again ----------

        /// <summary>Guest in the main menu: on to the host's lab (choosing a profile first the very first time).</summary>
        public static void GuestProceed()
        {
            if (!IsConnected || IsHost || !GameFlow.InLobby)
                return;
            if (_hostWorld == null || !_hostWorld.Valid)
            {
                Stage = GuestStage.WaitingForHost;
                Status = $"waiting for {_remoteName} to enter the lab…";
                return;
            }
            if (Profiles.ActiveHostId != _hostId && !Profiles.Exists(_hostId))
            {
                Stage = GuestStage.Choosing;
                Status = $"first time in {_remoteName}'s lab";
                UI.CoopPanel.Show();
                return;
            }
            // Always through the profile, even when already in it: it takes the host's world as it is now (after a
            // finished lab that is a new one).
            if (!Profiles.Enter(_hostId, _hostWorld, _hostBuckets, null))
                return;
            EnterHostLab();
        }

        /// <summary>Guest's first visit: a new profile, fresh or at the host's level.</summary>
        public static void ChooseProfile(bool copyHostLevel)
        {
            if (Stage != GuestStage.Choosing || !IsConnected)
                return;
            var progress = GameFlow.Data?.Get<Il2CppCodeBase.Services.PersistentDataService.PlayerProgress>();
            if (progress == null)
                return;
            var seed = copyHostLevel && _hostPersonal != null
                ? new NetPersonal
                {
                    Xp = _hostPersonal.Xp,
                    StartingXp = _hostPersonal.StartingXp,
                    Onboarding = progress.OnboardingFlags.Value,
                    Owned = _hostPersonal.Owned,
                    SwitchedOff = _hostPersonal.SwitchedOff,
                }
                : Profiles.Fresh(progress);
            if (Profiles.Enter(_hostId, _hostWorld, _hostBuckets, seed))
                EnterHostLab();
        }

        static void EnterHostLab()
        {
            Stage = GuestStage.Entering;
            Status = $"entering {_remoteName}'s lab…";
            GameFlow.GuestEnterLab();
        }

        /// <summary>Guest lost the session: back to the main menu and the solo profile, with the window saying why.</summary>
        static void GuestLeave()
        {
            Stage = GuestStage.None;
            if (!Profiles.Active)
                return;
            GameFlow.ToMainMenu();
            Profiles.ExitSoon();
            UI.CoopPanel.ShowSoon();
        }

        /// <summary>Every game state change (from <see cref="GameStatePatch"/>).</summary>
        public static void OnStateEntered(eGameState state)
        {
            if (state == eGameState.LoadLevel)
                _worldSentThisLab = false;
            if (state == eGameState.Playing)
            {
                if (IsConnected && IsHost && !_worldSentThisLab)
                    SendWorld();
                if (IsConnected && !IsHost)
                {
                    Stage = GuestStage.None;
                    Status = $"playing in {_remoteName}'s lab";
                }
            }
            // A guest back in the menu without a link leaves the host's profile.
            if (state == eGameState.Lobby && Profiles.Active && !IsConnected)
                Profiles.ExitSoon();
        }

        /// <summary>Host's answer to a guest: who we are, which lab, our own level (if they want to start there) and the world.</summary>
        static WelcomeMsg Welcome()
        {
            var welcome = new WelcomeMsg { Name = Settings.Name, HostId = Settings.HostId, Separator = ReadSeparatorRules() };
            var progress = GameFlow.Data?.Get<Il2CppCodeBase.Services.PersistentDataService.PlayerProgress>();
            if (progress != null)
            {
                welcome.World = Profiles.ReadWorld(progress);
                welcome.HostPersonal = Profiles.ReadPersonal(progress);
                // In the menu the lab may be about to change (host is entering it): the guest waits for SendWorld.
                if (GameRefs.InLevel)
                    welcome.Buckets = Profiles.ReadWorldBuckets();
                else
                    welcome.World.GameStarted = false;
            }
            return welcome;
        }

        static void SendWorld()
        {
            var progress = GameFlow.Data?.Get<Il2CppCodeBase.Services.PersistentDataService.PlayerProgress>();
            if (progress == null)
                return;
            _worldSentThisLab = true;
            _session.Send(new WorldMsg { World = Profiles.ReadWorld(progress), Buckets = Profiles.ReadWorldBuckets() });
        }

        // ---------- pours ----------

        public static bool CanSendPour => IsConnected && !World.Applying && GameRefs.InLevel;

        public static void SendPour(VesselRef from, VesselRef to, eSectionFamily family, int units)
        {
            if (CanSendPour)
                _session.Send(new PourMsg { From = from, To = to, F = (int)family, N = units, L = CurrentLab });
        }

        /// <summary>Joining is done from the main menu, so the shared world is loaded into the co-op save folder.</summary>
        public static string JoinBlocker =>
            IsRunning && IsHost ? "you are hosting (stop hosting first)"
            : !SavePaths.Active && GameRefs.State is not (eGameState.Lobby or eGameState.LoadLobby or eGameState.None)
                ? "go back to the main menu to join"
                : null;

        /// <summary>Join a known host, or (no address) the configured HostAddress, or whoever answers on the LAN.</summary>
        public static void Join(IPAddress address = null, int port = 0)
        {
            var blocker = JoinBlocker;
            if (blocker != null)
            {
                Status = blocker;
                return;
            }
            Stop();
            if (address != null)
            {
                Connect(address, port > 0 ? port : Settings.Port);
                return;
            }
            Status = "looking for a host…";
            var configured = Settings.HostAddress;
            Task.Run(() =>
            {
                IPAddress found;
                var foundPort = Settings.Port;
                if (!string.IsNullOrWhiteSpace(configured) && IPAddress.TryParse(configured.Trim(), out var parsed))
                    found = parsed;
                else if (!DiscoveryResponder.TryFind(Settings.DiscoveryPort, TimeSpan.FromSeconds(4), out found, out foundPort, out _))
                {
                    MainThread.Post(() => Status = "no host found: type the IP shown on the host's screen");
                    return;
                }
                MainThread.Post(() => Connect(found, foundPort));
            });
        }

        static void Connect(IPAddress address, int port)
        {
            Status = $"connecting to {address}…";
            _session = TcpSession.Join(address, port);
        }

        /// <summary>Host: the client claimed something we hold. Send our state of it again (we have the final word).</summary>
        public static void Reassert(int uid)
        {
            if (IsConnected && IsHost)
                DirtyPotions.Add(uid);
        }

        public static void Stop()
        {
            if (_session != null && GameRefs.InLevel && _ghost != null)
                World.DropRemoteHeld();
            _session?.Dispose();
            _session = null;
            _responder?.Dispose();
            _responder = null;
            ResetLink();
            Status = StatusOff;
        }

        static void ResetLink()
        {
            _ready = false;
            _remoteName = "";
            PingMs = -1;
            InSync = null;
            _initialSyncRequested = false;
            Deferred.Clear();
            DirtyPotions.Clear();
            DirtyVats.Clear();
            PendingSettle.Clear();
            SentFalling.Clear();
            HostVatCapacity.Clear();
            HostSeparator = null;
            _sendSeq = 0;
            _seenSeq = 0;
            SentPotionAt.Clear();
            SentVatAt.Clear();
            _initialWorldPending = false;
            Array.Clear(FruitlessResyncs, 0, FruitlessResyncs.Length);
            Array.Clear(MismatchStreak, 0, MismatchStreak.Length);
            Array.Clear(BucketActivity, 0, BucketActivity.Length);
            _ghost?.Destroy();
            _ghost = null;
            CatSync.Release();
        }

        // ---------- per-frame ----------

        public static void Update()
        {
            MainThread.Drain();
            Profiles.Tick();
            if (_session != null)
            {
                while (_session != null && _session.Poll(out var frame))
                {
                    try { Handle(frame); }
                    catch (Exception e) { CoopMod.Log.Error($"message failed: {e}"); }
                }
            }

            Liquid.Tick();

            if (!IsConnected)
            {
                // Bottles still on their way (e.g. put down when the other player left) finish their flight.
                if (GameRefs.InLevel)
                    World.TickMotion(null, Time.unscaledDeltaTime);
                return;
            }

            if (GameRefs.InLevel)
            {
                if (Deferred.Count > 0)
                {
                    var queued = Deferred.ToList();
                    Deferred.Clear();
                    foreach (var (type, body) in queued)
                        HandleWorld(type, body);
                }

                if (!IsHost && !_initialSyncRequested)
                {
                    _initialSyncRequested = true;
                    _initialWorldPending = true;
                    _session.Send(new NeedMsg { Buckets = Enumerable.Range(0, Protocol.BucketCount).ToArray() });
                    CoopMod.Log.Msg("asked host for the full world");
                }

                ScanStep();
                _ghost?.Tick(Time.unscaledDeltaTime);
                World.TickMotion(_ghost, Time.unscaledDeltaTime);
                Pours.Tick();
                if (IsHost)
                    CatSync.HostTick(_session, Now);
                else
                    CatSync.ClientTick(Time.unscaledDeltaTime);
                World.TickDiagnostics();
            }
            else if (_ghost != null && !GameRefs.InLevel)
            {
                _ghost.Destroy();
                _ghost = null;
            }

            if (Now >= _nextPing)
            {
                _nextPing = Now + PingInterval;
                _session.Send(new PingMsg { Sent = Now });
            }
        }

        public static void LateUpdate()
        {
            if (!IsConnected || !GameRefs.InLevel)
                return;
            FlushChanges();
            SendPlayer();
        }

        // ---------- incoming ----------

        static void Handle(string frame)
        {
            if (frame == "\u0000connected")
            {
                _remoteName = "";
                Status = IsHost ? "a player connected, saying hello…" : "connected, saying hello…";
                if (!IsHost)
                    _session.Send(new HelloMsg { Mod = Protocol.ModVersion, Game = Application.version, Name = Settings.Name });
                return;
            }
            if (frame == TcpSession.Disconnected)
            {
                var who = string.IsNullOrEmpty(_remoteName) ? "the other player" : _remoteName;
                if (GameRefs.InLevel && _ghost != null)
                    World.DropRemoteHeld();
                var wasReady = _ready;
                ResetLink();
                if (IsHost)
                    Status = wasReady ? $"{who} left; waiting for a player" : $"hosting, waiting for a player (port {Settings.Port})";
                else
                {
                    Status = wasReady ? $"{who} left the game or the connection was lost" : "could not connect (IP, port or firewall?)";
                    _session?.Dispose();
                    _session = null;
                    GuestLeave();
                }
                CoopMod.Log.Msg($"link closed: {Status}");
                return;
            }

            (string type, JObject body) msg;
            try { msg = Protocol.Decode(frame); }
            catch (Exception e)
            {
                CoopMod.Log.Warning($"bad frame: {e.Message}");
                return;
            }

            switch (msg.type)
            {
                case "hello" when IsHost:
                    var hello = msg.body.ToObject<HelloMsg>();
                    if (hello.Mod != Protocol.ModVersion || hello.Game != Application.version)
                    {
                        _session.Send(new RejectMsg
                        {
                            Reason = $"different versions (you: mod {hello.Mod}, game {hello.Game}; host: mod {Protocol.ModVersion}, game {Application.version}). " +
                                     "Use the same game folder on both PCs.",
                        });
                        Status = $"{hello.Name} turned away: different versions";
                        return;
                    }
                    _remoteName = hello.Name;
                    _ready = true;
                    _session.Send(Welcome());
                    _worldSentThisLab = GameRefs.InLevel;
                    Status = $"{_remoteName} joined";
                    CoopMod.Log.Msg($"{_remoteName} joined from {_session.RemoteAddress}");
                    return;

                case "welcome" when !IsHost:
                    var welcome = msg.body.ToObject<WelcomeMsg>();
                    _remoteName = welcome.Name;
                    _hostId = welcome.HostId;
                    _hostWorld = welcome.World;
                    _hostPersonal = welcome.HostPersonal;
                    _hostBuckets = welcome.Buckets;
                    HostSeparator = welcome.Separator;
                    _ready = true;
                    Status = $"connected to {_remoteName}";
                    CoopMod.Log.Msg($"joined {_remoteName}'s world (host {_hostId}, lab {_hostWorld?.LabManifestHash})");
                    GuestProceed();
                    return;

                case "world" when !IsHost:
                    var world = msg.body.ToObject<WorldMsg>();
                    _hostWorld = world.World;
                    _hostBuckets = world.Buckets;
                    // Waiting for the host, or back in the menu with them (a finished lab): follow them in.
                    if (Stage == GuestStage.WaitingForHost || (Stage == GuestStage.None && Profiles.Active && GameFlow.InLobby))
                        GuestProceed();
                    return;

                case "reject":
                    Status = "turned away: " + msg.body.ToObject<RejectMsg>().Reason;
                    CoopMod.Log.Warning(Status);
                    _session?.Dispose();
                    _session = null;
                    return;

                case "ping":
                    _session.Send(new PongMsg { Sent = msg.body.ToObject<PingMsg>().Sent });
                    return;

                case "pong":
                    PingMs = (Now - msg.body.ToObject<PongMsg>().Sent) * 1000.0;
                    return;
            }

            if (!_ready)
                return;

            if (msg.type == "cat")
            {
                if (GameRefs.InLevel && !IsHost)
                    CatSync.OnCat(msg.body.ToObject<CatMsg>());
                return;
            }

            if (msg.type == "p")
            {
                if (GameRefs.InLevel)
                    OnPlayer(msg.body.ToObject<PlayerMsg>());
                return;
            }

            if (msg.type == "pour")
            {
                // Only worth playing while it happens; the final states follow anyway.
                var pour = msg.body.ToObject<PourMsg>();
                if (GameRefs.InLevel && (pour.L == 0 || pour.L == CurrentLab))
                    Pours.Play(pour);
                return;
            }

            if (GameRefs.InLevel)
                HandleWorld(msg.type, msg.body);
            else
                Deferred.Add(msg);
        }

        static void HandleWorld(string type, JObject body)
        {
            switch (type)
            {
                case "s":
                    var states = body.ToObject<StatesMsg>();
                    var lab = CurrentLab;
                    if (states.Lab != 0 && lab != 0 && states.Lab != lab)
                    {
                        // Sent from another lab (one of us has just moved on): its bottle numbers mean other bottles here.
                        _seenSeq = Math.Max(_seenSeq, states.Seq);
                        if (_otherLabDropped++ < 5)
                            CoopMod.Log.Msg($"ignored {states.Potions.Count} state(s) from another lab ({states.Lab}, ours {lab})");
                        break;
                    }
                    if (!DropStale(states))
                        break;
                    if (!states.Resync)
                        Touch(states);
                    var changed = World.Apply(states.Potions, states.Vats, animate: !states.Resync, fixHeld: states.Fix && !IsHost);
                    if (states.Resync)
                        CheckResyncProgress(states, changed);
                    if (states.Resync || changed > 0)
                        CoopMod.Log.Msg($"applied {states.Potions.Count} vessel(s), {states.Vats.Count} vat(s): {changed} changed{(states.Resync ? " [resync]" : "")}");
                    break;

                case "need" when IsHost:
                    SendBuckets(body.ToObject<NeedMsg>().Buckets);
                    break;

                case "sep" when IsHost:
                    HostSeparatorPour(body.ToObject<SeparatorPourMsg>());
                    break;

                case "d" when !IsHost:
                    var digest = body.ToObject<DigestMsg>();
                    if (digest.L != 0 && CurrentLab != 0 && digest.L != CurrentLab)
                        break;
                    if (digest.VatCapacity != null)
                        foreach (var pair in digest.VatCapacity)
                            HostVatCapacity[pair.Key] = pair.Value;
                    if (digest.Separator != null)
                        HostSeparator = digest.Separator;
                    CompareDigest(digest.Buckets);
                    break;
            }
        }

        static void OnPlayer(PlayerMsg msg)
        {
            var pos = new Vector3(msg.Pos[0], msg.Pos[1], msg.Pos[2]);
            if (_ghost == null || !_ghost.Alive)
                _ghost = new Ghost(Settings.RemoteColor, pos);
            _ghost.Name = _remoteName;
            _ghost.InMenu = msg.InMenu;
            _ghost.Held = msg.Held;
            _ghost.SetTarget(pos, msg.Yaw, msg.Pitch);
        }

        // ---------- outgoing ----------

        static void SendPlayer()
        {
            if (Now < _nextPlayerSend)
                return;
            _nextPlayerSend = Now + PlayerSendInterval;
            var hero = GameRefs.Hero;
            if (hero == null)
                return;
            var pos = hero.Position;
            var look = hero.Look;
            var populator = GameRefs.Populator;
            _session.Send(new PlayerMsg
            {
                Pos = new[] { pos.x, pos.y, pos.z },
                Yaw = look != null ? look._yaw : 0f,
                Pitch = look != null ? look._pitch : 0f,
                InMenu = GameRefs.State != eGameState.Playing,
                Held = populator != null ? populator.HeldPotions().Count + populator.ApronPotions().Count : 0,
            });
        }

        /// <summary>Hand or apron row changed: everything in it now, and everything that just left it, is dirty.</summary>
        public static void MarkHeldRows()
        {
            if (!ShouldTrack())
                return;
            var held = World.LocalHeld(GameRefs.Populator);
            DirtyPotions.UnionWith(held);
            DirtyPotions.UnionWith(_lastHeld);
            _lastHeld = held;
        }

        public static void MarkPotion(PotionFacade potion)
        {
            if (ShouldTrack() && potion != null)
                DirtyPotions.Add(potion.Uid);
        }

        public static void MarkVat(VatFacade vat)
        {
            if (ShouldTrack() && vat != null)
                DirtyVats.Add(vat.Uid);
        }

        /// <summary>
        /// The separator changed: its loop, and the bottles hanging on it (it fills and drains them). Only the host's
        /// separator runs (see <see cref="SeparatorAuthorityPatches"/>), so only the host reports it.
        /// </summary>
        public static void MarkSeparator(Il2CppCodeBase.Components.EssenceSeparator.EssenceSeparatorFacade separator)
        {
            if (!ShouldTrack() || separator == null || !IsHost)
                return;
            DirtyVats.Add(separator.Uid);
            var holders = separator._holders;
            for (var i = 0; holders != null && i < holders.Length; i++)
            {
                var vessel = holders[i] != null ? holders[i]._vessel : null;
                if (vessel != null)
                    DirtyPotions.Add(vessel.Uid);
            }
        }

        /// <summary>Guest: a pour out of a separator holder, for the host's separator to do.</summary>
        public static void SendSeparatorPour(Il2CppCodeBase.Components.EssenceSeparator.EssenceSeparatorFacade separator,
            Il2CppCodeBase.Components.EssenceSeparator.PourOutHolderFacade holder)
        {
            if (IsConnected && !IsHost)
                _session.Send(new SeparatorPourMsg { U = separator.Uid, H = holder.Index });
        }

        static void HostSeparatorPour(SeparatorPourMsg msg)
        {
            var separator = World.Separators().FirstOrDefault(s => s.Uid == msg.U);
            if (separator == null || !separator.TryGetHolder(msg.H, out var holder) || holder == null || !separator.CanPourOutOf(holder))
            {
                CoopMod.Log.Msg($"guest's separator pour at holder {msg.H}: nothing to pour here");
                return;
            }
            var poured = separator.PourOutOf(holder, false);
            MarkSeparator(separator);
            if (Settings.Verbose)
                CoopMod.Log.Msg($"guest's separator pour at holder {msg.H}: {poured} unit(s)");
        }

        static bool ShouldTrack() => IsConnected && !World.Applying && GameRefs.InLevel;

        static void FlushChanges()
        {
            if (DirtyPotions.Count == 0 && DirtyVats.Count == 0 && PendingSettle.Count == 0)
                return;

            var populator = GameRefs.Populator;
            var held = World.LocalHeld(populator);
            var slots = World.SlotMap(populator);
            var msg = new StatesMsg();

            foreach (var uid in DirtyPotions)
                if (!PendingSettle.ContainsKey(uid))
                    PendingSettle[uid] = Now;
            DirtyPotions.Clear();

            foreach (var uid in PendingSettle.Keys.ToList())
            {
                if (!populator.TryGetPotion(uid, out var potion) || potion == null)
                {
                    PendingSettle.Remove(uid);
                    continue;
                }
                var state = World.Read(populator, potion, held, slotOf: slots);
                if (state.Location == NetLocation.Floor && potion.CarryState.Value != ePotionCarryState.Idle
                    && Now - PendingSettle[uid] < SettleTimeout)
                {
                    // Just dropped and still falling: tell the other side right away (it lets the bottle fall too),
                    // then keep watching and send where it finally comes to rest.
                    if (SentFalling.Add(uid))
                    {
                        state.Falling = true;
                        // A thrown bottle (R) flies to the point the player aimed at; the game knows it already.
                        if (potion.DropStyle == eDropStyle.BallisticToss)
                        {
                            var target = potion.DropTargetPosition;
                            if (target != Vector3.zero && (target - potion.transform.position).sqrMagnitude < 400f)
                            {
                                var rot = potion._carrier != null ? potion._carrier.DropTargetRotation : potion.transform.rotation;
                                state.Pos = new[] { target.x, target.y, target.z };
                                state.Rot = new[] { rot.x, rot.y, rot.z, rot.w };
                                state.Toss = true;
                            }
                        }
                        if (_tossLogs < 10)
                        {
                            _tossLogs++;
                            CoopMod.Log.Msg($"[diag] drop #{uid} style={potion.DropStyle} from={potion.transform.position} " +
                                            $"target={potion.DropTargetPosition} toss={state.Toss}");
                        }
                        msg.Potions.Add(state);
                    }
                    continue;
                }
                PendingSettle.Remove(uid);
                SentFalling.Remove(uid);
                msg.Potions.Add(state);
            }

            if (DirtyVats.Count > 0)
            {
                foreach (var vat in World.Vats())
                    if (DirtyVats.Contains(vat.Uid))
                        msg.Vats.Add(World.ReadVat(vat));
                foreach (var separator in World.Separators())
                    if (DirtyVats.Contains(separator.Uid))
                        msg.Vats.Add(World.ReadSeparator(separator));
                DirtyVats.Clear();
            }

            if (msg.Potions.Count > 0 || msg.Vats.Count > 0)
            {
                Stamp(msg, localChanges: true);
                _session.Send(msg);
                if (Settings.Verbose)
                    CoopMod.Log.Msg($"sent {string.Join(", ", msg.Potions.Select(p => $"#{p.Uid}:{p.Location}{(p.Place != 0 ? "@" + p.Place : "")}"))} vats={msg.Vats.Count}");
            }
        }

        static void SendBuckets(int[] buckets)
        {
            var populator = GameRefs.Populator;
            var held = World.LocalHeld(populator);
            var wanted = new HashSet<int>(buckets);
            var slots = World.SlotMap(populator);
            var msg = new StatesMsg { Resync = true };
            foreach (var entry in populator._potionsByUid)
                if (wanted.Contains(Protocol.BucketOf(entry.Key)))
                    msg.Potions.Add(World.Read(populator, entry.Value, held, slotOf: slots));
            if (wanted.Contains(0))
            {
                msg.Vats.AddRange(World.Vats().Select(World.ReadVat));
                msg.Vats.AddRange(World.Separators().Select(World.ReadSeparator));
            }
            Stamp(msg, localChanges: false);
            _session.Send(msg);
            CoopMod.Log.Msg($"sent {msg.Potions.Count} vessel(s) for bucket(s) {string.Join(",", buckets)}");
        }

        // ---------- stale states ----------
        //
        // Both players change the world, and a states message can cross one of ours on the wire: the other side wrote
        // it before it had our latest change of a vessel (typically a resync answer arriving just after we poured).
        // Applying it would put back the old contents of that one vessel while its partner in the pour keeps the new:
        // liquid lost or doubled. So every states message carries its number and the number of the last of ours the
        // sender had applied; a vessel we changed after that keeps our state, which is on its way to them.

        static int CurrentLab => GameFlow.Data?.Get<Il2CppCodeBase.Services.PersistentDataService.PlayerProgress>()?.LabManifestHash ?? 0;

        static void Stamp(StatesMsg msg, bool localChanges)
        {
            msg.Seq = ++_sendSeq;
            msg.Ack = _seenSeq;
            msg.Lab = CurrentLab;
            if (!localChanges)
                return;
            Touch(msg);
            foreach (var potion in msg.Potions)
                SentPotionAt[potion.Uid] = msg.Seq;
            foreach (var vat in msg.Vats)
                SentVatAt[vat.Uid] = msg.Seq;
        }

        /// <returns>False when the whole message is turned down (host, see below).</returns>
        static bool DropStale(StatesMsg states)
        {
            _seenSeq = Math.Max(_seenSeq, states.Seq);
            // The guest's first full world: the host's word on everything (as before), nothing to guard yet.
            if (states.Resync && _initialWorldPending && !IsHost)
            {
                _initialWorldPending = false;
                SentPotionAt.Clear();
                SentVatAt.Clear();
                return true;
            }
            var ack = states.Ack;
            bool Stale(Dictionary<int, int> sentAt, int uid) => sentAt.TryGetValue(uid, out var at) && at > ack;
            var stale = states.Potions.Count(p => Stale(SentPotionAt, p.Uid)) + states.Vats.Count(v => Stale(SentVatAt, v.Uid));
            // What the other side has applied needs no guarding any more.
            Forget(SentPotionAt, ack);
            Forget(SentVatAt, ack);
            if (stale == 0)
                return true;

            if (IsHost)
            {
                // A guest message about a vessel we changed meanwhile: both of us acted on it at the same instant
                // (e.g. drawing the last unit of a vat). Its changes go together (a pour's two ends travel in one
                // message), so take none of them: our version of every vessel in it stands and goes back to the guest.
                SendOurs(states);
                if (_staleDropped++ < 50)
                    CoopMod.Log.Msg($"the guest acted on {stale} vessel(s) we had just changed: ours stand ({states.Potions.Count + states.Vats.Count} sent back)");
                states.Potions.Clear();
                states.Vats.Clear();
                return false;
            }

            // Guest: our newer change of these is on its way to the host (who has the final word and answers if it
            // turns it down); the rest of the message applies.
            states.Potions.RemoveAll(p => Stale(SentPotionAt, p.Uid));
            states.Vats.RemoveAll(v => Stale(SentVatAt, v.Uid));
            if (_staleDropped++ < 50)
                CoopMod.Log.Msg($"kept our newer state of {stale} vessel(s) over a crossing message");
            return true;
        }

        /// <summary>Host: our state of every vessel in a guest message we turned down, to be taken as it is.</summary>
        static void SendOurs(StatesMsg turnedDown)
        {
            var populator = GameRefs.Populator;
            var held = World.LocalHeld(populator);
            var slots = World.SlotMap(populator);
            var answer = new StatesMsg { Fix = true };
            foreach (var state in turnedDown.Potions)
                if (populator.TryGetPotion(state.Uid, out var potion) && potion != null)
                    answer.Potions.Add(World.Read(populator, potion, held, slotOf: slots));
            foreach (var state in turnedDown.Vats)
            {
                var vat = World.FindVat(state.Uid);
                if (vat != null)
                    answer.Vats.Add(World.ReadVat(vat));
                else if (World.Separators().FirstOrDefault(s => s.Uid == state.Uid) is { } separator)
                    answer.Vats.Add(World.ReadSeparator(separator));
            }
            Stamp(answer, localChanges: true);
            _session.Send(answer);
        }

        static void Forget(Dictionary<int, int> sentAt, int ack)
        {
            if (sentAt.Count == 0)
                return;
            List<int> done = null;
            foreach (var pair in sentAt)
                if (pair.Value <= ack)
                    (done ??= new List<int>()).Add(pair.Key);
            if (done != null)
                foreach (var uid in done)
                    sentAt.Remove(uid);
        }

        // ---------- digest healing ----------

        /// <summary>Hashes the whole world a few hundred vessels per frame; the host sends the result, the client compares.</summary>
        static void ScanStep()
        {
            var populator = GameRefs.Populator;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (_scanIndex < 0)
            {
                if (Now < _nextScan)
                    return;
                if (_scanList == null)
                {
                    var potions = new List<PotionFacade>(populator.PotionCount);
                    var uids = new List<int>(populator.PotionCount);
                    foreach (var entry in populator._potionsByUid)
                    {
                        potions.Add(entry.Value);
                        uids.Add(entry.Key);
                    }
                    _scanList = potions.ToArray();
                    _scanUids = uids.ToArray();
                }
                _scanSlotOf = World.SlotMap(populator, report: true);
                _scanHeld = World.LocalHeld(populator);
                _scanAcc = new uint[Protocol.BucketCount];
                foreach (var vat in World.Vats())
                    _scanAcc[0] += World.Hash(World.ReadVat(vat));
                foreach (var separator in World.Separators())
                    _scanAcc[0] += World.Hash(World.ReadSeparator(separator));
                _scanIndex = 0;
                _scanCostMs = 0;
                _scanWorstFrameMs = 0;
            }

            var self = World.HeldBySelf;
            var other = World.HeldByOther;
            var end = Math.Min(_scanIndex + ScanChunk, _scanList.Length);
            for (; _scanIndex < end; _scanIndex++)
            {
                var potion = _scanList[_scanIndex];
                if (potion == null)
                    continue;
                var uid = _scanUids[_scanIndex];
                var place = 0;
                NetLocation location;
                if (_scanHeld.Contains(uid))
                    location = self;
                else if (World.RemoteHeld.Contains(uid))
                    location = other;
                else if (_scanSlotOf.TryGetValue(uid, out place))
                    location = NetLocation.Slot;
                else if (potion.IsHanging && potion.HangingHolder != null)
                {
                    // Same rule as World.Read, so the digest and the states sent agree.
                    location = NetLocation.Holder;
                    place = potion.HangingHolder.Index;
                }
                else
                    location = NetLocation.Floor;
                _scanAcc[Protocol.BucketOf(uid)] += World.Hash(uid, location, place, potion.Contents._units);

                // Safety net: a resting bottle whose drawn level disagrees with its contents two scans in a row
                // (seconds apart, so not a pour animation in progress) gets repainted.
                if (_visualNetOn && location is NetLocation.Floor or NetLocation.Slot or NetLocation.Holder && World.LooksWrong(potion))
                {
                    _visualChecked++;
                    if (_visualSuspects.Contains(uid))
                    {
                        if (World.FixVisual(potion))
                            _visualFixed++;
                    }
                    else
                        _visualNext.Add(uid);
                }
            }

            var frameMs = watch.Elapsed.TotalMilliseconds;
            _scanCostMs += frameMs;
            _scanWorstFrameMs = Math.Max(_scanWorstFrameMs, frameMs);

            if (_scanIndex < _scanList.Length)
                return;

            _myDigest = _scanAcc;
            _scanIndex = -1;

            // If most bottles "disagree", the two measures don't mean the same thing here: stop checking.
            if (_visualNetOn && _visualChecked > _scanList.Length / 4)
            {
                _visualNetOn = false;
                CoopMod.Log.Warning($"visual check off: {_visualChecked} of {_scanList.Length} bottles differ (measures not comparable)");
            }
            if (_visualFixed > 0)
                CoopMod.Log.Msg($"repainted {_visualFixed} bottle(s) whose level showed the wrong contents");
            (_visualSuspects, _visualNext) = (_visualNext, _visualSuspects);
            _visualNext.Clear();
            _visualChecked = 0;
            _visualFixed = 0;
            // Liquid totals in the log now and then (and once per lab), to compare the two players and over time.
            if (Now >= _nextLiquidLog)
            {
                _nextLiquidLog = Now + 300f;
                try { CoopMod.Log.Msg($"[liquid] {World.LiquidReport(populator)}"); }
                catch (Exception e) { CoopMod.Log.Warning($"liquid report: {e.Message}"); }
            }
            _nextScan = Now + DigestInterval;
            if (IsHost)
                _session.Send(new DigestMsg
                {
                    L = CurrentLab,
                    Buckets = _myDigest,
                    VatCapacity = World.Vats().ToDictionary(v => v.Uid, v => v.Capacity),
                    Separator = ReadSeparatorRules(),
                });
            if (Now >= _nextScanReport)
            {
                _nextScanReport = Now + 60f;
                CoopMod.Log.Msg($"world scan: {_scanCostMs:F1} ms total, worst frame {_scanWorstFrameMs:F1} ms");
            }
        }

        static void CompareDigest(uint[] hostDigest)
        {
            if (_myDigest == null || hostDigest == null || hostDigest.Length != Protocol.BucketCount)
                return;

            var need = new List<int>();
            for (var b = 0; b < Protocol.BucketCount; b++)
            {
                // A bucket someone is working in right now differs between two scans taken seconds apart anyway:
                // judge it once it has been quiet for a while (its changes travel as states meanwhile).
                if (hostDigest[b] == _myDigest[b] || Now - BucketActivity[b] < DigestInterval * 2f)
                {
                    MismatchStreak[b] = 0;
                    continue;
                }
                // Scans run at different moments; only trust a mismatch that survives two rounds.
                if (++MismatchStreak[b] >= 2)
                {
                    need.Add(b);
                    MismatchStreak[b] = 0;
                }
            }

            InSync = need.Count == 0 && MismatchStreak.All(s => s == 0);
            if (need.Count > 0)
            {
                CoopMod.Log.Msg($"worlds differ in bucket(s) {string.Join(",", need)}; asking host");
                _session.Send(new NeedMsg { Buckets = need.ToArray() });
            }
        }

        /// <summary>
        /// A resync that changes nothing yet keeps being needed means something refuses to converge:
        /// name the vessels that still differ after applying, so the log says what and where.
        /// </summary>
        static void CheckResyncProgress(StatesMsg states, int changed)
        {
            var buckets = states.Potions.Select(p => Protocol.BucketOf(p.Uid)).Distinct().ToList();
            foreach (var b in buckets)
                FruitlessResyncs[b] = changed == 0 ? FruitlessResyncs[b] + 1 : 0;
            if (changed > 0 || buckets.All(b => FruitlessResyncs[b] < 3))
                return;

            var populator = GameRefs.Populator;
            var held = World.LocalHeld(populator);
            var slots = World.SlotMap(populator);
            var report = new List<string>();
            foreach (var host in states.Potions)
            {
                if (!populator.TryGetPotion(host.Uid, out var potion) || potion == null)
                    continue;
                var mine = World.Read(populator, potion, held, withPose: false, slotOf: slots);
                if (World.Hash(mine) != World.Hash(host))
                    report.Add($"#{host.Uid} host={host.Location}@{host.Place} me={mine.Location}@{mine.Place}");
                if (report.Count >= 10)
                    break;
            }
            if (report.Count == 0)
            {
                // Every vessel the host sent matches: then the difference is in what is counted, not in a state.
                var sent = new HashSet<int>(states.Potions.Select(p => p.Uid));
                foreach (var b in buckets)
                {
                    var mine = new List<int>();
                    var nulls = 0;
                    foreach (var entry in populator._potionsByUid)
                    {
                        if (Protocol.BucketOf(entry.Key) != b)
                            continue;
                        if (entry.Value == null)
                            nulls++;
                        else
                            mine.Add(entry.Key);
                    }
                    var onlyMine = mine.Where(u => !sent.Contains(u)).Take(5).ToList();
                    var onlyHost = sent.Where(u => Protocol.BucketOf(u) == b && !mine.Contains(u)).Take(5).ToList();
                    var staleScan = _scanUids == null ? 0 : _scanUids.Where((u, i) => Protocol.BucketOf(u) == b && _scanList[i] == null).Count();
                    report.Add($"bucket {b}: {mine.Count} here ({nulls} gone, {staleScan} gone from the scan list), " +
                               $"{sent.Count(u => Protocol.BucketOf(u) == b)} from host; only here [{string.Join(",", onlyMine)}], only host [{string.Join(",", onlyHost)}]");
                }
            }
            CoopMod.Log.Warning($"buckets {string.Join(",", buckets)} do not converge; differing: {string.Join("; ", report)}");
            foreach (var b in buckets)
                FruitlessResyncs[b] = 0;
        }

        /// <summary>Ctrl+F7: a one-line health report to compare between the two players' logs.</summary>
        public static void DumpStatus()
        {
            var line = $"[status] {(IsRunning ? (IsHost ? "host" : "client") : "offline")} connected={IsConnected} sync={InSync?.ToString() ?? "?"} ping={PingMs:F0}ms";
            var populator = GameRefs.Populator;
            if (GameRefs.InLevel && populator != null)
            {
                var data = populator._runtimeDataService.Get<Il2CppCodeBase.Services.RuntimeDataService.PlayerRuntimeData>();
                line += $" held={populator.HeldPotions().Count} apron={populator.ApronPotions().Count} remoteHeld={World.RemoteHeld.Count}";
                if (data != null)
                    line += $" shelvedOk={data.CorrectlyShelvedPotions.Value} sealed={data.SealedPotions.Value} " +
                            $"shelves={data.CompletedShelves.Value}/{data.TotalShelves.Value} sections={data.CompletedSections.Value}/{data.TotalSections.Value}";
                var hero = GameRefs.Hero;
                if (hero != null)
                    line += $" pos={hero.Position}";
                line += $"\n[liquid] {World.LiquidReport(populator)}";
            }
            CoopMod.Log.Msg(line);
        }

        /// <summary>Client: ask for everything again. Host: hash now.</summary>
        public static void ForceResync()
        {
            if (!IsConnected)
                return;
            if (IsHost)
                _nextScan = 0;
            else
                _session.Send(new NeedMsg { Buckets = Enumerable.Range(0, Protocol.BucketCount).ToArray() });
        }

        public static void OnLevelLeft()
        {
            GameRefs.Forget();
            _scanIndex = -1;
            _scanList = null;
            _scanUids = null;
            _lastHeld.Clear();
            _initialSyncRequested = false;
            _nextLiquidLog = 0f;
            _visualSuspects.Clear();
            _visualNext.Clear();
            World.ResetLevel();
            CatSync.Forget();
        }
    }
}
