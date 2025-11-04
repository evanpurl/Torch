using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Havok;
using NLog;
using NLog.Fluent;
using Sandbox;
using Sandbox.Engine.Analytics;
using Sandbox.Engine.Multiplayer;
using Sandbox.Engine.Networking;
using Sandbox.Engine.Platform.VideoMode;
using Sandbox.Engine.Utils;
using Sandbox.Game;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineers.Game;
using SpaceEngineers.Game.GUI;
using Steamworks;
using Torch.API;
using Torch.Utils;
using VRage;
using VRage.Audio;
using VRage.Dedicated;
using VRage.EOS;
using VRage.FileSystem;
using VRage.Game;
using VRage.Game.ObjectBuilder;
using VRage.Game.SessionComponents;
using VRage.GameServices;
using VRage.Mod.Io;
using VRage.Plugins;
using VRage.Scripting;
using VRage.Steam;
using VRage.Utils;
using VRageRender;
using MyRenderProfiler = VRage.Profiler.MyRenderProfiler;

namespace Torch
{
    public class VRageGame
    {
        private static readonly ILogger _log = LogManager.GetCurrentClassLogger();

#pragma warning disable 649
        [ReflectedGetter(Name = "m_plugins", Type = typeof(MyPlugins))]
        private static readonly Func<List<IPlugin>> _getVRagePluginList;

        [ReflectedGetter(Name = "Static", TypeName = "Sandbox.Game.Audio.MyMusicController, Sandbox.Game")]
        private static readonly Func<object> _getMusicControllerStatic;


        [ReflectedSetter(Name = "Static", TypeName = "Sandbox.Game.Audio.MyMusicController, Sandbox.Game")]
        private static readonly Action<object> _setMusicControllerStatic;


        [ReflectedMethod(Name = "Unload", TypeName = "Sandbox.Game.Audio.MyMusicController, Sandbox.Game")]
        private static readonly Action<object> _musicControllerUnload;

        //[ReflectedGetter(Name = "UpdateLayerDescriptors", Type = typeof(MyReplicationServer))]
        //private static readonly Func<MyReplicationServer.UpdateLayerDesc[]> _layerSettings;

#pragma warning restore 649

        private readonly TorchBase _torch;
        private readonly Action _tweakGameSettings;
        private readonly string _userDataPath;
        private readonly string _appName;
        private readonly uint _appSteamId;
        private readonly string[] _runArgs;
        private SpaceEngineersGame _game;
        private readonly Thread _updateThread;
        private readonly string _rootPath;
        private readonly string _modCachePath = null;

        private bool _startGame = false;
        private readonly AutoResetEvent _commandChanged = new AutoResetEvent(false);
        private bool _destroyGame = false;

        private readonly AutoResetEvent _stateChangedEvent = new AutoResetEvent(false);
        private GameState _state;

        public enum GameState
        {
            Creating,
            Stopped,
            Running,
            Destroyed
        }

        internal VRageGame(TorchBase torch, Action tweakGameSettings, string appName, uint appSteamId,
    string userDataPath, string[] runArgs)
        {
            _torch = torch;
            _tweakGameSettings = tweakGameSettings ?? (() => { });
            _appName = appName;
            _appSteamId = appSteamId;

            _rootPath = TryGetRootPathSafe();

            _userDataPath = userDataPath;
            _runArgs = runArgs ?? Array.Empty<string>();

            Console.WriteLine($"[VRageGame] ctor: app={_appName}, steamId={_appSteamId}, rootPath={_rootPath}, userPath={_userDataPath ?? "<null>"}");

            _updateThread = new Thread(Run) { IsBackground = true, Name = "Torch.VRageGame" };
            _updateThread.Start();
        }

        private static string TryGetRootPathSafe()
        {
            try
            {
                // Newer builds expose this, but it may be null before VRage init.
                return MyVRage.Platform?.System?.GetRootPath()
                       ?? AppContext.BaseDirectory
                       ?? Environment.CurrentDirectory;
            }
            catch
            {
                return AppContext.BaseDirectory ?? Environment.CurrentDirectory;
            }
        }


        private void StateChange(GameState s)
        {
            if (_state == s)
                return;
            _state = s;
            _stateChangedEvent.Set();
        }

        private void Run()
        {
            StateChange(GameState.Creating);
            try
            {
                Create();
                _destroyGame = false;
                while (!_destroyGame)
                {
                    StateChange(GameState.Stopped);
                    _commandChanged.WaitOne();
                    if (_startGame)
                    {
                        _startGame = false;
                        DoStart();
                    }
                }
            }
            finally
            {
                Destroy();
                StateChange(GameState.Destroyed);
            }
        }

        private void Create()
        {
            bool dedicated = true;
            Environment.SetEnvironmentVariable("SteamAppId", _appSteamId.ToString());

            // Basic Space Engineers setup
            SpaceEngineersGame.SetupBasicGameInfo();
            SpaceEngineersGame.SetupPerGameSettings();
            MyFinalBuildConstants.APP_VERSION = MyPerGameSettings.BasicGameInfo.GameVersion;
            MySessionComponentExtDebug.ForceDisable = true;
            MyPerGameSettings.SendLogToKeen = false;

            // --- Initialize MyVRage.Platform if needed ---
            if (MyVRage.Platform == null)
            {
                Console.WriteLine("[VRageGame] MyVRage.Platform is null — initializing manually...");
                try
                {
                    object platform = null;

                    // Try MyVRageWindows (newer builds)
                    var windowsType = Type.GetType("VRage.Platform.Windows.MyVRageWindows, VRage.Platform.Windows", throwOnError: false);
                    var platformType = Type.GetType("VRage.Platform.Windows.MyVRagePlatform, VRage.Platform.Windows", throwOnError: false);

                    if (windowsType != null)
                    {
                        var initMethod = windowsType.GetMethod("Init", BindingFlags.Public | BindingFlags.Static);
                        if (initMethod != null)
                        {
                            Console.WriteLine("[VRageGame] Calling MyVRageWindows.Init() ...");
                            // Try to invoke with minimal args (string, log, null, false) or variants
                            var parameters = initMethod.GetParameters();
                            var args = new object[parameters.Length];
                            for (int i = 0; i < parameters.Length; i++)
                            {
                                var p = parameters[i];
                                if (p.ParameterType == typeof(string))
                                    args[i] = "SpaceEngineersDedicated";
                                else if (p.ParameterType.FullName?.Contains("IMyLog") == true)
                                    args[i] = MySandboxGame.Log;
                                else if (p.ParameterType == typeof(bool))
                                    args[i] = false;
                                else
                                    args[i] = null;
                            }
                            initMethod.Invoke(null, args);
                            Console.WriteLine("[VRageGame] MyVRageWindows.Init() invoked successfully.");
                        }
                    }

                    // Fallback: MyVRagePlatform.Create()
                    if (MyVRage.Platform == null && platformType != null)
                    {
                        var createMethod = platformType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static);
                        if (createMethod != null)
                        {
                            platform = createMethod.Invoke(null, null);
                            typeof(MyVRage).GetMethod("Init", BindingFlags.Public | BindingFlags.Static)!
                                .Invoke(null, new[] { platform });
                            Console.WriteLine("[VRageGame] MyVRagePlatform initialized via Create().");
                        }
                    }

                    // Fallback 2: MyVRage.Init(new MyVRagePlatform())
                    if (MyVRage.Platform == null && platformType != null)
                    {
                        var ctor = platformType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
                                               .FirstOrDefault(c => c.GetParameters().Length == 0);
                        if (ctor != null)
                        {
                            platform = ctor.Invoke(null);
                            typeof(MyVRage).GetMethod("Init", BindingFlags.Public | BindingFlags.Static)!
                                .Invoke(null, new[] { platform });
                            Console.WriteLine("[VRageGame] MyVRagePlatform initialized via private ctor.");
                        }
                    }

                    if (MyVRage.Platform == null)
                        throw new InvalidOperationException("Failed to initialize MyVRage.Platform (no compatible init path found).");

                    Console.WriteLine("[VRageGame] MyVRagePlatform initialized successfully.");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[VRageGame] Failed to initialize MyVRagePlatform: {ex}");
                    throw;
                }
            }


            _ = MyVRage.Platform.Scripting;

            MyFileSystem.ExePath = Path.GetDirectoryName(typeof(SpaceEngineersGame).Assembly.Location);

            _tweakGameSettings();

            MyFileSystem.Reset();
            MyInitializer.InvokeBeforeRun(_appSteamId, _appName, _rootPath, _userDataPath, false, -1, null, _modCachePath);

            Console.WriteLine("Loading Dedicated Config");
            MySandboxGame.ConfigDedicated.Load();
            MyPlatformGameSettings.CONSOLE_COMPATIBLE = MySandboxGame.ConfigDedicated.ConsoleCompatibility;

            Console.WriteLine("Initializing network services");

            var isEos = TorchBase.Instance.Config.UgcServiceType == UGCServiceType.EOS;
            if (isEos)
            {
                Console.WriteLine("Running on Epic Online Services.");
                Console.WriteLine("Steam workshop will not work with current settings. Some functions might not work properly!");
            }

            var aggregator = new MyServerDiscoveryAggregator();
            MyServiceManager.Instance.AddService<IMyServerDiscovery>(aggregator);

            IMyGameService service = null;

            try
            {
                if (isEos)
                {
                    service = MyEOSService.Create();
                    MyEOSService.InitNetworking(
                        dedicated,
                        false,
                        "Space Engineers",
                        service,
                        "xyza7891A4WeGrpP85BTlBa3BSfUEABN",
                        "ZdHZVevSVfIajebTnTmh5MVi3KPHflszD9hJB7mRkgg",
                        "24b1cd652a18461fa9b3d533ac8d6b5b",
                        "1958fe26c66d4151a327ec162e4d49c8",
                        "07c169b3b641401496d352cad1c905d6",
                        "https://retail.epicgames.com/",
                        MyEOSService.CreatePlatform(),
                        MySandboxGame.ConfigDedicated.VerboseNetworkLogging,
                        Enumerable.Empty<string>(),
                        aggregator,
                        MyMultiplayer.Channels
                    );

                    var mockingInventory = new MyMockingInventory(service);
                    MyServiceManager.Instance.AddService<IMyInventoryService>(mockingInventory);
                }
                else
                {
                    service = MySteamGameService.Create(dedicated, _appSteamId);

                    // ✅ Register the service FIRST
                    MyServiceManager.Instance.AddService(service);

                    // ✅ Then safely access WorkshopService
                    MyGameService.WorkshopService.AddAggregate(MySteamUgcService.Create(_appSteamId, service));

                    MySteamGameService.InitNetworking(
                        dedicated,
                        service,
                        "Space Engineers",
                        aggregator
                    );
                }


                if (service == null)
                    throw new InvalidOperationException("Failed to create IMyGameService (Steam or EOS service is null).");

                // Add Mod.io aggregate workshop
                MyGameService.WorkshopService.AddAggregate(MyModIoService.Create(
                    service,
                    "spaceengineers",
                    "264",
                    "1fb4489996a5e8ffc6ec1135f9985b5b",
                    "331",
                    "f2b64abe55452252b030c48adc0c1f0e",
                    MyPlatformGameSettings.UGC_TEST_ENVIRONMENT,
                    true,
                    "XboxLive",
                    "XboxOne"
                ));

                if (!isEos && !MyGameService.HasGameServer)
                {
                    Console.WriteLine("Network service is not running! Please reinstall dedicated server.");
                    return;
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to initialize game service layer.");
                throw;
            }

            Console.WriteLine("Initializing services");
            MyServiceManager.Instance.AddService<IMyMicrophoneService>(new MyNullMicrophone());
            MyNetworkMonitor.Init();
            Console.WriteLine("Services initialized");

            MySandboxGame.InitMultithreading();

            if (!MySandboxGame.IsReloading)
                MyFileSystem.InitUserSpecific(dedicated ? null : MyGameService.UserId.ToString());
            MySandboxGame.IsReloading = dedicated;

            // Renderer setup
            IMyRender renderer = dedicated ? new MyNullRender() : null;
            MyRenderProxy.Initialize(renderer);
            MyRenderProfiler.SetAutocommit(false);

            // Load serializers
            Console.WriteLine("Setting up serializers");
            MyPlugins.RegisterGameAssemblyFile(MyPerGameSettings.GameModAssembly);
            if (MyPerGameSettings.GameModBaseObjBuildersAssembly != null)
                MyPlugins.RegisterBaseGameObjectBuildersAssemblyFile(MyPerGameSettings.GameModBaseObjBuildersAssembly);
            MyPlugins.RegisterGameObjectBuildersAssemblyFile(MyPerGameSettings.GameModObjBuildersAssembly);
            MyPlugins.RegisterSandboxAssemblyFile(MyPerGameSettings.SandboxAssembly);
            MyPlugins.RegisterSandboxGameAssemblyFile(MyPerGameSettings.SandboxGameAssembly);
            MyGlobalTypeMetadata.Static.Init(false);
            typeof(MySandboxGame).GetMethod("Preallocate", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, null);

            // Fix up entity factory registration
            var entityFactory = Type.GetType("Sandbox.Game.Entities.MyEntityFactory, Sandbox.Game");
            var objFactory = (VRage.ObjectBuilders.MyObjectFactory<
                VRage.Game.Entity.MyEntityTypeAttribute,
                VRage.Game.Entity.MyEntity>)entityFactory?
                .GetField("m_objectFactory", BindingFlags.Static | BindingFlags.NonPublic)?
                .GetValue(null);

            if (objFactory != null && objFactory.TryGetProducedType(typeof(MyObjectBuilder_CubePlacer)) == null)
            {
                var registerDescriptorsFromAssembly = entityFactory.GetMethod(
                    "RegisterDescriptorsFromAssembly",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(Assembly[]) },
                    null
                );

                registerDescriptorsFromAssembly?.Invoke(null, new object[] { new[] { MyPlugins.GameAssembly, MyPlugins.SandboxAssembly } });
                registerDescriptorsFromAssembly?.Invoke(null, new object[] { MyPlugins.UserAssemblies });
            }
        }


        private void Destroy()
        {
            Console.WriteLine("[VRageGame] Begin graceful shutdown...");

            try
            {
                // Try to stop the running SpaceEngineersGame if it exists
                if (_game != null)
                {
                    try
                    {
                        Console.WriteLine("[VRageGame] Stopping game loop...");
                        _game.Exit();
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"[VRageGame] _game.Exit() threw: {e.Message}");
                    }

                    // ❌ SKIP Keen’s MySandboxGame.Dispose() to prevent MyScriptCompiler whitelist crash
                    Console.WriteLine("[VRageGame] Skipping MySandboxGame.Dispose() to avoid MyScriptCompiler static init crash.");
                    _game = null;
                }
            }
            catch (Exception e)
            {
                _log.Warn(e, "Game shutdown threw (non-fatal).");
            }

            // --- Clean up Keen services safely ---
            try
            {
                if (MyGameService.HasGameServer)
                {
                    Console.WriteLine("[VRageGame] Logging off game server...");
                    MyGameService.GameServer?.LogOff();
                }
            }
            catch (Exception e)
            {
                _log.Warn(e, "GameServer.LogOff threw.");
            }

            try
            {
                Console.WriteLine("[VRageGame] Shutting down MyGameService...");
                MyGameService.ShutDown();
            }
            catch (Exception e)
            {
                _log.Warn(e, "MyGameService.ShutDown threw.");
            }

            try
            {
                _getVRagePluginList()?.Remove(_torch);
            }
            catch
            {
                // Ignore: plugin list may be null or uninitialized
            }

            try
            {
                Console.WriteLine("[VRageGame] Invoking MyInitializer.AfterRun...");
                MyInitializer.InvokeAfterRun();
            }
            catch (Exception e)
            {
                _log.Warn(e, "InvokeAfterRun threw.");
            }

            StateChange(GameState.Destroyed);
            Console.WriteLine("[VRageGame] Shutdown complete. State=Destroyed");
        }



        private void DoStart()
        {
            _game = new SpaceEngineersGame(_runArgs);

            if (MySandboxGame.FatalErrorDuringInit)
                throw new InvalidOperationException("Failed to start sandbox game: see Keen log for details");

            try
            {
                // ✅ Safe: at this point, MyPlugins.Init() has already run
                var pluginList = _getVRagePluginList?.Invoke();
                if (pluginList != null && !pluginList.Contains(_torch))
                {
                    pluginList.Add(_torch);
                    Console.WriteLine("[VRageGame] Torch plugin added to VRage after SE game init.");
                }

                StateChange(GameState.Running);
                _game.Run();
            }
            finally
            {
                StateChange(GameState.Stopped);
            }
        }


        private void DoDisableAutoload()
        {
            if (MySandboxGame.ConfigDedicated is MyConfigDedicated<MyObjectBuilder_SessionSettings> config)
            {
                var tempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
                Directory.CreateDirectory(tempDirectory);
                config.LoadWorld = null;
                config.PremadeCheckpointPath = tempDirectory;
            }
        }


#pragma warning disable 649
        [ReflectedMethod(Name = "StartServer")]
        private static Action<MySession, MyMultiplayerBase> _hostServerForSession;
#pragma warning restore 649

        private void DoLoadSession(string sessionPath)
        {
            if (!Path.IsPathRooted(sessionPath))
                sessionPath = Path.Combine(MyFileSystem.SavesPath, sessionPath);

            if (!Sandbox.Engine.Platform.Game.IsDedicated)
            {
                MySessionLoader.LoadSingleplayerSession(sessionPath);
                return;
            }
            MyObjectBuilder_Checkpoint checkpoint = MyLocalCache.LoadCheckpoint(sessionPath, out ulong checkpointSize);
        }

        private void DoJoinSession(ulong lobbyId)
        {
            MyJoinGameHelper.JoinGame(lobbyId);
        }

        private void DoUnloadSession()
        {
            if (!Sandbox.Engine.Platform.Game.IsDedicated)
            {
                MyScreenManager.CloseAllScreensExcept(null);
                MyGuiSandbox.Update(16);
            }
            if (MySession.Static != null)
            {
                MySession.Static.Unload();
                MySession.Static = null;
            }
            {
                var musicCtl = _getMusicControllerStatic();
                if (musicCtl != null)
                {
                    _musicControllerUnload(musicCtl);
                    _setMusicControllerStatic(null);
                    MyAudio.Static.MusicAllowed = true;
                }
            }
            if (MyMultiplayer.Static != null)
            {
                MyMultiplayer.Static.Dispose();
            }
            if (!Sandbox.Engine.Platform.Game.IsDedicated)
            {
                MyGuiSandbox.AddScreen(MyGuiSandbox.CreateScreen(MyPerGameSettings.GUI.MainMenu));
            }
        }

        private void DoStop()
        {
            ParallelTasks.Parallel.Scheduler.WaitForTasksToFinish(TimeSpan.FromSeconds(10.0));
            MySandboxGame.Static.Exit();
        }

        /// <summary>
        /// Signals the game to stop itself.
        /// </summary>
        public void SignalStop()
        {
            _startGame = false;
            var game = _game;
            if (game != null)
                game.Invoke(DoStop, $"{nameof(VRageGame)}::{nameof(SignalStop)}");
        }


        /// <summary>
        /// Signals the game to start itself
        /// </summary>
        public void SignalStart()
        {
            _startGame = true;
            _commandChanged.Set();
        }

        /// <summary>
        /// Signals the game to destroy itself
        /// </summary>
        public void SignalDestroy()
        {
            _destroyGame = true;
            SignalStop();
            _commandChanged.Set();
        }

        public Task LoadSession(string path)
        {
            return _torch.InvokeAsync(() => DoLoadSession(path));
        }

        public Task JoinSession(ulong lobbyId)
        {
            return _torch.InvokeAsync(() => DoJoinSession(lobbyId));
        }

        public Task UnloadSession()
        {
            return _torch.InvokeAsync(DoUnloadSession);
        }

        /// <summary>
        /// Waits for the game to transition to the given state
        /// </summary>
        /// <param name="state">State to transition to</param>
        /// <param name="timeout">Timeout</param>
        /// <returns></returns>
        public bool WaitFor(GameState state, TimeSpan? timeout = null)
        {
            // Kinda icky, but we can't block the update and expect the state to change.
            if (Thread.CurrentThread == _updateThread)
                return _state == state;

            DateTime? end = timeout.HasValue ? (DateTime?)(DateTime.Now + timeout.Value) : null;
            while (_state != state && (!end.HasValue || end > DateTime.Now + TimeSpan.FromSeconds(1)))
                if (end.HasValue)
                    _stateChangedEvent.WaitOne(end.Value - DateTime.Now);
                else
                    _stateChangedEvent.WaitOne();
            return _state == state;
        }
    }
}
