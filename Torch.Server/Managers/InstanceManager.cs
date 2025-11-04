using NLog;
using Sandbox;
using Sandbox.Engine.Utils;
using Sandbox.Game;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Torch.API;
using Torch.API.Managers;
using Torch.Managers;
using Torch.Mod;
using VRage.FileSystem;
using VRage.Game;
using VRage.ObjectBuilders;

namespace Torch.Server.Managers
{
    public class InstanceManager : Manager
    {
        private const string CONFIG_NAME = "SpaceEngineers-Dedicated.cfg";
        private static readonly Logger Log = LogManager.GetLogger(nameof(InstanceManager));

        public MyConfigDedicated<MyObjectBuilder_SessionSettings> DedicatedConfig { get; private set; }
        public List<WorldInfo> Worlds { get; private set; } = new();

        public event Action<MyConfigDedicated<MyObjectBuilder_SessionSettings>> InstanceLoaded;

        public InstanceManager(ITorchBase torchInstance) : base(torchInstance) { }

        public void LoadInstance(string path, bool validate = true)
        {
            Console.WriteLine($"Loading instance {path}");
            if (validate)
                ValidateInstance(path);

            MyFileSystem.Reset();
            MyFileSystem.Init("Content", path);
            MyFileSystem.InitUserSpecific(null);

            var configPath = Path.Combine(path, CONFIG_NAME);
            DedicatedConfig = new MyConfigDedicated<MyObjectBuilder_SessionSettings>(configPath);
            DedicatedConfig.Load(configPath);

            // Load all worlds
            var savesDir = Path.Combine(path, "Saves");
            if (!Directory.Exists(savesDir))
            {
                Console.WriteLine("No 'Saves' directory found; creating one.");
                Directory.CreateDirectory(savesDir);
            }

            Worlds = Directory.EnumerateDirectories(savesDir)
                .Where(f => File.Exists(Path.Combine(f, "Sandbox.sbc")))
                .Select(f => new WorldInfo(f))
                .ToList();

            if (Worlds.Count == 0)
            {
                Console.WriteLine($"No worlds found in instance {path}.");
                return;
            }

            // Load first world or configured world
            var worldPath = DedicatedConfig.LoadWorld ?? Worlds.First().Path;
            SelectWorld(worldPath);

            InstanceLoaded?.Invoke(DedicatedConfig);
        }

        public void SelectWorld(string worldPath)
        {
            if (!Directory.Exists(worldPath))
            {
                Log.Error($"World path not found: {worldPath}");
                return;
            }

            DedicatedConfig.LoadWorld = worldPath;

            try
            {
                var world = new WorldInfo(worldPath);
                Console.WriteLine($"Selected world: {world.Name}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Failed to load world at path: {worldPath}");
                DedicatedConfig.LoadWorld = null;
            }
        }

        public void SaveConfig()
        {
            try
            {
                var configPath = Path.Combine(Torch.Config.InstancePath, CONFIG_NAME);
                DedicatedConfig.Save(configPath);
                Console.WriteLine("Saved dedicated config.");

                var worldPath = DedicatedConfig.LoadWorld;
                if (worldPath == null)
                {
                    Console.WriteLine("No world selected; skipping world save.");
                    return;
                }

                var world = new WorldInfo(worldPath);
                world.Save();
                Console.WriteLine("Saved world checkpoint/config.");
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to save instance configuration.");
            }
        }

        private void ValidateInstance(string path)
        {
            Directory.CreateDirectory(Path.Combine(path, "Saves"));
            Directory.CreateDirectory(Path.Combine(path, "Mods"));

            var configPath = Path.Combine(path, CONFIG_NAME);
            if (!File.Exists(configPath))
            {
                var cfg = new MyConfigDedicated<MyObjectBuilder_SessionSettings>(configPath);
                cfg.Save(configPath);
            }
        }
    }

    public class WorldInfo
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public string Path { get; }
        public string Name => System.IO.Path.GetFileName(Path);
        public MyObjectBuilder_Checkpoint Checkpoint { get; private set; }
        public MyObjectBuilder_WorldConfiguration WorldConfig { get; private set; }

        public WorldInfo(string path)
        {
            Path = path;
            Load();
        }

        private void Load()
        {
            var checkpointPath = System.IO.Path.Combine(Path, "Sandbox.sbc");
            var configPath = System.IO.Path.Combine(Path, "Sandbox_config.sbc");

            if (!File.Exists(checkpointPath))
                throw new FileNotFoundException($"Missing Sandbox.sbc at {Path}");

            MyObjectBuilderSerializer.DeserializeXML(checkpointPath, out MyObjectBuilder_Checkpoint checkpoint, out _);
            Checkpoint = checkpoint ?? throw new Exception($"Failed to load checkpoint for {Path}");

            if (File.Exists(configPath))
            {
                MyObjectBuilderSerializer.DeserializeXML(configPath, out MyObjectBuilder_WorldConfiguration worldConfig, out _);
                WorldConfig = worldConfig;
            }
            else
            {
                // Fallback to embedded settings
                WorldConfig = new MyObjectBuilder_WorldConfiguration
                {
                    Mods = checkpoint.Mods,
                    Settings = checkpoint.Settings
                };
            }
        }

        public void Save()
        {
            var checkpointPath = System.IO.Path.Combine(Path, "Sandbox.sbc");
            var configPath = System.IO.Path.Combine(Path, "Sandbox_config.sbc");

            MyObjectBuilderSerializer.SerializeXML(checkpointPath, false, Checkpoint);
            MyObjectBuilderSerializer.SerializeXML(configPath, false, WorldConfig);
        }
    }
}
