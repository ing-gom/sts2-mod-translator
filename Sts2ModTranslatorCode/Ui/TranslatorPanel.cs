using System;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Localization;
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
            var b = RowButton($"{m.Name}     [{string.Join(", ", m.ShipsLangs)}]");
            b.Pressed += () => { _mod = mod; Navigate(View.Languages); };
            ListVBox(list).AddChild(b);
        }
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
        var langs = _mod.ShipsLangs
            .Concat(new[] { TranslationSync.CurrentLanguage() })
            .Distinct().OrderBy(x => x, StringComparer.Ordinal);

        var list = ScrollList();
        foreach (var lang in langs)
        {
            var l = lang;
            var (tot, tr) = TranslationStore.Coverage(_mod, l);
            int pct = tot == 0 ? 0 : (int)Math.Round(100.0 * tr / tot);
            var b = RowButton($"{l}     {pct}%   ({tr}/{tot})");
            b.Pressed += () =>
            {
                _lang = l;
                TranslationStore.EnsureTemplates(_mod, l); // override 스켈레톤 보장
                Navigate(View.Files);
            };
            ListVBox(list).AddChild(b);
        }
    }

    // ── 뷰: 파일(테이블) 목록 ───────────────────────────────
    private static void BuildFiles()
    {
        if (_mod == null) { Navigate(View.Mods); return; }
        var list = ScrollList();
        foreach (var table in _mod.EngByTable.Keys.OrderBy(t => t, StringComparer.Ordinal))
        {
            var t = table;
            var (tot, tr) = TranslationStore.TableCoverage(_mod, _lang, t);
            int pct = tot == 0 ? 0 : (int)Math.Round(100.0 * tr / tot);

            var row = new HBoxContainer();
            row.AddChild(new Label
            {
                Text = $"{t}.json     {pct}%  ({tr}/{tot})",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            });
            var edit = ActionButton("Edit"); edit.Pressed += () => { _table = t; Navigate(View.Editor); };
            var up = ActionButton("Upload"); up.Pressed += () => OpenUploadDialog(t);
            row.AddChild(edit); row.AddChild(up);
            ListVBox(list).AddChild(row);
        }
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
        // 참조 언어 선택: 모드가 동봉한 언어들(eng 우선). 모드가 대상 언어(예: kor)를
        // 이미 동봉했다면 그 기존 번역도 여기서 토글해 볼 수 있다.
        var refLangs = _mod.ByLang.Keys
            .OrderBy(l => l == "eng" ? "" : l, StringComparer.Ordinal).ToList();
        if (refLangs.Count == 0) refLangs.Add("eng");
        int defIdx = refLangs.IndexOf("eng"); if (defIdx < 0) defIdx = 0;

        var srcHeader = new HBoxContainer();
        srcHeader.AddChild(Lbl("Reference:", GRAY));
        var refOpt = new OptionButton();
        refOpt.AddThemeFontSizeOverride("font_size", 16);
        for (int i = 0; i < refLangs.Count; i++) refOpt.AddItem(refLangs[i], i);
        refOpt.Select(defIdx);
        srcHeader.AddChild(refOpt);
        srcCol.AddChild(srcHeader);

        var srcEdit = new CodeEdit
        {
            Text = TranslationStore.SourceText(_mod.Id, _table, refLangs[defIdx]),
            Editable = false,
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            GuttersDrawLineNumbers = true,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        srcCol.AddChild(srcEdit);
        panes.AddChild(srcCol);

        string mid = _mod.Id, tbl = _table;
        refOpt.ItemSelected += (long idx) =>
        {
            if (idx >= 0 && idx < refLangs.Count)
                srcEdit.Text = TranslationStore.SourceText(mid, tbl, refLangs[(int)idx]);
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
        footer.AddChild(save); footer.AddChild(reload); footer.AddChild(up);
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

    private static Label Lbl(string text, Color c)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", 18);
        l.AddThemeColorOverride("font_color", c);
        return l;
    }
}
