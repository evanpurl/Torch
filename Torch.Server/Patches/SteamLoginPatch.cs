using System;
using System.Linq;
using System.Reflection;
using NLog;
using SteamKit2;
using Torch.Managers.PatchManager;

namespace Torch.Patches
{
    [PatchShim]
    public static class SteamLoginPatch
    {
        private static readonly ILogger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext context)
        {
            try
            {
                // Try to find the VRage SteamGameServer type dynamically
                var serverType = Type.GetType("VRage.Steam.MySteamGameServer, VRage.Steam");
                if (serverType == null)
                {
                    Console.WriteLine("SteamLoginPatch: Could not find VRage.Steam.MySteamGameServer. Skipping patch.");
                    return;
                }

                // Try both old and new method names
                var loginMethod =
                    serverType.GetMethod("LogOnAnonymous", BindingFlags.Public | BindingFlags.Static) ??
                    serverType.GetMethod("LogOn", BindingFlags.Public | BindingFlags.Static);

                if (loginMethod == null)
                {
                    Console.WriteLine("SteamLoginPatch: No LogOn[Anonymous] method found. Skipping login patch.");
                }
                else
                {
                    var prefix = typeof(SteamLoginPatch).GetMethod(nameof(Prefix),
                        BindingFlags.Static | BindingFlags.NonPublic);
                    context.GetPattern(loginMethod).Prefixes.Add(prefix);
                    Log.Info($"SteamLoginPatch: Hooked login method {loginMethod.Name}");
                }

                // Patch WaitStart if it exists
                var waitStart = serverType.GetMethod("WaitStart", BindingFlags.Public | BindingFlags.Static);
                if (waitStart != null)
                {
                    var prefix = typeof(SteamLoginPatch).GetMethod(nameof(WaitStartLonger),
                        BindingFlags.Static | BindingFlags.NonPublic);
                    context.GetPattern(waitStart).Prefixes.Add(prefix);
                    Log.Info("SteamLoginPatch: Applied WaitStart timeout increase.");
                }
                else
                {
                    Console.WriteLine("SteamLoginPatch: WaitStart not found. Skipping timeout patch.");
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "SteamLoginPatch failed during patch application.");
            }
        }

        /// <summary>
        /// Prefix to override Steam login using a GSLT if provided.
        /// </summary>
        private static bool Prefix()
        {
            var token = TorchBase.Instance?.Config?.LoginToken;
            if (string.IsNullOrEmpty(token))
            {
                Log.Info("SteamLoginPatch: No GSLT provided; continuing with anonymous login.");
                return true;
            }

            Log.Info("SteamLoginPatch: Logging in to Steam with GSLT...");

            var steamClient = new SteamClient();
            var gameServer = steamClient.GetHandler<SteamGameServer>();

            gameServer.LogOn(new SteamGameServer.LogOnDetails
            {
                Token = token
            });

            // Skip the original login function.
            return false;
        }

        /// <summary>
        /// Increases startup wait timeout.
        /// </summary>
        private static void WaitStartLonger(ref int timeOut)
        {
            timeOut = 20000; // 20 seconds
            Log.Debug("SteamLoginPatch: Increased WaitStart timeout to 20 seconds.");
        }
    }
}
