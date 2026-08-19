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
2. Open **Main Menu ▸ Mod Translator** (next to Settings) — or, without leaving a run, **Pause ▸ Mod Translator**.
3. Pick a **mod** ▸ a **language** ▸ a **file**.
4. Translate in the built-in editor, **Upload** a JSON file, or **Open Folder** to edit externally.
5. Press **Save** — it validates the JSON and applies instantly.

### In-game editor

- Side-by-side panes: original (read-only) on the left, your translation on the right.
- Reference-language toggle: if a mod already ships other languages, switch the left pane to compare against them.
- **The reference follows you:** move the caret and the left pane scrolls to the same entry and highlights it, with the key you're on named in the header. It matches by key, not line number, so it stays correct even when the reference language translated only part of the file — and says *not in this reference* when the key is missing there entirely.
- **Find box:** spotted an awkward line while playing? Type it — or one distinctive word — in **Find** and it jumps to that entry. It searches the **original**, your **translation** and the **key**, so it finds entries you haven't translated yet as well as ones you have. Color tags (`[color=…]`) and placeholders (`!D!`, `{…}`) are ignored on both sides, so you can paste what the game actually showed you. Every occurrence is **highlighted in both panes** as you type — original and translation — and the jump selects the one you're on, so you can see at a glance whether the phrase appears once or a dozen times. Enter (or **Ctrl+F** from the editor) cycles the matches; the header shows how many there are and the reference pane follows along.
- Line numbers + JSON validation on save.
- **Empty-entry navigator:** a live count of how many entries are still blank, plus a **Next empty ▼** button that jumps straight to the next untranslated line. The file list also shows an `◦ N empty` tag per file. Entries whose original text is itself empty are left out of the count — there is nothing to translate in them, so they never stand between you and 100%.

### Fixing a line without leaving your run

The editor also opens from the **pause menu**, so the loop of *spot an awkward line while playing → fix it → keep going* costs you nothing but a pause. Save applies the change to the live text immediately — no restart, no rebuild, and the run is untouched.

While the panel is open the game's hotkeys are suspended, so typing a translation can't leak into the game as a card play or an end-turn; they come back the moment it closes.

**Esc** resumes the run and tucks the panel away with the pause menu — it doesn't discard what you were editing. Pause again and the panel is right where you left it, text and all. Unsaved text still only lives in the box, so press **Save** to apply it.

### Editing a mod's own text / leftover foreign lines

Open any mod and the language list starts with an **✎ edit this mod's own text** row for the language the mod already ships (e.g. English for a mostly-English mod). Pick it to override that text — handy when a mod is *mostly* translated but a few cards or events are still in another language, or when a machine-translated mod reads roughly. Only the entries you fill in are applied over the mod's existing text; blanks keep it as-is. Because it's an override rather than a translation, this row shows an edit count instead of a percentage, and the mod list stays clean (an already-English mod isn't shown as "0% translated").

*(This row edits whatever language the mod's source folder is written in — it doesn't assume that language is the mod's "original". If a mod also ships another language you'd rather edit, that language is just a normal row lower in the list.)*

If a mod's source folder actually *mixes* two languages (e.g. an English folder with some entries still in Chinese, or a Chinese mod with a few Korean/Japanese lines left over), it's tagged **"partly translated — mixed source"**, its row reads *"fill the foreign leftovers"*, and DeepL auto-fill auto-detects each entry's language so only the untranslated ones are translated. Detection is per-entry and language-aware — it catches CJK↔Latin mixes as well as CJK↔CJK (Chinese vs Korean/Japanese), and flags as soon as two or more entries are in a different language than the rest (ratio doesn't matter). The left reference pane and **Next empty ▼** help you find them.

### When a mod updates

Mods change. When the mod you translated ships a new version, its row is tagged **⚠ out of sync**, but that only tells you *something* moved — not *what*. This mod also tracks, per entry, the exact original text you translated each line **from**. When a mod update rewrites some card or event, only those specific entries are flagged **source-changed** — everything you translated that still matches is left alone.

- The file list shows `⚠ N source-changed` per file; open one and a **Next changed ▼** button jumps straight to each entry whose original moved. The status bar shows a **word-level diff** of what changed (`Deal ⟨5→8⟩ damage.`) and the left reference pane shows the current original — so you can see exactly which word to fix, not just that *something* did.
- Re-translate the entry and save, and the flag clears for that line. Editing an unrelated line never clears another line's flag.
- This is forward-looking: it starts tracking from the version you have installed now, so a mod that updates *after* you translate it is caught precisely, entry by entry.

### Mod glossary (keeping terms consistent)

The built-in keyword glossary already pins the game's own terms (`Vulnerable` → the official word in your language). For a mod's **own** terms — character names, unique mechanics — press **Glossary…** on the file list and add lines like `Artoria = 아르토리아`. Any entry whose original uses a term but whose translation doesn't use your wording is flagged, with a **Next term ▼** navigator and an inline hint in the reference header telling you the term to use. It's advisory (it never rewrites your text) and works no matter how the translation was made — DeepL, an AI agent, or by hand. The glossary lives in the workspace, so the AI agent picks it up automatically. Local-only; it isn't bundled into exported packs.

### Machine-translation drafts (DeepL)

To speed up a first pass, the editor can pre-fill **empty** entries with DeepL:

- Set your DeepL API key once (**Auto-translate…** on the mods list — the free tier works).
- **Auto-fill ✨** drafts the open file; **Auto-fill all ✨** drafts every file for the current language. Existing translations are never overwritten — only blanks are filled, as a draft to review.
- **Keyword accuracy:** highlighted game keywords (`Vulnerable`, `Block`, `Exhaust`, …) are corrected to the game's own official term in your language — extracted from the game's localization for 13 languages — so drafts match in-game wording even when the machine translation doesn't.
- **Placeholders** (`!D!`, `[color]…[/color]`, `{…}`) are protected so the translation can't corrupt them.

### Translate with an AI agent

DeepL translates entry by entry. An AI coding agent (Claude Code and similar) can read a
whole file at once — so it keeps terminology consistent across a mod and handles the
syntax DeepL has to skip.

The `Translations/` folder sets one up for you. There is no path to paste and no format
to explain — everything the agent needs is already in the folder:

- **`.claude/skills/translate-mod/SKILL.md`** — the rules: which files to edit, what an
  empty value means, which placeholders must survive untouched, and how many
  `{X:plural:…}` branches your target language actually needs. Agents pick this up on
  their own when run from this folder.
- **`glossary_<lang>.txt`** — the game's own official term for every keyword in your
  language, so the agent writes the wording players already know instead of inventing a
  synonym for *Vulnerable* or *Exhaust*.
- **`supported_mods.txt`** — the work list: each mod, its tables, and current coverage.

Press **Translate with AI…** on the mods list — it copies the folder path and shows the
three steps (open a terminal there, run your agent, ask it to translate). Press
**Reload** when the agent finishes to apply the result.

Both files are rewritten on every launch, so they always match the installed version and
your current language, and they come back on their own if a Workshop update wipes them.

> Why this matters for quality: `{Amount:plural:a card|[blue]{}[/blue] cards}` holds
> translatable text *inside* the placeholder. Machine translation masks the whole thing
> and leaves those branches in English. The rules file tells the agent to translate
> inside the branches — and that Korean, Japanese, Chinese and Thai must end up with
> exactly **one** branch, because the game always renders the first one for those
> languages.

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
