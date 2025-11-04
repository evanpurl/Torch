using NLog;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace Torch.Server
{
    internal static class Program
    {
        public static void Main(string[] args)
        {
            var log = LogManager.GetCurrentClassLogger();
            Console.WriteLine("Starting Torch dedicated server (console mode)...");

            try
            {
                // Set working directory
                var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
                Directory.SetCurrentDirectory(exeDir);

                // Load config
                var config = new TorchConfig();
                ConfigureAssemblyResolver();
                EnsureDedicatedServerInstalled();
                var server = new TorchServer(config);

                // Initialize
                server.Init();
                Console.WriteLine("[Program] Torch.Init() complete, waiting for VRage to be ready...");

                // Wait for VRage to reach "Stopped" state before starting (mirrors original Torch)
                var game = typeof(TorchBase)
                    .GetProperty("Game", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(server);

                if (game != null)
                {
                    var waitFor = game.GetType().GetMethod("WaitFor");
                    var stateEnum = game.GetType().GetNestedType("GameState");
                    var stoppedValue = Enum.Parse(stateEnum, "Stopped");
                    waitFor?.Invoke(game, new object[] { stoppedValue, TimeSpan.FromSeconds(10) });
                }

                Console.WriteLine("[Program] VRage reports ready, calling Start()...");
                server.Start();
                Console.WriteLine("[Program] Server started. Press Ctrl+C to stop.");

                // Graceful Ctrl+C shutdown
                var exitEvent = new ManualResetEvent(false);
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    Console.WriteLine("[Program] Ctrl+C detected. Stopping server...");
                    server.Stop();
                    exitEvent.Set();
                };

                // Keep alive until manual exit
                while (!exitEvent.WaitOne(1000))
                {
                    // Keep updating Torch managers every second
                    server.Update();
                }

                Console.WriteLine("Torch server stopped cleanly.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Fatal error in Torch.Server | Error: {ex}");
                Console.Error.WriteLine($"Fatal error: {ex}");
            }
            finally
            {
                LogManager.Shutdown();
            }
        }

        private static void EnsureDedicatedServerInstalled()
        {
            var sePath = Path.Combine(AppContext.BaseDirectory, "DedicatedServer64");

            // Check if we already have Space Engineers Dedicated installed
            if (File.Exists(Path.Combine(sePath, "Sandbox.Game.dll")))
            {
                Console.WriteLine("[Bootstrap] Found existing Space Engineers Dedicated binaries.");
                Environment.SetEnvironmentVariable("PATH",
                    sePath + ";" + Environment.GetEnvironmentVariable("PATH"));
                return;
            }

            Console.WriteLine("[Bootstrap] Space Engineers Dedicated not found — installing via SteamCMD...");

            var steamCmd = Path.Combine(AppContext.BaseDirectory, "steamcmd", "steamcmd.exe");
            if (!File.Exists(steamCmd))
                throw new FileNotFoundException("[Bootstrap] steamcmd.exe not found in working directory!");

            var args = $"+login anonymous +force_install_dir \"{AppContext.BaseDirectory}\" +app_update 298740 validate +quit";
            Console.WriteLine($"[Bootstrap] Running SteamCMD: {steamCmd} {args}");

            var psi = new ProcessStartInfo
            {
                FileName = steamCmd,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var proc = new Process { StartInfo = psi })
            {
                proc.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        Console.WriteLine($"[SteamCMD] {e.Data}");
                };

                proc.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        Console.Error.WriteLine($"[SteamCMD ERROR] {e.Data}");
                };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                proc.WaitForExit();
                Console.WriteLine($"[Bootstrap] SteamCMD exited with code {proc.ExitCode}");
            }

            if (!File.Exists(Path.Combine(sePath, "Sandbox.Game.dll")))
                throw new FileNotFoundException("[Bootstrap] SE Dedicated installation failed — Sandbox.Game.dll missing.");

            // Add Bin64 to PATH for VRage and SE runtime
            Environment.SetEnvironmentVariable("PATH",
                sePath + ";" + Environment.GetEnvironmentVariable("PATH"));

            Console.WriteLine("[Bootstrap] Space Engineers Dedicated installed successfully!");
        }

        private static void ConfigureAssemblyResolver()
        {
            var baseDir = AppContext.BaseDirectory;

            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                var assemblyName = new AssemblyName(args.Name).Name + ".dll";
                var searchPaths = new[]
                {
            Path.Combine(baseDir, assemblyName),
            Path.Combine(baseDir, "DedicatedServer64", assemblyName)
        };

                foreach (var path in searchPaths)
                {
                    if (File.Exists(path))
                    {
                        try
                        {
                            return Assembly.LoadFrom(path);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Resolver] Failed to load {path}: {ex.Message}");
                        }
                    }
                }

                return null;
            };

            //Console.WriteLine($"[Resolver] Custom assembly resolver active. Game binaries at: {gameBinaries}");
        }



    }
}
