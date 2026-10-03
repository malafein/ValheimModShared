using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace malafein.Valheim.Shared
{
    internal enum ModifierMatch
    {
        // Every listed modifier held, and no unlisted Shift/Ctrl/Alt, so E, Left Shift + E and
        // Left Alt + E stay distinct.
        Exact,

        // Every listed modifier held; extra held modifiers are ignored, so a binding still
        // fires while sprinting with Left Shift held.
        AtLeast
    }

    // Shortcuts are read through ZInput, Valheim's own input layer, which Valheim 1.0 builds
    // on Unity 6's Input System. BepInEx's KeyboardShortcut.IsDown() also still works, but
    // ZInput keeps key handling on the same mapping the game uses, which the conflict check
    // below relies on.
    //
    // Usage, in Plugin.Awake after Config.Bind:
    //   Keybinds.Init(Config, "Use");                  // vanilla buttons shared on purpose
    //   Keybinds.Add(RenameShipKey);
    //   Keybinds.Add("Slot 1", () => slot.Value.Shortcut);
    [HarmonyPatch]
    internal static class Keybinds
    {
        private class Binding
        {
            public string Name;
            public Func<KeyboardShortcut> Shortcut;
            public string Context;
        }

        private const string DefaultContext = "";

        private static readonly MethodInfo TakeInputMethod = AccessTools.Method(typeof(Player), "TakeInput");
        private static readonly FieldInfo ButtonsField = AccessTools.Field(typeof(ZInput), "m_buttons");
        private static readonly MethodInfo KeyCodeToPathMethod = AccessTools.Method(typeof(ZInput), "KeyCodeToPath");

        // Left and right are separate modifiers, as in the game itself: vanilla crouches on Left
        // Ctrl and sprints on Left Shift, leaving the right-hand keys free for shortcuts. Listed
        // in the order Format shows them.
        private static readonly KeyCode[] ModifierKeys =
        {
            KeyCode.LeftControl,
            KeyCode.RightControl,
            KeyCode.LeftShift,
            KeyCode.RightShift,
            KeyCode.LeftAlt,
            KeyCode.RightAlt
        };

        private static readonly Dictionary<KeyCode, string> ModifierLabels = new Dictionary<KeyCode, string>
        {
            { KeyCode.LeftControl, "Left Ctrl" },
            { KeyCode.RightControl, "Right Ctrl" },
            { KeyCode.LeftShift, "Left Shift" },
            { KeyCode.RightShift, "Right Shift" },
            { KeyCode.LeftAlt, "Left Alt" },
            { KeyCode.RightAlt, "Right Alt" }
        };

        // ZInput registers each mouse button under a raw name as well as under the actions
        // bound to it. Nothing in the game reads these names, so sharing them isn't a conflict;
        // a real action on the same button (Attack, Block) is still reported.
        private static readonly string[] RawMouseButtons =
        {
            "MouseLeft",
            "MouseRight",
            "MouseMiddle",
            "MouseForward",
            "MouseBack"
        };

        private static readonly List<Binding> s_bindings = new List<Binding>();
        private static readonly HashSet<string> s_ignoredVanillaButtons = new HashSet<string>(RawMouseButtons);
        private static List<string> s_lastConflicts = new List<string>();

        // Re-checks conflicts whenever any setting in the mod's config changes. The check is
        // cheap and only logs when the set of conflicts changes, so there's no need to know
        // which settings are shortcuts. ignoredVanillaButtons are ZInput button names (e.g.
        // "Use") that share a key with the mod's shortcuts on purpose.
        internal static void Init(ConfigFile config, params string[] ignoredVanillaButtons)
        {
            s_ignoredVanillaButtons.UnionWith(ignoredVanillaButtons);
            config.SettingChanged += (sender, args) => CheckConflicts();
        }

        // Registers a shortcut for the conflict check. Shortcuts that share a context run at
        // the same time, so identical ones are reported; leave context null when there's
        // only one.
        internal static void Add(ConfigEntry<KeyboardShortcut> entry, string context = null)
        {
            Add(entry.Definition.Key, () => entry.Value, context);
        }

        // For shortcuts stored inside another config type. Return KeyboardShortcut.Empty
        // while the binding is inactive.
        internal static void Add(string name, Func<KeyboardShortcut> shortcut, string context = null)
        {
            s_bindings.Add(new Binding { Name = name, Shortcut = shortcut, Context = context ?? DefaultContext });

            // No-op until ZInput exists; the ZInput.Load postfix covers the normal startup order.
            CheckConflicts();
        }

        internal static bool IsDown(KeyboardShortcut shortcut, ModifierMatch match = ModifierMatch.Exact)
        {
            KeyCode mainKey = shortcut.MainKey;

            // ZInput rejects Mouse5/Mouse6 and anything above JoystickButton19.
            if (mainKey == KeyCode.None || !ZInput.IsKeyCodeValid(mainKey)) return false;
            if (!ZInput.GetKeyDown(mainKey, false)) return false;

            if (!shortcut.Modifiers.All(key => ZInput.GetKey(key, false))) return false;

            if (match == ModifierMatch.AtLeast) return true;

            return ModifierKeys.All(key => shortcut.Modifiers.Contains(key) || !ZInput.GetKey(key, false));
        }

        // The Shift/Ctrl/Alt keys held right now, by side. For capturing a new binding: Unity's
        // GUI events only report that a modifier is held, not which side.
        internal static KeyCode[] HeldModifiers()
        {
            return ModifierKeys.Where(key => ZInput.GetKey(key, false)).ToArray();
        }

        // Player.TakeInput() is false while chat, menus, or the inventory have focus. The Input
        // System reads raw keys regardless of UI focus, so without this gate typing a capital E
        // in chat would trigger Left Shift + E. It's protected, so it's reached by reflection.
        internal static bool CanTakeInput(Player player)
        {
            return (bool)TakeInputMethod.Invoke(player, null);
        }

        // "Left Shift + E" style text for hover prompts, so they always show the configured keys.
        internal static string Format(KeyboardShortcut shortcut)
        {
            if (shortcut.MainKey == KeyCode.None) return "Not set";

            var parts = new List<string>();
            parts.AddRange(ModifierKeys.Where(key => shortcut.Modifiers.Contains(key)).Select(key => ModifierLabels[key]));
            parts.AddRange(shortcut.Modifiers.Where(key => !ModifierLabels.ContainsKey(key)).Select(key => key.ToString()));
            parts.Add(shortcut.MainKey.ToString());
            return string.Join(" + ", parts);
        }

        // Some gamepad layouts register placeholder buttons through ZInput.AddUnusedButton
        // (JoyTabLeft, JoyTabRight) with an empty InputAction. ButtonDef.GetActionPath() indexes
        // bindings[0] unguarded, so it throws for them. They have no key, so they can't conflict.
        private static string GetBoundPath(ZInput.ButtonDef button)
        {
            try
            {
                return button.GetActionPath();
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        // ZInput.Load applies saved rebinds at startup, Save runs when the player saves the
        // controls menu, and ResetToDefault restores vanilla bindings. Re-check after each.
        // The startup Load is called from inside the ZInput constructor, before ZInput.instance
        // is assigned, so the hooks pass along the instance being patched.
        [HarmonyPatch(typeof(ZInput), nameof(ZInput.Load))]
        [HarmonyPostfix]
        private static void Postfix_ZInputLoad(ZInput __instance)
        {
            CheckConflicts(__instance);
        }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.Save))]
        [HarmonyPostfix]
        private static void Postfix_ZInputSave(ZInput __instance)
        {
            CheckConflicts(__instance);
        }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.ResetToDefault))]
        [HarmonyPostfix]
        private static void Postfix_ZInputResetToDefault(ZInput __instance)
        {
            CheckConflicts(__instance);
        }

        // Warns when a shortcut's main key is also bound to a vanilla action (vanilla mostly
        // ignores modifiers, so Shift + F would still fire the Forsaken power on F), when it
        // uses a key Valheim cannot read, or when two shortcuts in the same context are
        // identical. Only logs when the set of conflicts changes.
        internal static void CheckConflicts(ZInput input = null)
        {
            // These checks run inside vanilla ZInput.Load/Save/ResetToDefault and config change
            // events. A failure here must never escape into the game's input handling, where it
            // could stop saved key bindings from loading.
            try
            {
                FindAndLogConflicts(input ?? ZInput.instance);
            }
            catch (Exception e)
            {
                Log.Warn($"Keybinding conflict check failed: {e.Message}");
            }
        }

        private static void FindAndLogConflicts(ZInput input)
        {
            if (input == null || ButtonsField == null || KeyCodeToPathMethod == null) return;
            if (!(ButtonsField.GetValue(input) is Dictionary<string, ZInput.ButtonDef> buttons)) return;

            var conflicts = new List<string>();
            var active = s_bindings
                .Select(b => new { Binding = b, Shortcut = b.Shortcut() })
                .Where(b => b.Shortcut.MainKey != KeyCode.None)
                .ToList();

            foreach (var item in active)
            {
                Binding binding = item.Binding;
                KeyboardShortcut shortcut = item.Shortcut;
                KeyCode mainKey = shortcut.MainKey;
                if (!ZInput.IsKeyCodeValid(mainKey))
                {
                    conflicts.Add($"{binding.Name} ({Format(shortcut)}) uses {mainKey}, which Valheim cannot read. The shortcut will never fire.");
                    continue;
                }

                string path = (string)KeyCodeToPathMethod.Invoke(null, new object[] { mainKey, false });
                IEnumerable<string> clashes = buttons.Values
                    .Where(b => !s_ignoredVanillaButtons.Contains(b.Name))
                    .Where(b => string.Equals(GetBoundPath(b), path, StringComparison.OrdinalIgnoreCase))
                    .Select(b => b.Name)
                    .Distinct();

                foreach (string button in clashes)
                {
                    conflicts.Add($"{binding.Name} ({Format(shortcut)}) shares {mainKey} with the game's \"{button}\" binding. Both will trigger.");
                }
            }

            var duplicates = active
                .GroupBy(b => new { b.Binding.Context, Keys = Format(b.Shortcut) })
                .Where(g => g.Count() > 1);

            foreach (var group in duplicates)
            {
                string names = string.Join(" and ", group.Select(b => b.Binding.Name));
                conflicts.Add($"{names} are bound to the same keys ({group.Key.Keys}). They will trigger at once.");
            }

            if (conflicts.SequenceEqual(s_lastConflicts)) return;

            foreach (string conflict in conflicts)
            {
                Log.Warn($"Keybinding conflict: {conflict}");
            }
            if (conflicts.Count == 0 && s_lastConflicts.Count > 0)
            {
                Log.Info("Keybinding conflicts resolved.");
            }
            s_lastConflicts = conflicts;
        }
    }
}
