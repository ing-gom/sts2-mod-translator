# STS2 Mod Translator

A Slay the Spire 2 mod that lets you **translate other mods' text into any language** — right inside the game, without rebuilding them.

It reads the localized text of your other installed mods and injects your own translations back at runtime: cards, relics, powers, characters and more, even for mods that don't ship your language.

## How it works

STS2 stores text in named localization tables (`LocManager` / `LocTable`). Mods that ship a standard `localization/<lang>/*.json` folder register their text into those tables. This mod:

1. Scans loaded mods and extracts the text of any mod that ships a localization folder.
2. Writes editable template files (`overrides/`) plus a read-only `source/` reference of the original text.
3. On every language load, merges your non-empty translations back into the live tables (applied **last**, so it always wins — no load-order requirement).

Mods that hardcode text outside the localization system are listed as *unsupported*.

## Usage

1. Enable the mod and launch the game in the language you want to translate into.
2. Open **Main Menu ▸ Mod Translator** (next to Settings).
3. Pick a **mod** ▸ a **language** ▸ a **file**.
4. Translate in the built-in editor, **Upload** a JSON file, or **Open Folder** to edit externally.
5. Press **Save** — it validates the JSON and applies instantly.

### In-game editor

- Side-by-side panes: original (read-only) on the left, your translation on the right.
- Reference-language toggle: if a mod already ships other languages, switch the left pane to compare against them.
- Line numbers + JSON validation on save.

### Files

Translations live under the mod's `Translations/` folder (use **Open Folder**), with a fallback to `%APPDATA%\Sts2ModTranslator\` if the mod folder isn't writable.

### Translation tips

Keep these intact and change only the human-readable text:

- Placeholders like `!D!`, `!B!`, `!Crit!` — numbers are substituted automatically.
- Markup such as `*keyword`, leading `#`, `/+`, and `[color]` tags.

Leave a value **empty** to keep the original (English) text for that entry.

## Build

Requires Slay the Spire 2 installed (path auto-discovered). Build with the .NET SDK:

```
dotnet build -c Release
```

The DLL + manifest are copied to `<STS2>/mods/Sts2ModTranslator/`.

## License

MIT — see [LICENSE](LICENSE). Author: inggom.
