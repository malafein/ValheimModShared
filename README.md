# ValheimModShared

Source shared by malafein's Valheim mods. It's added to each mod repo as a git submodule at
`Shared/` and compiles straight into that mod's DLL, so players install nothing extra.

- `Log.cs`: logging through the mod's BepInEx log source, with Debug filtered by BepInEx at runtime
  (define `STRIP_DEBUG_LOG` in a build to drop it).
- `Keybinds.cs`: shortcut matching (left and right modifiers kept distinct, exact or at-least), hover-prompt
  formatting, the `Player.TakeInput()` gate, and a warning when a shortcut collides with a
  vanilla binding.

## Rules for this repo

- Plain `.cs` files only. A `.csproj` here would be swept into every mod's build.
- Everything is `internal` and lives in `malafein.Valheim.Shared`.
- Don't reference a mod's `Plugin` class. Mods pass in what the library needs.

## Using it in a mod

```bash
git submodule add git@github.com:malafein/ValheimModShared.git Shared
```

```csharp
using malafein.Valheim.Shared;

// Plugin.Awake, after Config.Bind
Log.Init(Logger, () => DebugMode.Value);
Keybinds.Init(Config);
Keybinds.Add(SomeShortcut);
```

Clone a mod with `git clone --recursive`, or run `git submodule update --init` in an existing
clone. To pick up a newer version of this library in a mod:

```bash
git submodule update --remote Shared
git commit -am "update shared library"
```

Push this repo before pushing a mod that points at a new commit of it.

## License

GPL-3.0, same as the mods.
