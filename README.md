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

## Sharing translations as a standalone mod

You can turn your work into its own distributable mod — like other mods reference **baselib**, a *translation mod* just references this one. Other players install your translation mod (plus this one) and the text is applied automatically, no editing required.

**Export from the editor:** pick a mod, then press **Export as mod**. It writes a ready-to-ship mod folder to `%APPDATA%\Sts2ModTranslator\exported\<id>_Translation\` containing a manifest and your non-empty translations for every language you've worked on. Drop that folder into `<STS2>/mods/` (or upload it to the Workshop) to share.

**Or author one by hand** — a translation mod needs no DLL, just data:

```
<YourTranslationModId>/
  <YourTranslationModId>.json          # manifest, see below
  translations/
    <targetModId>/                     # the mod you're translating
      <lang>/                          # e.g. kor, zhs, jpn
        <table>.json                   # { "loc_key": "translated text", ... }
```

The manifest must depend on this mod so the translations get applied:

```json
{
  "id": "MyDownfallKorean",
  "name": "Downfall — Korean",
  "version": "1.0.0",
  "has_dll": false,
  "dependencies": ["Sts2ModTranslator"],
  "affects_gameplay": false
}
```

Installed translation mods are detected at boot, shown in the panel (marked *translation pack installed*), and merged into the game last — so your own in-editor translations still take priority over a pack, and a pack takes priority over the original text.

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
