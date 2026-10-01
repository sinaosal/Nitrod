using System;
using System.Collections;
using System.Collections.Generic;
using NitroxClient.Communication;
using NitroxClient.Communication.Abstract;
using NitroxClient.Communication.MultiplayerSession;
using NitroxClient.GameLogic;
using NitroxClient.GameLogic.Bases;
using NitroxClient.GameLogic.ChatUI;
using NitroxClient.GameLogic.PlayerLogic.PlayerModel.Abstract;
using NitroxClient.GameLogic.PlayerLogic.PlayerModel.ColorSwap;
using NitroxClient.MonoBehaviours.Cyclops;
using NitroxClient.MonoBehaviours.Discord;
using NitroxClient.MonoBehaviours.Gui.InGame;
using NitroxClient.MonoBehaviours.Gui.MainMenu.ServerJoin;
using Nitrox.Model.Core;
using Nitrox.Model.Packets.Core;
using Nitrox.Model.Subnautica.Packets;
using NitroxClient.Communication.Packets.Processors.Core;
using UnityEngine;
using UnityEngine.SceneManagement;
using UWE;

namespace NitroxClient.MonoBehaviours
{
    public class Multiplayer : MonoBehaviour
    {
        public static Multiplayer Main;
        private ClientProcessorContext packetProcessorContext;
        private PacketProcessorsInvoker processorInvoker = null!;
        private IClient client;
        private IMultiplayerSession multiplayerSession;
        private PacketReceiver packetReceiver;
        private IPacketSender packetSender;
        private ThrottledPacketSender throttledPacketSender;
        private GameLogic.Terrain terrain;

        public bool InitialSyncCompleted { get; set; }

        /// <summary>
        ///     True if multiplayer is loaded and client is connected to a server.
        /// </summary>
        public static bool Active => Main && Main.multiplayerSession.Client.IsConnected;

        /// <summary>
        ///     True if multiplayer is loaded and player has successfully joined a server.
        /// </summary>
        public static bool Joined => Main && Main.multiplayerSession.CurrentState.CurrentStage == MultiplayerSessionConnectionStage.SESSION_JOINED;

        public void Awake()
        {
            Log.Info($"Multiplayer.Awake entered. Scene: {SceneManager.GetActiveScene().name}");
            client = NitroxServiceLocator.LocateService<IClient>();
            multiplayerSession = NitroxServiceLocator.LocateService<IMultiplayerSession>();
            packetReceiver = NitroxServiceLocator.LocateService<PacketReceiver>();
            packetSender = NitroxServiceLocator.LocateService<IPacketSender>();
            throttledPacketSender = NitroxServiceLocator.LocateService<ThrottledPacketSender>();
            terrain = NitroxServiceLocator.LocateService<GameLogic.Terrain>();
            packetProcessorContext = new ClientProcessorContext(packetSender);
            processorInvoker = NitroxServiceLocator.LocateService<PacketProcessorsInvoker>();

            Main = this;
            DontDestroyOnLoad(gameObject);
            SceneManager.sceneLoaded += DiagnosticSceneLoaded;
            SceneManager.activeSceneChanged += DiagnosticActiveSceneChanged;

            Log.Info("Multiplayer client loaded…");
            Log.InGame(Language.main.Get("Nitrox_MultiplayerLoaded"));
        }

        public void OnDestroy()
        {
            SceneManager.sceneLoaded -= DiagnosticSceneLoaded;
            SceneManager.activeSceneChanged -= DiagnosticActiveSceneChanged;
            Log.Warn($"Multiplayer object destroyed. Scene: {SceneManager.GetActiveScene().name}, Connected: {client?.IsConnected}, Stage: {multiplayerSession?.CurrentState?.CurrentStage}");
        }

        public void OnApplicationQuit()
        {
            Log.Warn($"Application quit requested. Scene: {SceneManager.GetActiveScene().name}, Connected: {client?.IsConnected}, Stage: {multiplayerSession?.CurrentState?.CurrentStage}");
        }

        private static void DiagnosticSceneLoaded(Scene scene, LoadSceneMode loadMode)
        {
            Log.Info($"Scene loaded. Name: {scene.name}, Mode: {loadMode}, Active scene: {SceneManager.GetActiveScene().name}");
        }

        private static void DiagnosticActiveSceneChanged(Scene previousScene, Scene nextScene)
        {
            Log.Info($"Active scene changed from {previousScene.name} to {nextScene.name}");
        }

        public void Update()
        {
            client.PollEvents();

            if (multiplayerSession.CurrentState.CurrentStage != MultiplayerSessionConnectionStage.DISCONNECTED)
            {
                ProcessPackets();
                throttledPacketSender.Update();

                // Loading up shouldn't be bothered by entities spawning in the surroundings
                if (multiplayerSession.CurrentState.CurrentStage == MultiplayerSessionConnectionStage.SESSION_JOINED &&
                    InitialSyncCompleted)
                {
                    terrain.UpdateVisibility();
                }
            }
        }

        public static event Action OnLoadingComplete;
        public static event Action OnBeforeMultiplayerStart;
        public static event Action OnAfterMultiplayerEnd;

        public static void SubnauticaLoadingStarted()
        {
            Log.Info($"SubnauticaLoadingStarted invoked. Scene: {SceneManager.GetActiveScene().name}, Subscribers: {OnBeforeMultiplayerStart?.GetInvocationList().Length ?? 0}");
            OnBeforeMultiplayerStart?.Invoke();
            Log.Info("SubnauticaLoadingStarted subscribers completed");
        }

        public static void SubnauticaLoadingCompleted()
        {
            bool connected = Main && Main.client?.IsConnected == true;
            Log.Info($"SubnauticaLoadingCompleted invoked. Scene: {SceneManager.GetActiveScene().name}, Active: {Active}, Connected: {connected}");
            if (Active)
            {
                Main.InitialSyncCompleted = false;
                Main.StartCoroutine(LoadAsync());
                Log.Info("Started Nitrox post-world-load coroutine");
            }
            else
            {
                SetLoadingComplete();
                OnLoadingComplete?.Invoke();
            }
        }

        public static IEnumerator LoadAsync()
        {
            Log.Info("Waiting for LargeWorldStreamer to settle");
            WaitScreen.ManualWaitItem worldSettleItem = WaitScreen.Add(Language.main.Get("Nitrox_WorldSettling"));

            yield return new WaitUntil(() => LargeWorldStreamer.main != null &&
                                             LargeWorldStreamer.main.land != null &&
                                             LargeWorldStreamer.main.IsReady() &&
                                             LargeWorldStreamer.main.IsWorldSettled());

            Log.Info("LargeWorldStreamer settled; starting multiplayer session");
            WaitScreen.Remove(worldSettleItem);

            WaitScreen.ManualWaitItem joiningItem = WaitScreen.Add(Language.main.Get("Nitrox_JoiningSession"));
            yield return Main.StartCoroutine(Main.StartSession());
            Log.Info("Multiplayer session start coroutine completed; waiting for initial sync");
            WaitScreen.Remove(joiningItem);

            WaitScreen.ManualWaitItem waitingItem = WaitScreen.Add(Language.main.Get("Nitrox_Waiting"));
            Log.InGame(Language.main.Get("Nitrox_Waiting"));
            yield return new WaitUntil(() => Main.InitialSyncCompleted);
            Log.Info("Initial sync completed; finishing multiplayer load");
            WaitScreen.Remove(waitingItem);

            SetLoadingComplete();
            OnLoadingComplete?.Invoke();
        }

        public void ProcessPackets()
        {
            packetReceiver.ConsumePackets(static (packet, context) =>
            {
                try
                {
                    PacketProcessorsInvoker.Entry processor = context.processorInvoker.GetProcessor(packet.GetType());
                    if (processor == null)
                    {
                        throw new Exception($"Failed to find packet processor for packet {packet.GetType()}");
                    }
                    processor.Execute(context.packetProcessorContext, packet).ContinueWithHandleError();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Error trying to process packet {packet}");
                }
            }, (processorInvoker, packetSender, packetProcessorContext));
        }

        public IEnumerator StartSession()
        {
            Log.Info("Initializing local player state before joining session");
            yield return StartCoroutine(InitializeLocalPlayerState());
            Log.Info("Local player state initialized; sending join-session packet");
            multiplayerSession.JoinSession();
            Log.Info($"Join-session packet sent. Stage: {multiplayerSession.CurrentState.CurrentStage}");
            InitMonoBehaviours();
            Log.Info("Multiplayer gameplay behaviours initialized");
            Utils.SetContinueMode(true);
            SceneManager.sceneLoaded += SceneManager_sceneLoaded;

            RegisterConnectedDelegates();
        }

        public void InitMonoBehaviours()
        {
            // Gameplay.
            gameObject.AddComponent<UnderwaterStateTracker>();
            gameObject.AddComponent<PrecursorTracker>();
            gameObject.AddComponent<PlayerMovementBroadcaster>();
            gameObject.AddComponent<PlayerDeathBroadcaster>();
            gameObject.AddComponent<PlayerStatsBroadcaster>();
            gameObject.AddComponent<EntityPositionBroadcaster>();
            gameObject.AddComponent<BuildingHandler>();
            gameObject.AddComponent<MovementBroadcaster>();
            gameObject.AddComponent<PlayerPingManager>();
            VirtualCyclops.Initialize();
        }

        public void StopCurrentSession()
        {
            SceneManager.sceneLoaded -= SceneManager_sceneLoaded;
            OnAfterMultiplayerEnd?.Invoke();

            UnregisterConnectedDelegates();
        }

        private static void SetLoadingComplete()
        {
            WaitScreen.main.isWaiting = false;
            WaitScreen.main.stageProgress.Clear();
            FreezeTime.End(FreezeTime.Id.WaitScreen);
            WaitScreen.main.items.Clear();

            PlayerManager remotePlayerManager = NitroxServiceLocator.LocateService<PlayerManager>();

            TopRightWatermarkText.ApplyChangesForInGame();
            DiscordClient.InitializeRPInGame(Main.multiplayerSession.AuthenticationContext.Username, remotePlayerManager.GetTotalPlayerCount(), Main.multiplayerSession.SessionPolicy.MaxConnections);
            CoroutineHost.StartCoroutine(PlayerChatManager.Instance.LoadChatKeyHint());
        }

        private IEnumerator InitializeLocalPlayerState()
        {
            ILocalNitroxPlayer localPlayer = NitroxServiceLocator.LocateService<ILocalNitroxPlayer>();
            IEnumerable<IColorSwapManager> colorSwapManagers = NitroxServiceLocator.LocateService<IEnumerable<IColorSwapManager>>();

            // This is used to init the lazy GameObject in order to create a real default Body Prototype for other players
            GameObject body = localPlayer.BodyPrototype;
            Log.Info($"Init body prototype {body.name}");

            ColorSwapAsyncOperation swapOperation = new ColorSwapAsyncOperation(localPlayer, colorSwapManagers).BeginColorSwap();
            yield return new WaitUntil(() => swapOperation.IsColorSwapComplete());
            swapOperation.ApplySwappedColors();

            // UWE developers added noisy logging for non-whitelisted components during serialization.
            // We add NitroxEntiy in here to avoid a large amount of log spam.
            ProtobufSerializer.componentWhitelist.Add(nameof(NitroxEntity));
        }

        private void SceneManager_sceneLoaded(Scene scene, LoadSceneMode loadMode)
        {
            if (scene.name == "XMenu")
            {
                // If we just disconnected from a multiplayer session, then we need to kill the connection here.
                // Maybe a better place for this, but here works in a pinch.
                JoinServerBackend.StopMultiplayerClient();
            }
        }

        private void OnPlayerChat(string message)
        {
            multiplayerSession.Send(new ChatMessage(multiplayerSession.Reservation.SessionId, message));
        }

        private void OnPlayerCommand(string command)
        {
            multiplayerSession.Send(new ServerCommand(command));
        }

        public void RegisterConnectedDelegates()
        {
            PlayerChatManager.Instance.OnPlayerChat += OnPlayerChat;
            PlayerChatManager.Instance.OnPlayerCommand += OnPlayerCommand;
        }

        public void UnregisterConnectedDelegates()
        {
            PlayerChatManager.Instance.OnPlayerChat -= OnPlayerChat;
            PlayerChatManager.Instance.OnPlayerCommand -= OnPlayerCommand;
        }
    }
}
