using System;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using Sts2ModTranslator.Core;

namespace Sts2ModTranslator.Ui;

/// <summary>
/// 메인 메뉴에 'Mod Translator' 항목(네이티브 SettingsButton 복제)을 추가하고,
/// 클릭 시 드릴다운 패널을 연다: 모드 목록 → 언어 → 파일(편집/업로드).
/// 우측 상단 X 로 닫기. 배경 dim 없음(불투명 패널).
/// </summary>
public static class TranslatorPanel
{
    private static readonly Color GOLD = new(0.93f, 0.77f, 0.40f);
    private static readonly Color WHITE = new(0.93f, 0.94f, 0.97f);
    private static readonly Color GRAY = new(0.60f, 0.63f, 0.72f);
    private static readonly Color RED = new(0.92f, 0.45f, 0.45f);

    private enum View { Mods, Languages, Files, Editor }

    private static Control? _root;
    private static Label? _title;
    private static Button? _back;
    private static VBoxContainer? _content;
    private static TextEdit? _editor;
    private static Label? _status;

    private static View _view = View.Mods;
    private static SupportedMod? _mod;
    private static string _lang = "";
    private static string _table = "";

    // ── 메인 메뉴 항목 ──────────────────────────────────────
    public static void Attach(NMainMenu menu) =>
        Callable.From(() => DoAttach(menu)).CallDeferred();

    private static void DoAttach(NMainMenu menu)
    {
        try
        {
            if (menu == null || !GodotObject.IsInstanceValid(menu)) return;
            if (menu.HasNode("MainMenuTextButtons/Sts2ModTranslatorButton")) return;

            TranslationSync.EnsureUiStrings(LocManager.Instance);

            var settingsBtn = menu.GetNodeOrNull<NMainMenuTextButton>("MainMenuTextButtons/SettingsButton");
            if (settingsBtn == null)
            {
                MainFile.Logger.Warn("[Sts2ModTranslator] SettingsButton 미발견 — 메뉴 항목 skip.");
                return;
            }

            var btn = (NMainMenuTextButton)settingsBtn.Duplicate(14); // signals(1) 제외
            btn.Name = "Sts2ModTranslatorButton";
            settingsBtn.AddSibling(btn, false);
            btn.SetLocalization(TranslationSync.MenuLabelKey);
            btn.Released += _ => ShowPanel();

            var min = btn.CustomMinimumSize;
            btn.CustomMinimumSize = new Vector2(Math.Max(300f, min.X), min.Y);
            var self = new NodePath(".");
            btn.FocusNeighborLeft = self;
            btn.FocusNeighborRight = self;

            _root = BuildPanel();
            _root.Visible = false;
            menu.AddChild(_root);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Sts2ModTranslator] menu 항목 추가 실패: {ex.Message}");
        }
    }

    private static void ShowPanel()
    {
        if (_root == null || !GodotObject.IsInstanceValid(_root)) return;
        _root.Visible = true;
        Navigate(View.Mods);
    }

    private static void Hide()
    {
        if (_root != null && GodotObject.IsInstanceValid(_root)) _root.Visible = false;
    }

    // ── 패널 골격 ───────────────────────────────────────────
    private static Control BuildPanel()
    {
        // dim 없음: 투명 root 가 클릭만 가로챔
        var root = new Control { Name = "Sts2ModTranslatorPanel", MouseFilter = Control.MouseFilterEnum.Stop };
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        // 화면 거의 가득 채우는 큰 패널(가장자리 여백만). 해상도에 따라 자동 스케일.
        var panel = new PanelContainer();
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        panel.OffsetLeft = 80; panel.OffsetTop = 56;
        panel.OffsetRight = -80; panel.OffsetBottom = -56;
        // 기본 테마 패널이 반투명이라 뒤 메뉴가 비침 → 불투명 StyleBox 로 오버라이드.
        var sb = new StyleBoxFlat { BgColor = new Color(0.075f, 0.086f, 0.125f, 1.0f) };
        sb.SetBorderWidthAll(2);
        sb.BorderColor = new Color(0.30f, 0.34f, 0.46f, 1.0f);
        sb.SetCornerRadiusAll(10);
        sb.SetContentMarginAll(0);
        panel.AddThemeStyleboxOverride("panel", sb);
        root.AddChild(panel);

        var margin = new MarginContainer();
        foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(s, 20);
        panel.AddChild(margin);

        var vbox = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        margin.AddChild(vbox);

        // 헤더: [Back] [Title ....] [X]
        var header = new HBoxContainer();
        _back = new Button { Text = "←", CustomMinimumSize = new Vector2(44, 40) };
        _back.AddThemeFontSizeOverride("font_size", 22);
        _back.Pressed += GoBack;
        header.AddChild(_back);

        _title = new Label { Text = "STS2 Mod Translator", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _title.AddThemeFontSizeOverride("font_size", 26);
        _title.AddThemeColorOverride("font_color", GOLD);
        header.AddChild(_title);

        var x = new Button { Text = "X", CustomMinimumSize = new Vector2(40, 40) };
        x.AddThemeFontSizeOverride("font_size", 20);
        x.Pressed += Hide;
        header.AddChild(x);
        vbox.AddChild(header);

        _status = new Label { Text = "", Visible = false };
        _status.AddThemeFontSizeOverride("font_size", 16);
        vbox.AddChild(_status);

        vbox.AddChild(new HSeparator());

        _content = new VBoxContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        vbox.AddChild(_content);

        return root;
    }

    // ── 네비게이션 ──────────────────────────────────────────
    private static void Navigate(View v)
    {
        _view = v;
        if (_back != null) _back.Visible = v != View.Mods;
        SetStatus("", false);
        if (_title != null)
            _title.Text = v switch
            {
                View.Mods => $"STS2 Mod Translator   —   target: {TranslationSync.CurrentLanguage()}",
                View.Languages => _mod?.Name ?? "",
                View.Files => $"{_mod?.Name}  /  {_lang}",
                View.Editor => $"{_mod?.Name}  /  {_lang}  /  {_table}.json",
                _ => "STS2 Mod Translator",
            };
        RebuildContent();
    }

    private static void GoBack()
    {
        switch (_view)
        {
            case View.Languages: Navigate(View.Mods); break;
            case View.Files: Navigate(View.Languages); break;
            case View.Editor: Navigate(View.Files); break;
        }
    }

    private static void RebuildContent()
    {
        if (_content == null) return;
        foreach (var c in _content.GetChildren()) c.QueueFree();
        _editor = null;

        switch (_view)
        {
            case View.Mods: BuildMods(); break;
            case View.Languages: BuildLanguages(); break;
            case View.Files: BuildFiles(); break;
            case View.Editor: BuildEditor(); break;
        }
    }

    // ── 뷰: 모드 목록 ───────────────────────────────────────
    private static void BuildMods()
    {
        var scan = TranslationSync.CurrentScan;
        if (scan == null) { _content!.AddChild(Lbl("No mods scanned yet.", GRAY)); return; }

        var list = ScrollList();
        foreach (var m in scan.Supported.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            var mod = m;
            // 설치된 번역 모드가 이 대상을 번역 중이면 언어별 커버리지(%)를 표시(런타임 자동 적용).
            string pack = PackTagForTarget(scan, m.Id);
            var b = RowButton($"{m.Name}     [{string.Join(", ", m.ShipsLangs)}]{pack}");
            if (pack.Length > 0) b.AddThemeColorOverride("font_color", GOLD);
            b.Pressed += () => { _mod = mod; Navigate(View.Languages); };
            ListVBox(list).AddChild(b);
        }

        // 설치된 팩은 위 대상 행의 'pack: 언어 %' 태그로 표시. 내용은 모드 ▸ Edit 의 Reference 에서 본다.
        if (scan.Bundled.Any)
            ListVBox(list).AddChild(Lbl(
                $"({scan.Bundled.Providers.Count} translation pack(s) installed — applied automatically. "
                + "Open a mod ▸ Edit, then pick \"pack: <lang>\" in Reference to view its text.)", GOLD));
        if (scan.Unsupported.Count > 0)
            ListVBox(list).AddChild(Lbl($"({scan.Unsupported.Count} unsupported mods — no localization folder)", GRAY));

        // 하단 액션
        var footer = new HBoxContainer();
        var of = ActionButton("Open Folder"); of.Pressed += OpenFolder;
        var rl = ActionButton("Reload"); rl.Pressed += () => { int n = TranslationSync.ReloadFromDisk(); SetStatus($"Reloaded {n} keys.", true, false); };
        footer.AddChild(of); footer.AddChild(rl);
        _content!.AddChild(footer);
    }

    // ── 뷰: 언어 목록 ───────────────────────────────────────
    private static void BuildLanguages()
    {
        if (_mod == null) { Navigate(View.Mods); return; }

        string cur = TranslationSync.CurrentLanguage();
        // 게임 전체 지원 언어 ∪ 모드 동봉 언어. eng 는 원문(번역 대상 아님)이라 제외.
        // 현재 설정 언어를 맨 위(기본 선택)로, 나머지는 게임 선언 순서를 유지(OrderBy 안정 정렬).
        var langs = TranslationSync.SupportedLanguages()
            .Concat(_mod.ShipsLangs)
            .Where(l => !string.Equals(l, "eng", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => string.Equals(l, cur, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();

        // 현재 언어만 인게임 즉시 반영, 나머지는 그 언어로 전환 후 적용됨을 안내.
        _content!.AddChild(Lbl(
            "Pick any language to translate. The current game language applies instantly; "
            + "others apply after you switch the game to that language.", GRAY));

        var list = ScrollList();
        foreach (var lang in langs)
        {
            var l = lang;
            var (tot, tr) = TranslationStore.Coverage(_mod, l);
            int pct = tot == 0 ? 0 : (int)Math.Round(100.0 * tr / tot);
            bool isCurrent = string.Equals(l, cur, StringComparison.OrdinalIgnoreCase);
            string tag = isCurrent ? "  ◀ current" : "";
            var b = RowButton($"{l}     {pct}%   ({tr}/{tot}){tag}");
            if (isCurrent) b.AddThemeColorOverride("font_color", GOLD);
            b.Pressed += () =>
            {
                _lang = l;
                TranslationStore.EnsureTemplates(_mod, l); // override 스켈레톤 보장
                Navigate(View.Files);
            };
            ListVBox(list).AddChild(b);
        }

        // 하단 액션: 이 모드의 번역을 배포 가능한 독립 "번역 모드" 로 내보내기 (워크샵 친화).
        var mod = _mod;
        var box = new VBoxContainer();

        // 1) 번역가(매니페스트 author) 입력 — 세션 간 유지.
        var authorRow = new HBoxContainer();
        authorRow.AddChild(Lbl("Author:", GRAY));
        _authorEdit = new LineEdit
        {
            Text = TranslationStore.LoadAuthor(),
            PlaceholderText = "your name (becomes the mod's author)",
            CustomMinimumSize = new Vector2(300, 36),
        };
        authorRow.AddChild(_authorEdit);
        box.AddChild(authorRow);

        // 2) 설치 버튼 — 번역을 독립 모드로 게임 mods\ 에 생성(로드·테스트·창작마당 업로드의 입력).
        var footer = new HBoxContainer();
        var install = ActionButton("Install as mod");
        install.CustomMinimumSize = new Vector2(190, 40);
        install.Pressed += () => InstallToGameMods(mod);
        footer.AddChild(install);
        box.AddChild(footer);

        box.AddChild(Lbl(
            "Creates a standalone translation mod in the game's mods folder. Restart to load & test it; "
            + "then upload it to the Workshop. To share the file directly, zip that folder.", GRAY));
        _content!.AddChild(box);
    }

    // ── 번역 모드 내보내기 ──────────────────────────────────
    private static LineEdit? _authorEdit;

    /// <summary>author 입력칸 값을 읽어 저장하고 반환(다음 내보내기에 재사용).</summary>
    private static string CurrentAuthor()
    {
        string a = (_authorEdit?.Text ?? "").Trim();
        TranslationStore.SaveAuthor(a);
        return a;
    }

    // 게임 mods\ 에 바로 내보내 곧장 로드/테스트 + 워크샵 업로드 대시보드가 '설치됨'으로 인식.
    private static void InstallToGameMods(SupportedMod mod)
    {
        string? mods = TranslationStore.GameModsDir;
        if (string.IsNullOrEmpty(mods))
        {
            SetStatus("Could not locate the game's mods folder.", true, true);
            return;
        }
        var (ok, path, err) = TranslationStore.ExportMod(mod, CurrentAuthor(), mods);
        if (!ok) { SetStatus("Install failed: " + err, true, true); return; }
        SetStatus($"Installed -> {path}.  Restart the game to load it.", true, false);
        try { OS.ShellShowInFileManager(path); }
        catch (Exception ex) { MainFile.Logger.Warn($"[Sts2ModTranslator] install open 실패: {ex.Message}"); }
    }

    /// <summary>
    /// 설치된 팩이 (대상, 언어)에 대해 번역한 키 수와 대상 모드의 전체 번역 가능 키 수.
    /// total=0 이면 대상의 eng 기준이 없어(런타임 등록 모드 등) % 산정 불가 — 제공 키 수만 의미.
    /// </summary>
    private static (int translated, int total) PackCoverage(ScanResult scan, string targetId, string lang)
    {
        var byTable = scan.Bundled.ForTargetLang(targetId, lang);
        var sm = scan.Supported.FirstOrDefault(s => s.Id == targetId);
        if (byTable == null) return (0, sm?.TotalKeys ?? 0);
        if (sm == null) return (byTable.Values.Sum(d => d.Count), 0); // eng 기준 없음 — 제공 키만
        int tr = 0;
        foreach (var (table, dict) in byTable)
        {
            if (!sm.EngByTable.TryGetValue(table, out var eng)) continue;
            tr += dict.Count(kv => eng.ContainsKey(kv.Key) && !string.IsNullOrEmpty(kv.Value));
        }
        return (tr, sm.TotalKeys);
    }

    /// <summary>"kor 87%" 또는 (기준 없을 때) "kor".</summary>
    private static string LangPct(ScanResult scan, string targetId, string lang)
    {
        var (tr, total) = PackCoverage(scan, targetId, lang);
        return total > 0 ? $"{lang} {(int)Math.Round(100.0 * tr / total)}%" : lang;
    }

    /// <summary>한 대상 모드에 설치된 팩의 언어별 커버리지 태그. 없으면 "".</summary>
    private static string PackTagForTarget(ScanResult scan, string targetId)
    {
        var langs = scan.Bundled.LangsForTarget(targetId);
        return langs.Count == 0 ? "" : "   ◆ pack: " + string.Join(", ", langs.Select(l => LangPct(scan, targetId, l)));
    }

    // ── 뷰: 파일(테이블) 목록 ───────────────────────────────
    private static void BuildFiles()
    {
        if (_mod == null) { Navigate(View.Mods); return; }
        var list = ScrollList();
        int problems = 0;
        foreach (var table in _mod.EngByTable.Keys.OrderBy(t => t, StringComparer.Ordinal))
        {
            var t = table;
            var (tot, tr, invalid) = TranslationStore.TableStatus(_mod, _lang, t);
            int pct = tot == 0 ? 0 : (int)Math.Round(100.0 * tr / tot);

            var row = new HBoxContainer();
            // 깨진 JSON 은 빨간색 + 경고로 표시(번역 미적용 상태). 정상은 진행률만.
            var lbl = new Label
            {
                Text = invalid
                    ? $"{t}.json     ⚠ JSON error — open & fix"
                    : $"{t}.json     {pct}%  ({tr}/{tot})",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            lbl.AddThemeColorOverride("font_color", invalid ? RED : WHITE);
            if (invalid) problems++;
            row.AddChild(lbl);
            var edit = ActionButton("Edit"); edit.Pressed += () => { _table = t; Navigate(View.Editor); };
            var up = ActionButton("Upload"); up.Pressed += () => OpenUploadDialog(t);
            row.AddChild(edit); row.AddChild(up);
            ListVBox(list).AddChild(row);
        }

        if (problems > 0)
            SetStatus(
                $"⚠ {problems} file(s) have invalid JSON and are NOT applied. Open each, fix the JSON, and Save.",
                true, true);

        var footer = new HBoxContainer();
        var mod = _mod; string lang = _lang;
        var resetAll = ActionButton("Reset all files");
        resetAll.Pressed += () => Confirm(
            "Reset all files",
            $"Clear ALL translations for {lang} in {mod.Name} and restore the original text?",
            "Reset all",
            () =>
            {
                TranslationStore.ResetLanguage(mod, lang);
                TranslationSync.ReloadFromDisk();
                SetStatus($"Reset all files for {lang}.", true, false);
                RebuildContent();
            });
        footer.AddChild(resetAll);
        _content!.AddChild(footer);
    }

    // ── 뷰: 편집기 ──────────────────────────────────────────
    private static void BuildEditor()
    {
        if (_mod == null) { Navigate(View.Mods); return; }

        // 좌: 원본(eng) read-only  /  우: 번역(현재 언어) 편집
        var panes = new HBoxContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };

        var srcCol = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        // 참조 소스 목록 = ①모드 동봉 언어들(eng 우선) + ②설치된 번역 팩이 이 (대상,테이블)에
        // 제공하는 언어들("pack: kor"). 팩 항목을 고르면 그 팩의 번역 텍스트를 참조로 본다.
        string mid = _mod.Id, tbl = _table;
        var refItems = new System.Collections.Generic.List<(string label, string lang, bool isPack)>();
        foreach (var l in _mod.ByLang.Keys.OrderBy(l => l == "eng" ? "" : l, StringComparer.Ordinal))
            refItems.Add((l, l, false));
        if (refItems.Count == 0) refItems.Add(("eng", "eng", false));
        var scan = TranslationSync.CurrentScan;
        if (scan != null)
            foreach (var pl in scan.Bundled.LangsForTarget(mid))
                if (scan.Bundled.ForTable(mid, pl, tbl).Count > 0)
                    refItems.Add(($"pack: {pl}", pl, true));

        int defIdx = refItems.FindIndex(it => !it.isPack && it.lang == "eng");
        if (defIdx < 0) defIdx = 0;

        string RefText(int i)
        {
            var it = refItems[i];
            return it.isPack && scan != null
                ? TranslationStore.ToPrettyJson(scan.Bundled.ForTable(mid, it.lang, tbl))
                : TranslationStore.SourceText(mid, tbl, it.lang);
        }

        var srcHeader = new HBoxContainer();
        srcHeader.AddChild(Lbl("Reference:", GRAY));
        var refOpt = new OptionButton();
        refOpt.AddThemeFontSizeOverride("font_size", 16);
        for (int i = 0; i < refItems.Count; i++) refOpt.AddItem(refItems[i].label, i);
        refOpt.Select(defIdx);
        srcHeader.AddChild(refOpt);
        srcCol.AddChild(srcHeader);

        var srcEdit = new CodeEdit
        {
            Text = RefText(defIdx),
            Editable = false,
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            GuttersDrawLineNumbers = true,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        srcCol.AddChild(srcEdit);
        panes.AddChild(srcCol);

        refOpt.ItemSelected += (long idx) =>
        {
            if (idx >= 0 && idx < refItems.Count) srcEdit.Text = RefText((int)idx);
        };

        var ovCol = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        ovCol.AddChild(Lbl($"Translation ({_lang})", GOLD));
        _editor = new CodeEdit
        {
            Text = TranslationStore.OverrideText(_mod.Id, _lang, _table),
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            GuttersDrawLineNumbers = true,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        ovCol.AddChild(_editor);
        panes.AddChild(ovCol);

        _content!.AddChild(panes);

        var footer = new HBoxContainer();
        var save = ActionButton("Save"); save.Pressed += SaveEditor;
        var reload = ActionButton("Reload File"); reload.Pressed += () =>
        {
            if (_editor != null) _editor.Text = TranslationStore.OverrideText(_mod.Id, _lang, _table);
            SetStatus("Reloaded from disk.", true, false);
        };
        var up = ActionButton("Upload"); up.Pressed += () => OpenUploadDialog(_table);
        var emod = _mod; string elang = _lang;
        var reset = ActionButton("Reset");
        reset.Pressed += () => Confirm(
            "Reset file",
            $"Clear all translations in {tbl}.json ({elang}) and restore the original text?",
            "Reset",
            () =>
            {
                TranslationStore.ResetOverride(emod, elang, tbl);
                if (_editor != null) _editor.Text = TranslationStore.OverrideText(emod.Id, elang, tbl);
                TranslationSync.ReloadFromDisk();
                SetStatus("Reset to original.", true, false);
            });
        footer.AddChild(save); footer.AddChild(reload); footer.AddChild(up); footer.AddChild(reset);
        _content.AddChild(footer);
    }

    private static void SaveEditor()
    {
        if (_mod == null || _editor == null) return;
        var (ok, err) = TranslationStore.SaveOverrideText(_mod.Id, _lang, _table, _editor.Text);
        if (!ok) { SetStatus("Save failed: " + err, true, true); return; }
        int n = TranslationSync.ReloadFromDisk();
        SetStatus($"Saved & applied ({n} keys active).", true, false);
    }

    // ── 업로드 ──────────────────────────────────────────────
    private static void OpenUploadDialog(string table)
    {
        if (_mod == null || _root == null) return;
        var dlg = new FileDialog
        {
            Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Title = $"Upload {table}.json  ({_lang})",
            CurrentDir = TranslationStore.Root,
        };
        dlg.AddFilter("*.json", "JSON");
        string mid = _mod.Id, lang = _lang, t = table;
        dlg.FileSelected += (string path) =>
        {
            var (ok, err) = TranslationStore.ImportInto(mid, lang, t, path);
            if (ok) { TranslationSync.ReloadFromDisk(); SetStatus($"Uploaded {t}.json.", true, false); RebuildContent(); }
            else SetStatus("Upload failed: " + err, true, true);
            dlg.QueueFree();
        };
        dlg.Canceled += () => dlg.QueueFree();
        _root.AddChild(dlg);
        dlg.PopupCentered(new Vector2I(900, 640));
    }

    private static void OpenFolder()
    {
        try { OS.ShellShowInFileManager(TranslationStore.Root); }
        catch (Exception ex) { MainFile.Logger.Warn($"[Sts2ModTranslator] open folder 실패: {ex.Message}"); }
    }

    // ── 위젯 헬퍼 ───────────────────────────────────────────
    private static void SetStatus(string text, bool visible, bool error = false)
    {
        if (_status == null) return;
        _status.Text = text;
        _status.Visible = visible && !string.IsNullOrEmpty(text);
        _status.AddThemeColorOverride("font_color", error ? RED : GRAY);
    }

    private static ScrollContainer ScrollList()
    {
        var sc = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(840, 460),
        };
        var v = new VBoxContainer { Name = "list", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        sc.AddChild(v);
        _content!.AddChild(sc);
        return sc;
    }

    private static VBoxContainer ListVBox(ScrollContainer sc) => sc.GetNode<VBoxContainer>("list");

    private static Button RowButton(string text)
    {
        var b = new Button
        {
            Text = text,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Alignment = HorizontalAlignment.Left,
            CustomMinimumSize = new Vector2(0, 40),
        };
        b.AddThemeFontSizeOverride("font_size", 18);
        return b;
    }

    private static Button ActionButton(string text)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(120, 40) };
        b.AddThemeFontSizeOverride("font_size", 18);
        return b;
    }

    /// <summary>게임 네이티브 Yes/No 모달(NVerticalPopup)로 확인. Yes 시 onYes 실행.</summary>
    private static void Confirm(string title, string body, string yesLabel, Action onYes)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            MainFile.Logger.Warn("[Sts2ModTranslator] no SceneTree — confirm 불가.");
            return;
        }
        try
        {
            var packed = ResourceLoader.Load<PackedScene>(SceneHelper.GetScenePath("ui/vertical_popup"));
            if (packed == null) { MainFile.Logger.Warn("[Sts2ModTranslator] vertical_popup 씬 없음."); return; }
            var popup = packed.Instantiate<NVerticalPopup>();
            tree.Root.AddChild(popup);
            popup.SetText(title, body);
            popup.YesButton.SetText(yesLabel);
            popup.YesButton.IsYes = true;
            popup.YesButton.Released += _ =>
            {
                try { onYes(); }
                catch (Exception ex) { MainFile.Logger.Warn($"[Sts2ModTranslator] confirm action 실패: {ex.Message}"); }
                finally { if (GodotObject.IsInstanceValid(popup)) popup.QueueFree(); }
            };
            popup.NoButton.SetText("Cancel");
            popup.NoButton.IsYes = false;
            popup.NoButton.Visible = true;
            popup.NoButton.Released += _ => { if (GodotObject.IsInstanceValid(popup)) popup.QueueFree(); };
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Sts2ModTranslator] confirm modal 실패: {ex.Message}");
        }
    }

    private static Label Lbl(string text, Color c)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", 18);
        l.AddThemeColorOverride("font_color", c);
        return l;
    }
}
