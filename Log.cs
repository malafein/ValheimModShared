using System;
using BepInEx.Logging;

namespace malafein.Valheim.Shared
{
    // Routes every message through the mod's own BepInEx log source, so lines carry the
    // [Level : Mod Name] tag and honor BepInEx log filtering. Don't use ZLog: it writes to
    // the game's log without the mod's tag.
    //
    // Level rules:
    //   Warn/Error - a broken assumption (missing prefab, reflection miss). A handled
    //                gameplay state (item not in inventory, chest full) is Debug.
    //   Info       - sparse lifecycle breadcrumbs (loaded, registered).
    //   Debug      - everything else. Filtered by BepInEx unless the player enables it.
    internal static class Log
    {
        private static ManualLogSource s_logger;
        private static Func<bool> s_debugEnabled;

        // debugEnabled is checked on every Debug call, so a config toggle takes effect live.
        // Leave it null to pass every Debug message on to BepInEx.
        internal static void Init(ManualLogSource logger, Func<bool> debugEnabled = null)
        {
            s_logger = logger;
            s_debugEnabled = debugEnabled;
        }

        internal static void Info(string message) => Logger.LogInfo(message);
        internal static void Warn(string message) => Logger.LogWarning(message);
        internal static void Error(string message) => Logger.LogError(message);

        // Debug messages ship in every build by default; BepInEx filters them at runtime, and a
        // player can enable them in BepInEx.cfg for a bug report. A mod that must keep them out
        // of a build (e.g. spoilers) defines STRIP_DEBUG_LOG for it. This source compiles into
        // each mod, so the symbol is the mod's own build setting. Callers still build the
        // message string, so keep Debug lines out of per-frame code.
        internal static void Debug(string message)
        {
#if !STRIP_DEBUG_LOG
            if (s_debugEnabled == null || s_debugEnabled())
                Logger.LogDebug(message);
#endif
        }

        // Falls back to a source named after the mod's assembly if Init hasn't run yet,
        // so a message logged too early is still written somewhere.
        private static ManualLogSource Logger
        {
            get
            {
                if (s_logger == null)
                {
                    string name = typeof(Log).Assembly.GetName().Name;
                    s_logger = BepInEx.Logging.Logger.CreateLogSource(name);
                }
                return s_logger;
            }
        }
    }
}
