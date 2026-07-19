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
- **Empty-entry navigator:** a live count of how many entries are still blank, plus a **Next empty ▼** button that jumps straight to the next untranslated line. Handy for finding the few entries auto-fill skips (those whose source text is itself empty). The file list also shows an `◦ N empty` tag per file.

### Partially-translated (mixed-language) mods

Some mods ship an incomplete translation — most of their text is in one language but a chunk was never translated (e.g. a mostly-English mod with some cards still in Chinese). Normally the mod's own source language isn't offered as a target (there's nothing to translate). But when the source is detected as *mixed*, that language stays selectable and is tagged **"partly translated — mixed source"**: pick it to override just the foreign leftovers into the language you're playing, leaving the already-correct entries empty. The left reference pane and **Next empty ▼** help you find them, and DeepL auto-fill auto-detects each entry's language so only the untranslated ones are translated.

### Machine-translation drafts (DeepL)

To speed up a first pass, the editor can pre-fill **empty** entries with DeepL:

- Set your DeepL API key once (**Auto-translate…** on the mods list — the free tier works).
- **Auto-fill ✨** drafts the open file; **Auto-fill all ✨** drafts every file for the current language. Existing translations are never overwritten — only blanks are filled, as a draft to review.
- **Keyword accuracy:** highlighted game keywords (`Vulnerable`, `Block`, `Exhaust`, …) are corrected to the game's own official term in your language — extracted from the game's localization for 13 languages — so drafts match in-game wording even when the machine translation doesn't.
- **Placeholders** (`!D!`, `[color]…[/color]`, `{…}`) are protected so the translation can't corrupt them.

### BaseLib mods

Mods built on the **BaseLib** framework (e.g. FGO-based mods) author their text with a shorthand — `!Damage!`/`!D!` for values, `*keyword*` for gold highlights, `#` to opt a string in — which BaseLib rewrites to the game's native format *when it loads the mod's files*. Because this mod injects translations at runtime (bypassing that file-load step), injected text is now run through BaseLib's own converter just before it's applied, so `!Var!`/`*keyword*` render as numbers/highlights in-game instead of showing the raw shorthand. Non-BaseLib mods (and text already in `{…}` form) are unaffected; the step is a no-op when BaseLib isn't installed.

### Files

Translations live under the mod's `Translations/` folder (use **Open Folder**), with a fallback to `%APPDATA%\Sts2ModTranslator\` if the mod folder isn't writable.

## Sharing translations as a standalone mod

You can turn your work into its own distributable mod — like other mods reference **baselib**, a *translation mod* just references this one. Other players install your translation mod (plus this one) and the text is applied automatically, no editing required.

**Export from the editor:** pick a mod, then press **Export as mod**. It writes a ready-to-ship mod folder to `%APPDATA%\Sts2ModTranslator\exported\<id>_Translation\` containing a manifest and your non-empty translations for every language you've worked on. Drop that folder into `<STS2>/mods/` (or upload it to the Workshop) to share.

**Bundle several mods into one pack:** on the mods list press **Bundle pack…**. Tick the mods you want to include (only mods you've translated are selectable), give the pack a **name**, and press **Install pack to mods**. It writes a single translation mod — `<name>_Translations/` — whose `translations/` folder holds every ticked mod's text side by side. Your selection and pack name are remembered between sessions; re-installing under the same name updates it (and drops any mods you unticked). This is the easy way to ship one Workshop item that translates a whole set of mods at once.

**Updating a pack you already deployed:** you don't have to re-tick everything. The builder shows an **Existing packs ▾** dropdown listing every bundle pack already installed in your mods folder. Pick one and it re-ticks that pack's mods, fills in its name, and suggests the next version — then just press **Update installed pack**. (Mods that aren't currently loaded/translated show up as unavailable and would be dropped on re-install, so you're warned.)

**Or author one by hand** — a translation mod needs no DLL, just data:

```
<YourTranslationModId>/
  <YourTranslationModId>.json          # manifest, see below
  translations/
    <targetModId>/                     # the mod you're translating
      <lang>/                          # e.g. kor, zhs, jpn
        <table>.txt                    # JSON content: { "loc_key": "translated text", ... }
```

> Data files use a `.txt` extension (their content is still JSON). Only the manifest is `.json` — this keeps the game's mod loader from trying to parse each translation file as a manifest and logging a harmless "missing id" warning for it at boot. Legacy `.json` data files are still read for backward compatibility.

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
