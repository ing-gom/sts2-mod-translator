using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.addons.mega_text;
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

    private enum View { Mods, Languages, Files, Editor, Unsupported, ExportPack }

    private static Control? _root;
    private static Label? _title;
    private static Button? _back;
    private static VBoxContainer? _content;
    private static TextEdit? _editor;
    private static TextEdit? _srcEdit;   // 참조(원문) 패널 — 번역 캐럿을 따라 같은 키로 스크롤된다
    private static Label? _refKeyLbl;    // 지금 편집 중인 키 표시(참조 패널 헤더)
    private static Label? _status;
    private static Label? _emptyLbl;
    private static Label? _staleLbl;     // 원문 변경(stale) 항목 개수 표시(편집기 헤더)

    /// <summary>참조 패널의 키 → 줄 번호. 참조 텍스트가 바뀔 때만 다시 만든다.</summary>
    private static Dictionary<string, int> _refLineByKey = new(StringComparer.Ordinal);

    /// <summary>원문이 번역 당시와 달라진 키 집합(편집기에서 "다음 변경 항목" 점프·헤더 표시용).</summary>
    private static HashSet<string> _staleKeys = new(StringComparer.Ordinal);
    /// <summary>stale 키 → 번역 당시의 옛 원문(점프 시 무엇이 바뀌었는지 보여 주기 위함).</summary>
    private static Dictionary<string, string> _staleOldByKey = new(StringComparer.Ordinal);

    private static LineEdit? _findEdit;  // 텍스트 검색 상자(편집기 헤더 아래 줄)
    private static Label? _findLbl;      // 검색 결과 개수/위치 표시
    /// <summary>마지막 검색어(정규화 후). 캐럿 이동만으로 재검색하지 않도록 캐시한다.</summary>
    private static string _findQuery = "";

    private static Label? _termLbl;      // 용어집 불일치 항목 개수(편집기 헤더)
    /// <summary>용어집 표기가 어긋난 키 집합("다음 용어 항목" 점프용).</summary>
    private static HashSet<string> _termKeys = new(StringComparer.Ordinal);
    /// <summary>용어 불일치 키 → "원문용어 → 기대표기" 안내(참조 헤더 표시용).</summary>
    private static Dictionary<string, string> _termInfoByKey = new(StringComparer.Ordinal);

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

    // ── 일시정지 메뉴 항목(런 중) ────────────────────────────
    /// <summary>
    /// 런 도중 <b>일시정지 메뉴</b>에도 같은 패널을 연다 — "플레이하다 어색한 줄을 봤는데 고치려면
    /// 메인 메뉴까지 나가야 한다" 를 없애는 진입점. 버튼은 Settings 버튼을 복제해 만들고, 패널은
    /// 메인 메뉴 때와 똑같이 <b>메뉴의 마지막 자식 Control</b> 로 붙인다(= 버튼들 위에 그려짐).
    /// ★자체 CanvasLayer 에 얹지 말 것: CanvasLayer 는 부모 Control 의 visible 을 따라가지 않아
    /// 일시정지가 닫힌 뒤에도 계속 그려지는데, 정작 입력은 못 받아 "보이는데 X 도 안 먹는" 유령이
    /// 된다(실측). 자식 Control 이면 일시정지와 함께 숨고, 노드는 살아 있어 편집 내용도 보존된다.
    /// </summary>
    public static void AttachToPause(NPauseMenu menu) =>
        Callable.From(() => DoAttachToPause(menu)).CallDeferred();

    private const string PanelNodeName = "Sts2ModTranslatorPanel";

    private static void DoAttachToPause(NPauseMenu menu)
    {
        try
        {
            if (menu == null || !GodotObject.IsInstanceValid(menu)) return;
            if (menu.HasNode(PanelNodeName)) return; // 이미 붙음(메뉴 재사용 — 캐시된 인스턴스)

            var container = menu.GetNodeOrNull<Control>("%ButtonContainer");
            var settingsBtn = container?.GetNodeOrNull<NPauseMenuButton>("Settings");
            if (settingsBtn == null)
            {
                MainFile.Logger.Warn("[Sts2ModTranslator] 일시정지 Settings 버튼 미발견 — 항목 skip.");
                return;
            }

            var btn = (NPauseMenuButton)settingsBtn.Duplicate(14); // signals(1) 제외
            btn.Name = "Sts2ModTranslatorButton";
            settingsBtn.AddSibling(btn, false);
            // 라벨은 로크 테이블(main_menu_ui)이 아니라 직접 넣는다 — 런 중엔 그 테이블이 없을 수 있다.
            btn.GetNodeOrNull<MegaLabel>("Label")?.SetTextAutoSize("Mod Translator");
            btn.Released += _ => ShowPanel();
            // 게임패드 포커스 링은 _Ready 가 6개 버튼으로 이미 짜 두었다 — 끼어들지 않고 자기 자신만.
            var self = btn.GetPath();
            btn.FocusNeighborTop = self;
            btn.FocusNeighborBottom = self;
            btn.FocusNeighborLeft = self;
            btn.FocusNeighborRight = self;

            _root = BuildPanel();
            _root.Visible = false;
            menu.AddChild(_root); // 마지막 자식 = 같은 캔버스에서 버튼들 위에 그려지고 입력도 먼저 받는다
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Sts2ModTranslator] 일시정지 항목 추가 실패: {ex.Message}");
        }
    }

    private static void ShowPanel()
    {
        if (_root == null || !GodotObject.IsInstanceValid(_root)) return;
        _root.Visible = true;
        BlockGameHotkeys(_root);
        Navigate(View.Mods);
    }

    private static void Hide()
    {
        BlockGameHotkeys(null);
        if (_root != null && GodotObject.IsInstanceValid(_root)) _root.Visible = false;
    }

    /// <summary>지금 단축키를 막고 있는 패널 노드(없으면 null). ★해제는 반드시 이 노드로 한다.</summary>
    private static Control? _blockedScreen;

    /// <summary>
    /// 패널이 화면에 있는 동안 게임 단축키를 막는다(<paramref name="screen"/>=null 이면 해제).
    /// 런 중에는 번역문을 타이핑한 키가 그대로 게임 조작(카드 사용·턴 종료 등)으로 새어 나간다.
    /// ★Add/Remove 는 <b>같은 노드로</b> 짝을 맞춘다 — NHotkeyManager 는 스크린을 Dictionary 키로
    /// 잡고 no-op 바인딩을 쌓는 방식이라, 노드가 free 된 뒤에는 풀 방법이 없어 게임 단축키가 영구히
    /// 먹통이 된다. 그래서 해제를 X 버튼뿐 아니라 <b>VisibilityChanged·TreeExiting</b> 에도 건다:
    /// ESC 로 일시정지를 닫으면 패널은 부모를 따라 숨을 뿐 Hide() 가 호출되지 않는다.
    /// </summary>
    private static void BlockGameHotkeys(Control? screen)
    {
        try
        {
            var mgr = NHotkeyManager.Instance;
            if (mgr == null) return;
            if (screen != null && !GodotObject.IsInstanceValid(screen)) screen = null;
            if (ReferenceEquals(screen, _blockedScreen)) return; // 중복 Add 는 예외(Dictionary 키)
            if (_blockedScreen != null)
            {
                if (GodotObject.IsInstanceValid(_blockedScreen)) mgr.RemoveBlockingScreen(_blockedScreen);
                _blockedScreen = null;
            }
            if (screen != null)
            {
                mgr.AddBlockingScreen(screen);
                _blockedScreen = screen;
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Sts2ModTranslator] 단축키 차단 전환 실패: {ex.Message}");
        }
    }

    // ── 패널 골격 ───────────────────────────────────────────
    private static Control BuildPanel()
    {
        // dim 없음: 투명 root 가 클릭만 가로챔
        var root = new TranslatorPanelRoot { Name = PanelNodeName, MouseFilter = Control.MouseFilterEnum.Stop };
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        root.OnCancel = Hide; // ESC = 패널만 닫기(일시정지 메뉴에는 그대로 남는다)
        // 단축키 차단의 수명은 "패널이 실제로 화면에 있는 동안" 이다. ESC 로 일시정지를 닫으면
        // 패널은 부모를 따라 숨을 뿐 Hide() 를 거치지 않으므로, 여기서 풀지 않으면 게임으로 돌아간
        // 뒤에도 조작이 먹통이 된다. 트리에서 빠질 때(런 종료)도 free 전에 반드시 푼다.
        root.VisibilityChanged += () =>
        {
            if (!GodotObject.IsInstanceValid(root)) return;
            if (root.IsVisibleInTree()) BlockGameHotkeys(root);
            else if (ReferenceEquals(_blockedScreen, root)) BlockGameHotkeys(null);
        };
        root.TreeExiting += () => { if (ReferenceEquals(_blockedScreen, root)) BlockGameHotkeys(null); };

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
                View.Unsupported => "Unsupported mods",
                View.ExportPack => "Bundle a translation pack",
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
            case View.Unsupported: Navigate(View.Mods); break;
            case View.ExportPack: Navigate(View.Mods); break;
        }
    }

    private static void RebuildContent()
    {
        if (_content == null) return;
        foreach (var c in _content.GetChildren()) c.QueueFree();
        _editor = null;
        _srcEdit = null;
        _refKeyLbl = null;
        _refLineByKey.Clear();
        _emptyLbl = null;
        _staleLbl = null;
        _staleKeys.Clear();
        _staleOldByKey.Clear();
        _termLbl = null;
        _termKeys.Clear();
        _termInfoByKey.Clear();
        _findEdit = null;
        _findLbl = null;
        _findQuery = "";

        switch (_view)
        {
            case View.Mods: BuildMods(); break;
            case View.Languages: BuildLanguages(); break;
            case View.Files: BuildFiles(); break;
            case View.Editor: BuildEditor(); break;
            case View.Unsupported: BuildUnsupported(); break;
            case View.ExportPack: BuildExportPack(); break;
        }
    }

    // ── 뷰: 모드 목록 ───────────────────────────────────────
    private static void BuildMods()
    {
        var scan = TranslationSync.CurrentScan;
        if (scan == null) { _content!.AddChild(Lbl("No mods scanned yet.", GRAY)); return; }

        // 출력 언어 = 게임 설정 언어. 별도 드롭다운이 없어 "영어로 번역이 안 된다"는 오해가 잦다
        // (원문이 영어인 모드는 영어가 번역 대상에서 빠짐). 출력 언어와 바꾸는 법을 상단에 명시.
        string cur = TranslationSync.CurrentLanguage();
        var banner = Lbl(
            $"Output language: {LangDisplay(cur)} — mods are translated into your game's language. "
            + "To translate into a different language (e.g. English), change the game language in "
            + "Options; the target follows it. A mod already in your language isn't listed as a translation "
            + "target — open it to rewrite its original text if you want.",
            GOLD);
        banner.AddThemeFontSizeOverride("font_size", 16);
        banner.AutowrapMode = Godot.TextServer.AutowrapMode.Word; // 폭을 밀지 않고 줄바꿈
        banner.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _content!.AddChild(banner);

        var list = ScrollList();
        int outdated = 0;
        foreach (var m in scan.Supported.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            var mod = m;
            // 설치된 번역 모드가 이 대상을 번역 중이면 언어별 커버리지(%)를 표시(런타임 자동 적용).
            string pack = PackTagForTarget(scan, m.Id);
            // 대상 모드가 번역 이후 업데이트됐으면 싱크 경고(내 번역 / 설치된 팩 각각).
            string sync = SyncTag(scan, m);
            // 폴더 이름과 실제 원문 언어가 다르면(예: 한국어 in eng/) 태그에 실제 언어를 병기.
            string srcTag = string.Equals(m.ContentLang, m.SourceLang, StringComparison.OrdinalIgnoreCase)
                ? "" : $"  (source: {LangDisplay(m.ContentLang)})";
            // 원문이 혼합(부분 번역)이면 명시 — "✎ Edit original" 로 섞인 외국어 항목을 덮어쓸 수 있음을 알린다.
            if (m.HasMixedSource) srcTag += "  (partly translated — mixed source)";
            var b = RowButton($"{m.Name}     [{string.Join(", ", m.ShipsLangs)}]{srcTag}{pack}{sync}");
            if (sync.Length > 0) { b.AddThemeColorOverride("font_color", RED); outdated++; }
            else if (pack.Length > 0) b.AddThemeColorOverride("font_color", GOLD);
            b.Pressed += () => { _mod = mod; Navigate(View.Languages); };
            ListVBox(list).AddChild(b);
        }
        if (outdated > 0)
            ListVBox(list).AddChild(Lbl(
                $"⚠ {outdated} mod(s) were updated after translating — their translations may be out of date. "
                + "Open a mod to review; re-translate changed strings, then re-export the pack.", RED));

        // 설치된 팩은 위 대상 행의 'pack: 언어 %' 태그로 표시. 내용은 모드 ▸ Edit 의 Reference 에서 본다.
        if (scan.Bundled.Any)
            ListVBox(list).AddChild(Lbl(
                $"({scan.Bundled.Providers.Count} translation pack(s) installed — applied automatically. "
                + "Open a mod ▸ Edit, then pick \"pack: <lang>\" in Reference to view its text.)", GOLD));
        if (scan.Unsupported.Count > 0)
        {
            var ub = RowButton($"▸ {scan.Unsupported.Count} unsupported mods — tap to see which & why");
            ub.AddThemeColorOverride("font_color", GRAY);
            ub.Pressed += () => Navigate(View.Unsupported);
            ListVBox(list).AddChild(ub);
        }

        // 하단 액션
        var footer = new HBoxContainer();
        var of = ActionButton("Open Folder"); of.Pressed += OpenFolder;
        var rl = ActionButton("Reload"); rl.Pressed += () => { int n = TranslationSync.ReloadFromDisk(); SetStatus(WithFormatWarning($"Reloaded {n} keys."), true, TranslationSync.LastInjectInvalidCount > 0); };
        var dk = ActionButton(TranslationStore.LoadApiKey().Length > 0 ? "DeepL key ✓" : "DeepL key…");
        dk.CustomMinimumSize = new Vector2(150, 40);
        dk.TooltipText = "Set the DeepL API key used by the editor's Auto-fill button.";
        dk.Pressed += () => PromptApiKey(() => { if (_view == View.Mods) RebuildContent(); });
        var ai = ActionButton("Translate with AI…");
        ai.CustomMinimumSize = new Vector2(180, 40);
        ai.TooltipText = "Use an AI coding agent to translate. Shows how to start; rules are already in the folder.";
        ai.Pressed += PromptAiKit;
        var pk = ActionButton("Bundle pack…");
        pk.CustomMinimumSize = new Vector2(160, 40);
        pk.TooltipText = "Bundle several mods' translations into one shareable translation pack.";
        pk.Pressed += () => Navigate(View.ExportPack);
        footer.AddChild(of); footer.AddChild(rl); footer.AddChild(dk); footer.AddChild(ai); footer.AddChild(pk);
        _content!.AddChild(footer);
    }

    // ── 뷰: 미지원 모드 목록 ─────────────────────────────────
    private static void BuildUnsupported()
    {
        var scan = TranslationSync.CurrentScan;
        if (scan == null || scan.Unsupported.Count == 0)
        {
            _content!.AddChild(Lbl("No unsupported mods.", GRAY));
            return;
        }

        _content!.AddChild(Lbl(
            "These installed mods can't be translated here because they ship no readable "
            + "localization tables (runtime-merged or hardcoded text).", GRAY));

        var list = ScrollList();
        foreach (var u in scan.Unsupported.OrderBy(u => u.Id, StringComparer.Ordinal))
        {
            var row = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            var name = new Label { Text = u.Name.Length > 0 && u.Name != u.Id ? $"{u.Name}  ({u.Id})" : u.Id };
            name.AddThemeFontSizeOverride("font_size", 18);
            name.AddThemeColorOverride("font_color", WHITE);
            row.AddChild(name);
            var reason = new Label { Text = "    " + u.Reason };
            reason.AddThemeFontSizeOverride("font_size", 15);
            reason.AddThemeColorOverride("font_color", GRAY);
            row.AddChild(reason);
            ListVBox(list).AddChild(row);
        }
    }

    // ── 뷰: 언어 목록 ───────────────────────────────────────
    private static void BuildLanguages()
    {
        if (_mod == null) { Navigate(View.Mods); return; }

        string cur = TranslationSync.CurrentLanguage();
        // 게임 전체 지원 언어 ∪ 모드 동봉 언어. 원문 언어는 번역 대상 아님이라 제외.
        // 현재 설정 언어를 맨 위(기본 선택)로, 나머지는 게임 선언 순서를 유지(OrderBy 안정 정렬).
        var langs = TranslationSync.SupportedLanguages()
            .Concat(_mod.ShipsLangs)
            // 대상에서 제외하는 건 폴더 이름이 아니라 '실제 원문 언어(ContentLang)' 하나뿐.
            // → 한국어를 eng/ 에 넣은 모드는 eng 가 정상적인 대상이 되어 '한국어→영어' 를 채울 수 있고,
            //   정상 모드(eng=영어)는 ContentLang=eng 라 기존처럼 eng 가 제외된다.
            .Where(l => !string.Equals(l, _mod.ContentLang, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => string.Equals(l, cur, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();

        // 현재 언어만 인게임 즉시 반영, 나머지는 그 언어로 전환 후 적용됨을 안내.
        // 길어서 한 줄에 안 들어가므로 자동 줄바꿈(cf. v1.14.1 출력언어 배너 — Lbl 기본은 미줄바꿈).
        var langBanner = Lbl(
            "Pick any language to translate. The current game language applies instantly; "
            + "others apply after you switch the game to that language. The top row edits the "
            + "mod's own text (the language it already ships) — use it to fix leftover or rough "
            + "lines when you play it.", GRAY);
        langBanner.AutowrapMode = Godot.TextServer.AutowrapMode.Word;
        langBanner.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _content!.AddChild(langBanner);

        // 폴더 이름(예: eng)과 실제 원문 언어가 다르면 명시 — DeepL source 오판 혼동 방지.
        if (!string.Equals(_mod.ContentLang, _mod.SourceLang, StringComparison.OrdinalIgnoreCase))
            _content!.AddChild(Lbl(
                $"Note: this mod stores its original text in the '{_mod.SourceLang}' folder, but the text is "
                + $"actually {LangDisplay(_mod.ContentLang)}. Auto-translation uses {LangDisplay(_mod.ContentLang)} "
                + "as the source language.", GOLD));

        var list = ScrollList();

        // 원문 언어(ContentLang) 편집 행 — 다른 언어와 '같은 목록'의 정식 항목으로 노출한다.
        // (예전엔 하단 별도 회색 "✎ Edit original" 행이었으나, 이미 영어인 모드를 유저가 덮어쓰려 해도
        //  목록에 영어가 없어 "영어 옵션이 없다"는 오해가 잦았다 — Black Souls 계열 실제 피드백.)
        // 번역이 아니라 '원문 위 override' 라서 진행률 %(0% 로 뜨면 "번역 필요"처럼 보이는 노이즈) 대신
        // 편집한 개수/안내를 보여준다. 모드 목록(BuildMods)의 커버리지엔 여전히 미포함이라 상단 목록은 깨끗.
        string orig = _mod.ContentLang;
        if (!string.IsNullOrEmpty(orig))
        {
            bool origIsCurrent = string.Equals(orig, cur, StringComparison.OrdinalIgnoreCase);
            bool mixed = _mod.HasMixedSource;
            var (_, edited) = TranslationStore.Coverage(_mod, orig); // tr = 이 언어 위 non-empty override 수
            // ★"original" 이라고 단정하지 않는다 — ContentLang 은 '소스 폴더의 실제 언어'일 뿐,
            //   모드의 진짜 원본/저작 언어라는 보장이 없다(eng+zhs 를 담은 중국어 모드는 eng 가 기계번역이라
            //   ContentLang=eng 여도 원본은 zhs). 그래서 행이 '하는 일'(이 모드가 담은 그 언어 텍스트 편집)로만 표기.
            string status = mixed
                ? "✎ partly translated — fill the foreign leftovers"
                : "✎ edit this mod's own text";
            if (edited > 0) status += $"  ({edited} edited)";
            string curTag = origIsCurrent ? "  ◀ current" : "";
            var ob = RowButton($"{orig}     {status}{curTag}");
            ob.AddThemeColorOverride("font_color", origIsCurrent ? GOLD : GRAY);
            ob.TooltipText = (mixed
                ? $"This mod is only partly translated — some entries are still in another language. Pick this to override just those into {LangDisplay(orig)} (leave the already-correct ones empty to keep them). DeepL auto-fill auto-detects each entry, so only the foreign ones are translated. Use \"Next empty ▼\" and the left reference pane to spot them."
                : $"Edit the {LangDisplay(orig)} text this mod ships (the text shown when you play in {LangDisplay(orig)}). Only the entries you fill in are applied over the mod's existing text; blanks keep it as-is.")
                + (origIsCurrent ? "" : $" It shows in-game after you switch the game language to {LangDisplay(orig)}.");
            ob.Pressed += () =>
            {
                _lang = orig;
                TranslationStore.EnsureTemplates(_mod, orig); // 원문 언어 override 스켈레톤 생성
                Navigate(View.Files);
            };
            ListVBox(list).AddChild(ob);
        }

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

        // 2) 버전 입력 — 이미 설치돼 있으면 현재 버전을 표기하고 patch 한 칸 올린 값을 기본 제안.
        string? modsDir = TranslationStore.GameModsDir;
        string? installedVer = modsDir == null ? null : TranslationStore.InstalledVersion(mod, modsDir);
        var versionRow = new HBoxContainer();
        versionRow.AddChild(Lbl("Version:", GRAY));
        _versionEdit = new LineEdit
        {
            Text = TranslationStore.NextVersion(installedVer),
            PlaceholderText = "1.0.0",
            CustomMinimumSize = new Vector2(140, 36),
        };
        versionRow.AddChild(_versionEdit);
        if (installedVer != null)
            versionRow.AddChild(Lbl($"installed: v{installedVer}", GOLD));
        box.AddChild(versionRow);

        // 3) 설치/업데이트 버튼 — 번역을 독립 모드로 게임 mods\ 에 생성(로드·테스트·창작마당 업로드의 입력).
        var footer = new HBoxContainer();
        var install = ActionButton(installedVer != null ? "Update installed mod" : "Install as mod");
        install.CustomMinimumSize = new Vector2(200, 40);
        install.Pressed += () => InstallToGameMods(mod);
        footer.AddChild(install);
        box.AddChild(footer);

        box.AddChild(Lbl(
            installedVer != null
                ? $"Already installed (v{installedVer}). Updating overwrites it with the new version above; "
                  + "restart to load & test it, then re-upload to the Workshop."
                : "Creates a standalone translation mod in the game's mods folder. Restart to load & test it; "
                  + "then upload it to the Workshop. To share the file directly, zip that folder.", GRAY));
        _content!.AddChild(box);
    }

    // ── 번역 모드 내보내기 ──────────────────────────────────
    private static LineEdit? _authorEdit;
    private static LineEdit? _versionEdit;

    // 팩 빌더 상태(다중 모드 선택). 세션 간 유지 — 최초 진입 때 디스크에서 복원.
    private static readonly HashSet<string> _packSelected = new(StringComparer.Ordinal);
    private static bool _packLoaded;
    private static LineEdit? _packNameEdit;

    /// <summary>author 입력칸 값을 읽어 저장하고 반환(다음 내보내기에 재사용).</summary>
    private static string CurrentAuthor()
    {
        string a = (_authorEdit?.Text ?? "").Trim();
        TranslationStore.SaveAuthor(a);
        return a;
    }

    /// <summary>version 입력칸 값(비어 있으면 ExportMod 가 "1.0.0" 으로 폴백).</summary>
    private static string CurrentVersion() => (_versionEdit?.Text ?? "").Trim();

    // 게임 mods\ 에 바로 내보내 곧장 로드/테스트 + 워크샵 업로드 대시보드가 '설치됨'으로 인식.
    private static void InstallToGameMods(SupportedMod mod)
    {
        string? mods = TranslationStore.GameModsDir;
        if (string.IsNullOrEmpty(mods))
        {
            SetStatus("Could not locate the game's mods folder.", true, true);
            return;
        }
        var (ok, path, err) = TranslationStore.ExportMod(mod, CurrentAuthor(), mods, CurrentVersion());
        if (!ok) { SetStatus("Install failed: " + err, true, true); return; }
        string ver = CurrentVersion();
        SetStatus($"Installed v{(ver.Length == 0 ? "1.0.0" : ver)} -> {path}.  Restart the game to load it.",
            true, false);
        try { OS.ShellShowInFileManager(path); }
        catch (Exception ex) { MainFile.Logger.Warn($"[Sts2ModTranslator] install open 실패: {ex.Message}"); }
    }

    // ── 뷰: 팩 빌더(여러 모드를 하나의 번역 팩으로) ─────────────
    private static void BuildExportPack()
    {
        var scan = TranslationSync.CurrentScan;
        if (scan == null) { _content!.AddChild(Lbl("No mods scanned yet.", GRAY)); return; }

        // 최초 진입 시 저장된 선택을 복원(세션 간 유지).
        if (!_packLoaded)
        {
            _packSelected.Clear();
            foreach (var id in TranslationStore.LoadPackSelection()) _packSelected.Add(id);
            _packLoaded = true;
        }

        _content!.AddChild(Lbl(
            "Bundle several mods' translations into one shareable pack. Tick the mods to include, "
            + "name the pack, then Install. Only mods you've translated (≥1 entry) can be ticked.", GRAY));

        // 이전에 배포한 번들 팩을 프리셋으로 되불러오기 — 설치된 *_Translations 폴더에서 대상 목록 역산.
        // 선택하면 이름·체크·버전(+1 제안)이 채워져 그대로 "Update installed pack" 가능.
        string? presetModsDir = TranslationStore.GameModsDir;
        var installedPacks = TranslationStore.DiscoverInstalledPacks(presetModsDir);
        if (installedPacks.Count > 0)
        {
            var presetRow = new HBoxContainer();
            presetRow.AddChild(Lbl("Existing packs:", GRAY));
            var opt = new OptionButton { CustomMinimumSize = new Vector2(360, 36) };
            opt.AddThemeFontSizeOverride("font_size", 16);
            opt.AddItem("Load a pack you've deployed…", 0);   // placeholder(index 0)
            for (int i = 0; i < installedPacks.Count; i++)
            {
                var p = installedPacks[i];
                string vtag = string.IsNullOrEmpty(p.Version) ? "" : $" v{p.Version}";
                opt.AddItem($"{p.Name}{vtag}  ({p.TargetIds.Count} mods)", i + 1);
            }
            opt.Select(0);
            opt.ItemSelected += (long idx) => LoadPackPreset(installedPacks, (int)idx);
            presetRow.AddChild(opt);
            _content!.AddChild(presetRow);
        }

        // 지원 모드 = 체크박스 + 번역 요약(키 수 / 언어). 번역 없는 모드는 회색 비활성.
        var list = ScrollList();
        var vb = ListVBox(list);
        int selectable = 0;
        foreach (var m in scan.Supported.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            var mod = m;
            var langs = TranslationStore.CollectLangs(mod);
            int keys = langs.Sum(l => l.tables.Values.Sum(d => d.Count));
            bool hasTr = keys > 0;
            if (hasTr) selectable++;
            else _packSelected.Remove(mod.Id); // 더 이상 번역 없는 모드는 선택에서 정리

            var cb = new CheckBox
            {
                Text = hasTr
                    ? $"{mod.Name}    ({keys} keys · {string.Join(", ", langs.Select(l => l.lang))})"
                    : $"{mod.Name}    (no translations yet)",
                ButtonPressed = hasTr && _packSelected.Contains(mod.Id),
                Disabled = !hasTr,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 40),
            };
            cb.AddThemeFontSizeOverride("font_size", 18);
            if (!hasTr) cb.AddThemeColorOverride("font_color", GRAY);
            cb.Toggled += (bool on) =>
            {
                if (on) _packSelected.Add(mod.Id); else _packSelected.Remove(mod.Id);
                TranslationStore.SavePackSelection(_packSelected);
            };
            vb.AddChild(cb);
        }
        if (selectable == 0)
            vb.AddChild(Lbl("You haven't translated any mod yet — translate a mod first, then come back.", GRAY));

        // 하단: 팩 이름 / author / version + 설치 버튼.
        var box = new VBoxContainer();

        var nameRow = new HBoxContainer();
        nameRow.AddChild(Lbl("Pack name:", GRAY));
        _packNameEdit = new LineEdit
        {
            Text = TranslationStore.LoadPackName(),
            PlaceholderText = "e.g. My Korean Pack (required)",
            CustomMinimumSize = new Vector2(320, 36),
        };
        nameRow.AddChild(_packNameEdit);
        box.AddChild(nameRow);

        var authorRow = new HBoxContainer();
        authorRow.AddChild(Lbl("Author:", GRAY));
        _authorEdit = new LineEdit
        {
            Text = TranslationStore.LoadAuthor(),
            PlaceholderText = "your name (becomes the pack's author)",
            CustomMinimumSize = new Vector2(320, 36),
        };
        authorRow.AddChild(_authorEdit);
        box.AddChild(authorRow);

        // 이미 설치된 같은-이름 팩이 있으면 버전을 표기하고 patch 한 칸 올린 값을 제안.
        string? modsDir = TranslationStore.GameModsDir;
        string curName = _packNameEdit.Text.Trim();
        string? installedVer = (modsDir == null || string.IsNullOrEmpty(curName))
            ? null
            : TranslationStore.InstalledVersionById(TranslationStore.ExportedPackId(curName), modsDir);
        var versionRow = new HBoxContainer();
        versionRow.AddChild(Lbl("Version:", GRAY));
        _versionEdit = new LineEdit
        {
            Text = TranslationStore.NextVersion(installedVer),
            PlaceholderText = "1.0.0",
            CustomMinimumSize = new Vector2(140, 36),
        };
        versionRow.AddChild(_versionEdit);
        if (installedVer != null)
            versionRow.AddChild(Lbl($"installed: v{installedVer}", GOLD));
        box.AddChild(versionRow);

        var footer = new HBoxContainer();
        var install = ActionButton(installedVer != null ? "Update installed pack" : "Install pack to mods");
        install.CustomMinimumSize = new Vector2(220, 40);
        install.Pressed += InstallPackToGameMods;
        footer.AddChild(install);
        // 초기화 — 체크·이름을 모두 비운다(디스크의 설치된 팩은 건드리지 않음).
        var reset = ActionButton("Reset");
        reset.CustomMinimumSize = new Vector2(120, 40);
        reset.TooltipText = "Clear all ticks and the pack name. Does not delete any installed pack.";
        reset.Pressed += ResetPackBuilder;
        footer.AddChild(reset);
        box.AddChild(footer);

        box.AddChild(Lbl(
            "Creates one standalone translation mod containing every ticked mod's translations. "
            + "Restart to load & test it, then upload it to the Workshop (add an image.png thumbnail there).",
            GRAY));
        _content!.AddChild(box);
    }

    // 선택한 모드들을 하나의 팩으로 게임 mods\ 에 내보낸다(즉시 설치본 + 워크샵 업로드 입력).
    private static void InstallPackToGameMods()
    {
        string? mods = TranslationStore.GameModsDir;
        if (string.IsNullOrEmpty(mods))
        {
            SetStatus("Could not locate the game's mods folder.", true, true);
            return;
        }
        if (_packSelected.Count == 0)
        {
            SetStatus("Tick at least one mod to bundle.", true, true);
            return;
        }
        string packName = (_packNameEdit?.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(packName))
        {
            SetStatus("Enter a pack name first.", true, true);
            return;
        }

        TranslationStore.SavePackName(packName);
        string packId = TranslationStore.ExportedPackId(packName);
        var scan = TranslationSync.CurrentScan;
        var selMods = scan?.Supported.Where(m => _packSelected.Contains(m.Id)).ToList()
                      ?? new List<SupportedMod>();

        var (ok, path, err, included, skipped) =
            TranslationStore.ExportPack(selMods, packId, packName, CurrentAuthor(), mods, CurrentVersion());
        if (!ok) { SetStatus("Install failed: " + err, true, true); return; }

        string ver = CurrentVersion();
        string msg = $"Installed \"{packName}\" v{(ver.Length == 0 ? "1.0.0" : ver)} "
                   + $"({included.Count} mod(s)) -> {path}.  Restart the game to load it.";
        if (skipped.Count > 0) msg += $"  Skipped {skipped.Count} with no translations.";
        SetStatus(msg, true, false);
        try { OS.ShellShowInFileManager(path); }
        catch (Exception ex) { MainFile.Logger.Warn($"[Sts2ModTranslator] pack install open 실패: {ex.Message}"); }
    }

    // 팩 빌더 초기화 — 체크·이름을 비운다. 설치된 팩 폴더는 삭제하지 않음(순수 UI 리셋).
    private static void ResetPackBuilder()
    {
        _packSelected.Clear();
        _packLoaded = true;                       // 재빌드 시 빈 선택 유지(디스크 재로드 방지)
        TranslationStore.SavePackSelection(_packSelected);
        TranslationStore.SavePackName("");
        RebuildContent();
        SetStatus("Cleared — pick mods and a name to build a new pack. (Installed packs are untouched.)",
            true, false);
    }

    // 이전에 배포한 팩을 프리셋으로 되불러온다: 대상 모드 체크 + 이름 채움 + 버전 자동 +1 제안.
    // idx 0 = 플레이스홀더(무시). 현재 번역 가능(로드+번역됨)한 대상만 체크되며, 나머지는 안내로만 표기.
    private static void LoadPackPreset(List<TranslationStore.InstalledPack> packs, int idx)
    {
        if (idx <= 0 || idx > packs.Count) return;
        var p = packs[idx - 1];

        var scan = TranslationSync.CurrentScan;
        var supported = scan?.Supported.Select(m => m.Id).ToHashSet(StringComparer.Ordinal)
                        ?? new HashSet<string>(StringComparer.Ordinal);

        _packSelected.Clear();
        foreach (var tid in p.TargetIds) _packSelected.Add(tid); // 지금 없는 대상도 유지(재설치 시 filter로 제외됨)
        _packLoaded = true;                                       // 재빌드 시 우리 선택 유지
        TranslationStore.SavePackSelection(_packSelected);
        TranslationStore.SavePackName(p.Name);

        RebuildContent();   // 체크박스·이름·버전(+1) 재구성

        int avail = p.TargetIds.Count(t => supported.Contains(t));
        int missing = p.TargetIds.Count - avail;
        string msg = $"Loaded \"{p.Name}\": {avail}/{p.TargetIds.Count} mods ticked.";
        if (missing > 0)
            msg += $"  {missing} not available right now (mod not loaded / not translated) — "
                 + "they'll be dropped if you re-install.";
        SetStatus(msg, true, missing > 0);
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
            // 분모(TotalKeys)가 원문 빈 키를 빼므로 분자도 같은 기준으로 세야 100% 를 넘지 않는다.
            tr += dict.Count(kv => eng.TryGetValue(kv.Key, out var src)
                                   && SupportedMod.IsTranslatable(src)
                                   && !string.IsNullOrEmpty(kv.Value));
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

    /// <summary>
    /// 대상 모드가 번역 이후 업데이트됐음을 알리는 태그. 두 출처를 각각 본다:
    ///   · 내 로컬 번역: 마지막 번역 시점 기록 버전 vs 현재 모드 버전.
    ///   · 설치된 번역 팩: 팩이 동봉한 기준 버전 vs 현재 모드 버전.
    /// 버전이 다르면 경고. 현재 버전이 비어 있으면(버전 미상) 판단 불가 → 표시 안 함. 없으면 "".
    /// </summary>
    internal static string SyncTag(ScanResult scan, SupportedMod m)
    {
        if (string.IsNullOrEmpty(m.Version)) return ""; // 대상 모드에 버전 정보 없음 — 판단 불가
        var parts = new System.Collections.Generic.List<string>();

        string? rec = TranslationStore.GetRecordedTargetVersion(m.Id);
        if (rec != null && !TranslationStore.SameVersion(rec, m.Version))
            parts.Add($"translated for v{rec}, mod now v{m.Version}");

        string? packVer = scan.Bundled.SourceVersionForTarget(m.Id);
        if (packVer != null && !TranslationStore.SameVersion(packVer, m.Version))
            parts.Add($"pack for v{packVer}, mod now v{m.Version}");

        return parts.Count == 0 ? "" : "   ⚠ out of sync (" + string.Join("; ", parts) + ")";
    }

    // ── 뷰: 파일(테이블) 목록 ───────────────────────────────
    private static void BuildFiles()
    {
        if (_mod == null) { Navigate(View.Mods); return; }
        var list = ScrollList();
        int problems = 0, fmtProblems = 0, staleProblems = 0, termProblems = 0;
        foreach (var table in _mod.EngByTable.Keys.OrderBy(t => t, StringComparer.Ordinal))
        {
            var t = table;
            var (tot, tr, invalid) = TranslationStore.TableStatus(_mod, _lang, t);
            int pct = tot == 0 ? 0 : (int)Math.Round(100.0 * tr / tot);
            // SmartFormat 문법이 깨진 값(JSON 으로는 유효 — 기존 JSON 경고에 안 잡힘) 개수.
            int badFmt = invalid ? 0
                : TranslationSync.InvalidFormatKeys(TranslationStore.OverrideText(_mod.Id, _lang, t)).Count;
            // 원문이 번역 당시와 달라진(=대상 모드 업데이트로 다시 번역해야 하는) 항목 개수.
            int stale = invalid ? 0 : TranslationStore.StaleKeys(_mod, _lang, t).Count;
            // 용어집 표기가 어긋난(원문에 용어 있는데 번역엔 정식표기 없음) 항목 개수(키 기준 distinct).
            int term = invalid ? 0
                : TranslationStore.GlossaryIssues(_mod, _lang, t).Select(i => i.key).Distinct().Count();

            var row = new HBoxContainer();
            // 깨진 JSON/포맷은 빨간색 + 경고로 표시(해당 항목 번역 미적용 상태). 정상은 진행률만.
            var lbl = new Label
            {
                Text = invalid
                    ? $"{t}.json     ⚠ JSON error — open & fix"
                    : $"{t}.json     {pct}%  ({tr}/{tot}){(tot > tr ? $"   ◦ {tot - tr} empty" : "")}"
                      + (stale > 0 ? $"   ⚠ {stale} source-changed" : "")
                      + (term > 0 ? $"   ⚠ {term} term" : "")
                      + (badFmt > 0 ? $"   ⚠ {badFmt} bad {{format}}" : ""),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            // 우선순위: 미적용(빨강) > 원문변경·용어(금색) > 정상(흰색).
            lbl.AddThemeColorOverride("font_color",
                invalid || badFmt > 0 ? RED : stale > 0 || term > 0 ? GOLD : WHITE);
            if (invalid) problems++;
            if (badFmt > 0) fmtProblems++;
            if (stale > 0) staleProblems++;
            if (term > 0) termProblems++;
            row.AddChild(lbl);
            var edit = ActionButton("Edit"); edit.Pressed += () => { _table = t; Navigate(View.Editor); };
            var up = ActionButton("Upload"); up.Pressed += () => OpenUploadDialog(t);
            row.AddChild(edit); row.AddChild(up);
            ListVBox(list).AddChild(row);
        }

        if (problems > 0 || fmtProblems > 0)
        {
            string msg = problems > 0
                ? $"⚠ {problems} file(s) have invalid JSON and are NOT applied. Open each, fix the JSON, and Save."
                : "";
            if (fmtProblems > 0)
                msg += (msg.Length > 0 ? "  " : "")
                    + $"⚠ {fmtProblems} file(s) contain entries with invalid {{format}} syntax "
                    + "(unbalanced braces?) — those entries are NOT applied. Open & fix, then Save.";
            if (staleProblems > 0)
                msg += (msg.Length > 0 ? "  " : "")
                    + $"⚠ {staleProblems} file(s) have entries whose original text changed since you "
                    + "translated them — open & use \"Next changed ▼\" to re-check.";
            if (termProblems > 0)
                msg += (msg.Length > 0 ? "  " : "")
                    + $"⚠ {termProblems} file(s) have entries that don't use a glossary term — "
                    + "open & use \"Next term ▼\".";
            SetStatus(msg, true, true);
        }
        else if (staleProblems > 0 || termProblems > 0)
        {
            var bits = new System.Collections.Generic.List<string>();
            if (staleProblems > 0)
                bits.Add($"{staleProblems} file(s) have entries whose original changed (\"Next changed ▼\")");
            if (termProblems > 0)
                bits.Add($"{termProblems} file(s) don't use a glossary term (\"Next term ▼\")");
            SetStatus("⚠ " + string.Join("; ", bits) + ". Open a file to review.", true, false);
        }

        var footer = new HBoxContainer();
        var mod = _mod; string lang = _lang;
        var autoAll = ActionButton("Auto-fill all ✨");
        autoAll.CustomMinimumSize = new Vector2(180, 40);
        bool hasKeyF = TranslationStore.LoadApiKey().Length > 0;
        autoAll.Disabled = !hasKeyF;
        autoAll.TooltipText = hasKeyF
            ? "Machine-translate every empty entry across all files with DeepL, then save."
            : "Set your DeepL API key first — use the \"DeepL key…\" button on the mods list.";
        autoAll.Pressed += () => OnAutoFillAll(autoAll);
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
        var gloss = ActionButton("Glossary…");
        gloss.CustomMinimumSize = new Vector2(140, 40);
        gloss.TooltipText =
            "Fix mod-specific terms (character names, unique mechanics) so they read the same everywhere.\n"
            + "Entries that don't use a term you set here are flagged with \"Next term ▼\" in the editor.";
        gloss.Pressed += () => OpenGlossaryDialog(mod, lang);
        footer.AddChild(autoAll); footer.AddChild(gloss); footer.AddChild(resetAll);
        _content!.AddChild(footer);
    }

    /// <summary>
    /// 모드별 사용자 용어집 편집 모달. 한 줄에 <c>원문 용어 = 번역 용어</c> 형식으로 자유 편집한다
    /// (그리드 위젯 대신 텍스트라 저작이 빠르다). 저장 시 첫 '=' 로 나눠 파싱하고 파일 목록을 갱신한다.
    /// </summary>
    private static void OpenGlossaryDialog(SupportedMod mod, string lang)
    {
        if (_root == null || !GodotObject.IsInstanceValid(_root)) return;
        var dlg = new AcceptDialog
        {
            Title = $"Glossary — {mod.Name} ({LangDisplay(lang)})",
            OkButtonText = "Save",
            MinSize = new Vector2I(680, 460),
        };
        var box = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        box.AddChild(Lbl("One term per line, as  original = translation  — e.g.", GRAY));
        box.AddChild(Lbl("   Artoria = 아르토리아", GRAY));
        box.AddChild(Lbl("Entries whose original contains the term but whose translation lacks yours are flagged.", GRAY));

        var edit = new TextEdit
        {
            Text = GlossaryToText(TranslationStore.LoadModGlossary(mod.Id, lang)),
            CustomMinimumSize = new Vector2(640, 340),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        box.AddChild(edit);
        dlg.AddChild(box);

        dlg.Confirmed += () =>
        {
            TranslationStore.SaveModGlossary(mod.Id, lang, ParseGlossaryText(edit.Text));
            SetStatus("Glossary saved.", true, false);
            if (_view == View.Files) RebuildContent();
            else if (_view == View.Editor) { UpdateTermState(); }
            if (GodotObject.IsInstanceValid(dlg)) dlg.QueueFree();
        };
        dlg.Canceled += () => { if (GodotObject.IsInstanceValid(dlg)) dlg.QueueFree(); };
        _root.AddChild(dlg);
        dlg.PopupCentered();
    }

    /// <summary>용어집 dict → 편집용 텍스트("원문 = 번역" 줄, 원문 정렬).</summary>
    private static string GlossaryToText(System.Collections.Generic.Dictionary<string, string> gloss)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var kv in gloss.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.Append(kv.Key).Append(" = ").Append(kv.Value).Append('\n');
        return sb.ToString();
    }

    /// <summary>편집 텍스트 → 용어집 dict. 첫 '=' 로 분리, 빈 줄·'#' 주석 무시.</summary>
    private static System.Collections.Generic.Dictionary<string, string> ParseGlossaryText(string text)
    {
        var d = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string src = line.Substring(0, eq).Trim();
            string tgt = line.Substring(eq + 1).Trim();
            if (src.Length > 0 && tgt.Length > 0) d[src] = tgt; // 뒤 항목이 앞을 덮음(중복 키)
        }
        return d;
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
        foreach (var l in _mod.ByLang.Keys.OrderBy(l => l == _mod.SourceLang ? "" : l, StringComparer.Ordinal))
            refItems.Add((l, l, false));
        if (refItems.Count == 0) refItems.Add((_mod.SourceLang, _mod.SourceLang, false));
        var scan = TranslationSync.CurrentScan;
        if (scan != null)
            foreach (var pl in scan.Bundled.LangsForTarget(mid))
                if (scan.Bundled.ForTable(mid, pl, tbl).Count > 0)
                    refItems.Add(($"pack: {pl}", pl, true));

        int defIdx = refItems.FindIndex(it => !it.isPack && it.lang == _mod.SourceLang);
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
        // 지금 편집 중인 키. 참조에 그 키가 없으면(참조 언어가 일부만 번역했거나 override 가 낡은
        // 경우) 참조 패널이 움직이지 않는데, 그게 고장인지 원래 없는 건지 여기서 구분해 준다.
        _refKeyLbl = Lbl("", GRAY);
        _refKeyLbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        // ★키는 최대 97자까지 나온다(STS2_WINE_FOX_EVENT_….description). 클립하지 않으면 라벨의
        // 최소 크기가 헤더를 밀어 패널 전체가 화면 밖으로 자란다(Godot: min-size 가 앵커를 이김).
        _refKeyLbl.ClipText = true;
        _refKeyLbl.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        srcHeader.AddChild(_refKeyLbl);
        srcCol.AddChild(srcHeader);

        var srcEdit = new CodeEdit
        {
            Text = RefText(defIdx),
            Editable = false,
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            GuttersDrawLineNumbers = true,
            HighlightCurrentLine = true, // 캐럿이 따라간 원문 줄을 눈에 보이게(기본값 false)
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        StyleFindHighlight(srcEdit);
        srcCol.AddChild(srcEdit);
        panes.AddChild(srcCol);
        _srcEdit = srcEdit;
        RebuildRefIndex();

        refOpt.ItemSelected += (long idx) =>
        {
            if (idx >= 0 && idx < refItems.Count) srcEdit.Text = RefText((int)idx);
            RebuildRefIndex();  // 참조 언어마다 키 집합·줄 배치가 다르다
            SyncRefToCaret();   // 새 참조에서도 편집 중인 키를 계속 비춘다
        };

        var ovCol = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        // 헤더: 제목 + 빈 항목/원문변경 카운트 + '다음 빈 항목'·'다음 변경 항목' 점프.
        var ovHeader = new HBoxContainer();
        var ovTitle = Lbl($"Translation ({_lang})", GOLD);
        ovTitle.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        ovHeader.AddChild(ovTitle);
        _staleLbl = new Label();
        _staleLbl.AddThemeFontSizeOverride("font_size", 16);
        ovHeader.AddChild(_staleLbl);
        var nextStale = ActionButton("Next changed ▼");
        nextStale.CustomMinimumSize = new Vector2(170, 36);
        nextStale.TooltipText =
            "Jump to the next entry whose ORIGINAL text changed since you translated it\n"
            + "(the target mod was updated). The reference pane shows what it was translated from.";
        nextStale.Pressed += JumpToNextStale;
        ovHeader.AddChild(nextStale);
        _termLbl = new Label();
        _termLbl.AddThemeFontSizeOverride("font_size", 16);
        ovHeader.AddChild(_termLbl);
        var nextTerm = ActionButton("Next term ▼");
        nextTerm.CustomMinimumSize = new Vector2(150, 36);
        nextTerm.TooltipText =
            "Jump to the next entry whose original uses a glossary term but whose\n"
            + "translation doesn't use the term you set. Edit the glossary from the file list.";
        nextTerm.Pressed += JumpToNextTerm;
        ovHeader.AddChild(nextTerm);
        _emptyLbl = new Label();
        _emptyLbl.AddThemeFontSizeOverride("font_size", 16);
        ovHeader.AddChild(_emptyLbl);
        var nextEmpty = ActionButton("Next empty ▼");
        nextEmpty.CustomMinimumSize = new Vector2(150, 36);
        nextEmpty.TooltipText =
            "Jump to the next untranslated (empty) entry.\n"
            + "Entries whose original text is empty are skipped — there is nothing to translate in them.";
        nextEmpty.Pressed += JumpToNextEmpty;
        ovHeader.AddChild(nextEmpty);
        ovCol.AddChild(ovHeader);

        // 검색 줄: "게임에서 본 그 문장이 어느 항목이냐" 를 푸는 유일한 수단. 내비게이터 3종은
        // 빈칸/원문변경/용어 불일치만 찾으므로, 이미 번역된 멀쩡한 줄은 어떤 버튼으로도 못 간다.
        var findRow = new HBoxContainer();
        findRow.AddChild(Lbl("Find:", GRAY));
        _findEdit = new LineEdit
        {
            PlaceholderText = "text you saw in game — searches original, translation and key",
            CustomMinimumSize = new Vector2(360, 36),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        // 타이핑 중에는 세고 칠하기만 한다(점프는 Enter/버튼). 칠하기가 즉시라서 "몇 번째 줄이냐"
        // 를 묻기 전에 눈으로 먼저 보인다.
        _findEdit.TextChanged += _ => { UpdateFindCount(); ApplyFindHighlight(); };
        _findEdit.TextSubmitted += _ => JumpToNextMatch();    // Enter 반복 = 다음 일치
        findRow.AddChild(_findEdit);
        _findLbl = new Label();
        _findLbl.AddThemeFontSizeOverride("font_size", 16);
        findRow.AddChild(_findLbl);
        var findNext = ActionButton("Find ▼");
        findNext.CustomMinimumSize = new Vector2(110, 36);
        findNext.TooltipText =
            "Jump to the next entry containing this text (Enter does the same; Ctrl+F focuses the box).\n"
            + "Matches the ORIGINAL text, your translation, or the key — so you can paste a line you saw\n"
            + "in game even if you haven't translated it yet. Color tags and !D! placeholders are ignored.";
        findNext.Pressed += () => JumpToNextMatch();
        findRow.AddChild(findNext);
        ovCol.AddChild(findRow);

        _editor = new CodeEdit
        {
            Text = TranslationStore.OverrideText(_mod.Id, _lang, _table),
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            GuttersDrawLineNumbers = true,
            HighlightCurrentLine = true, // 편집 중인 줄 = 원문에서 비추는 줄, 양쪽을 같은 방식으로
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        StyleFindHighlight(_editor);
        _editor.TextChanged += UpdateEmptyCount;
        _editor.TextChanged += UpdateFindCount;
        _editor.CaretChanged += SyncRefToCaret; // 캐럿이 놓인 키를 참조 패널이 따라온다
        _editor.GuiInput += FocusFindOnCtrlF;   // 편집 중 Ctrl+F → 검색 상자로
        ovCol.AddChild(_editor);
        UpdateStaleState(); // 원문 변경 항목 집합·카운트 계산(SyncRefToCaret 이 참조하기 전에)
        UpdateTermState();  // 용어집 불일치 항목 집합·카운트
        UpdateEmptyCount();
        SyncRefToCaret(); // 열자마자 첫 항목을 맞춰 둔다
        panes.AddChild(ovCol);

        _content!.AddChild(panes);

        var footer = new HBoxContainer();
        var save = ActionButton("Save"); save.Pressed += SaveEditor;
        var auto = ActionButton("Auto-fill ✨");
        auto.CustomMinimumSize = new Vector2(150, 40);
        bool hasKeyE = TranslationStore.LoadApiKey().Length > 0;
        auto.Disabled = !hasKeyE;
        auto.TooltipText = hasKeyE
            ? "Machine-translate the empty entries with DeepL (draft — review, then Save)."
            : "Set your DeepL API key first — use the \"DeepL key…\" button on the mods list.";
        auto.Pressed += () => OnAutoFill(auto);
        var reload = ActionButton("Reload File"); reload.Pressed += () =>
        {
            if (_editor != null) _editor.Text = TranslationStore.OverrideText(_mod.Id, _lang, _table);
            UpdateStaleState();
            UpdateTermState();
            UpdateEmptyCount();
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
                UpdateStaleState();
                UpdateTermState();
                UpdateEmptyCount();
                TranslationSync.ReloadFromDisk();
                SetStatus("Reset to original.", true, false);
            });
        footer.AddChild(save); footer.AddChild(auto); footer.AddChild(reload);
        footer.AddChild(up); footer.AddChild(reset);
        _content.AddChild(footer);
    }

    // ── 빈 항목 탐색 ────────────────────────────────────────────
    // ToPrettyJson 이 키당 한 줄을 보장하므로("KEY": "",) 라인 정규식으로 빈 값 항목을 찾는다.
    // 그룹 1 = 키 — 원문 조회에 쓴다.
    private static readonly Regex EmptyEntryRx =
        new(@"^\s*""((?:[^""\\]|\\.)+)""\s*:\s*""""\s*,?\s*$", RegexOptions.Compiled);

    /// <summary>
    /// 값이 빈 항목의 줄 번호. ★원문 자체가 빈 키는 제외한다 — 번역할 내용이 없어 채울 수 없고,
    /// 진행률도 그 키를 분모에서 빼므로 여기서 세면 "100% 인데 N empty" 라는 모순이 보인다.
    /// 원문에서 키를 못 찾으면(이스케이프 등) 안전하게 빈칸으로 취급해 노출한다.
    /// </summary>
    // 임의의 "KEY": … 항목 줄에서 키만 뽑는다(값은 보지 않음). ToPrettyJson 이 키당 한 줄을 보장.
    private static readonly Regex EntryKeyRx =
        new(@"^\s*""((?:[^""\\]|\\.)+)""\s*:", RegexOptions.Compiled);

    /// <summary>참조 패널의 키 → 줄 번호 인덱스를 다시 만든다(참조 언어를 바꿨을 때 등).</summary>
    private static void RebuildRefIndex()
    {
        _refLineByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        if (_srcEdit == null || !GodotObject.IsInstanceValid(_srcEdit)) return;
        int n = _srcEdit.GetLineCount();
        for (int i = 0; i < n; i++)
        {
            var m = EntryKeyRx.Match(_srcEdit.GetLine(i));
            if (m.Success) _refLineByKey[m.Groups[1].Value] = i;
        }
    }

    /// <summary>
    /// 번역 패널의 캐럿이 놓인 키를 참조 패널에서 찾아 화면 중앙에 맞춘다.
    /// ★줄 번호가 아니라 <b>키</b>로 맞추는 이유: 두 패널 모두 줄바꿈(Boundary)이 켜져 있어 같은
    /// 줄 번호라도 화면 행이 어긋나고, 참조 언어가 일부만 번역했거나 override 가 낡으면 키 집합
    /// 자체가 다르다(실측: 541쌍 중 51쌍 불일치). 줄로 맞추면 그 경우 조용히 다른 항목을 가리킨다.
    /// 참조에 없는 키면 아무것도 하지 않는다 — 마지막 위치를 유지하는 편이 엉뚱한 점프보다 낫다.
    /// </summary>
    private static void SyncRefToCaret()
    {
        if (_editor == null || !GodotObject.IsInstanceValid(_editor)) return;
        if (_srcEdit == null || !GodotObject.IsInstanceValid(_srcEdit)) return;
        var m = EntryKeyRx.Match(_editor.GetLine(_editor.GetCaretLine()));
        if (!m.Success) return; // 여는 중괄호·빈 줄 등 — 마지막 상태 유지
        string key = m.Groups[1].Value;
        bool stale = _staleKeys.Contains(key);
        if (_refLineByKey.TryGetValue(key, out int line))
        {
            _srcEdit.SetCaretLine(line);           // 현재 줄 하이라이트 = 어느 항목인지 눈에 보이게
            _srcEdit.SetLineAsCenterVisible(line);
            SetRefKey(key, missing: false, stale: stale);
        }
        else SetRefKey(key, missing: true, stale: stale); // 참조에 없는 키 — 패널은 두고 이유만 알린다
    }

    /// <summary>참조 헤더의 "지금 이 키" 표시. missing=참조 언어에 없음. stale=원문이 번역 후 바뀜.
    /// 용어집 불일치면 기대 표기를 함께 안내(원문변경 다음 우선순위).</summary>
    private static void SetRefKey(string key, bool missing, bool stale = false)
    {
        if (_refKeyLbl == null || !GodotObject.IsInstanceValid(_refKeyLbl)) return;
        bool term = !missing && !stale && _termInfoByKey.TryGetValue(key, out _);
        string suffix = missing ? "  — not in this reference"
                      : stale ? "  ⚠ original changed since you translated it"
                      : term ? $"  ⚠ glossary: {_termInfoByKey[key]}"
                      : "";
        _refKeyLbl.Text = $"  {key}{suffix}";
        _refKeyLbl.AddThemeColorOverride("font_color", missing || stale || term ? GOLD : WHITE);
    }

    // ── 원문 변경(stale) 내비게이터 ──────────────────────────────

    /// <summary>편집기의 stale 키 집합·옛 원문을 다시 계산하고 헤더 카운트를 갱신한다.</summary>
    private static void UpdateStaleState()
    {
        _staleKeys = new HashSet<string>(StringComparer.Ordinal);
        _staleOldByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_mod != null)
            foreach (var (key, old, _) in TranslationStore.StaleKeys(_mod, _lang, _table))
            {
                _staleKeys.Add(key);
                _staleOldByKey[key] = old;
            }
        if (_staleLbl != null && GodotObject.IsInstanceValid(_staleLbl))
        {
            int n = _staleKeys.Count;
            _staleLbl.Text = n == 0 ? "" : $"⚠ {n} changed  ";
            _staleLbl.AddThemeColorOverride("font_color", GOLD);
        }
    }

    /// <summary>캐럿 다음의 stale 항목 줄로 점프(끝이면 처음으로 wrap). 옛 원문을 상태줄에 보여 준다.</summary>
    private static void JumpToNextStale()
    {
        if (_editor == null || !GodotObject.IsInstanceValid(_editor)) return;
        var lines = KeyLines(_editor, _staleKeys);
        if (lines.Count == 0)
        {
            SetStatus(
                "No source-changed entries here — every translation still matches the current original.",
                true, false);
            return;
        }
        int cur = _editor.GetCaretLine();
        int next = lines.FirstOrDefault(l => l > cur, lines[0]); // wrap-around
        _editor.SetCaretLine(next);
        int q = _editor.GetLine(next).LastIndexOf('"');
        _editor.SetCaretColumn(Math.Max(0, q));
        _editor.CenterViewportToCaret();
        _editor.GrabFocus();
        var m = EntryKeyRx.Match(_editor.GetLine(next));
        string was = "";
        if (m.Success && _staleOldByKey.TryGetValue(m.Groups[1].Value, out var old))
        {
            string curSrc = _mod != null && _mod.EngByTable.TryGetValue(_table, out var e)
                            && e.TryGetValue(m.Groups[1].Value, out var c) ? c : "";
            string diff = SourceDiff.Describe(old, curSrc);
            was = diff.Length > 0 ? $"  Original changed:  {diff}"
                                  : $"  Was translated from: \"{Ellipsize(old, 80)}\"";
        }
        SetStatus($"Source-changed {lines.IndexOf(next) + 1}/{lines.Count} (line {next + 1}).{was}", true, false);
    }

    /// <summary>상태줄 표시용으로 긴 문자열을 자른다(줄바꿈은 공백으로).</summary>
    private static string Ellipsize(string s, int max)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    // ── 용어집 불일치 내비게이터 ─────────────────────────────────

    /// <summary>용어집 불일치 키 집합·안내를 다시 계산하고 헤더 카운트를 갱신한다.</summary>
    private static void UpdateTermState()
    {
        _termKeys = new HashSet<string>(StringComparer.Ordinal);
        _termInfoByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_mod != null)
            foreach (var (key, term, expected) in TranslationStore.GlossaryIssues(_mod, _lang, _table))
            {
                _termKeys.Add(key);
                // 한 항목에 여러 용어가 걸릴 수 있다 — 첫 안내만 헤더에 보여 준다(나머지는 카운트로).
                if (!_termInfoByKey.ContainsKey(key)) _termInfoByKey[key] = $"use \"{expected}\" for \"{term}\"";
            }
        if (_termLbl != null && GodotObject.IsInstanceValid(_termLbl))
        {
            int n = _termKeys.Count;
            _termLbl.Text = n == 0 ? "" : $"⚠ {n} term  ";
            _termLbl.AddThemeColorOverride("font_color", GOLD);
        }
    }

    /// <summary>지정 키 집합에 속하는 항목 줄 목록(편집기 우측 값에서 키를 뽑아 대조).</summary>
    private static List<int> KeyLines(TextEdit ed, HashSet<string> keys)
    {
        var lines = new List<int>();
        if (keys.Count == 0) return lines;
        int n = ed.GetLineCount();
        for (int i = 0; i < n; i++)
        {
            var m = EntryKeyRx.Match(ed.GetLine(i));
            if (m.Success && keys.Contains(m.Groups[1].Value)) lines.Add(i);
        }
        return lines;
    }

    /// <summary>캐럿 다음의 용어 불일치 항목으로 점프. 기대 표기를 상태줄에 안내.</summary>
    private static void JumpToNextTerm()
    {
        if (_editor == null || !GodotObject.IsInstanceValid(_editor)) return;
        var lines = KeyLines(_editor, _termKeys);
        if (lines.Count == 0)
        {
            SetStatus(
                _mod != null && TranslationStore.LoadModGlossary(_mod.Id, _lang).Count == 0
                    ? "No glossary set for this mod/language yet — add terms via \"Glossary…\" on the file list."
                    : "No glossary mismatches here — every entry uses the terms you set.",
                true, false);
            return;
        }
        int cur = _editor.GetCaretLine();
        int next = lines.FirstOrDefault(l => l > cur, lines[0]); // wrap-around
        _editor.SetCaretLine(next);
        int q = _editor.GetLine(next).LastIndexOf('"');
        _editor.SetCaretColumn(Math.Max(0, q));
        _editor.CenterViewportToCaret();
        _editor.GrabFocus();
        var m = EntryKeyRx.Match(_editor.GetLine(next));
        string hint = m.Success && _termInfoByKey.TryGetValue(m.Groups[1].Value, out var h) ? $"  Glossary: {h}" : "";
        SetStatus($"Term {lines.IndexOf(next) + 1}/{lines.Count} (line {next + 1}).{hint}", true, false);
    }

    private static List<int> EmptyEntryLines(TextEdit ed)
    {
        var src = _mod != null && _mod.EngByTable.TryGetValue(_table, out var e) ? e : null;
        var lines = new List<int>();
        int n = ed.GetLineCount();
        for (int i = 0; i < n; i++)
        {
            var m = EmptyEntryRx.Match(ed.GetLine(i));
            if (!m.Success) continue;
            if (src != null && src.TryGetValue(m.Groups[1].Value, out var s)
                && !SupportedMod.IsTranslatable(s)) continue; // 원문이 빔 — 채울 수 없는 항목
            lines.Add(i);
        }
        return lines;
    }

    private static void UpdateEmptyCount()
    {
        if (_editor == null || !GodotObject.IsInstanceValid(_editor)) return;
        if (_emptyLbl == null || !GodotObject.IsInstanceValid(_emptyLbl)) return;
        int n = EmptyEntryLines(_editor).Count;
        _emptyLbl.Text = n == 0 ? "all filled ✓  " : $"{n} empty  ";
        _emptyLbl.AddThemeColorOverride("font_color", n == 0 ? GRAY : GOLD);
    }

    /// <summary>캐럿 다음의 빈 항목 줄로 점프(끝이면 처음으로 wrap). 값의 "" 사이에 캐럿을 놓는다.</summary>
    private static void JumpToNextEmpty()
    {
        if (_editor == null || !GodotObject.IsInstanceValid(_editor)) return;
        var lines = EmptyEntryLines(_editor);
        UpdateEmptyCount();
        if (lines.Count == 0)
        {
            SetStatus("No empty entries in this file — everything is filled.", true, false);
            return;
        }
        int cur = _editor.GetCaretLine();
        int next = lines.FirstOrDefault(l => l > cur, lines[0]); // wrap-around
        _editor.SetCaretLine(next);
        string line = _editor.GetLine(next);
        int q = line.LastIndexOf('"');                 // 닫는 따옴표 → 그 앞("" 사이)에 캐럿
        _editor.SetCaretColumn(Math.Max(0, q));
        _editor.CenterViewportToCaret();
        _editor.GrabFocus();
        SetStatus($"Empty entry {lines.IndexOf(next) + 1}/{lines.Count} (line {next + 1}).", true, false);
    }

    // ── 텍스트 검색 ─────────────────────────────────────────────
    // "게임에서 본 이 문장이 어느 항목이냐" 를 푸는 경로. 내비게이터 3종(빈칸/원문변경/용어)은
    // 문제 있는 항목만 짚으므로, 이미 번역돼 있지만 어색한 줄은 검색 말고는 도달할 방법이 없다.

    /// <summary>편집기 줄에서 키와 값을 함께 뽑는다(ToPrettyJson 이 키당 한 줄을 보장).</summary>
    private static readonly Regex EntryPairRx =
        new(@"^\s*""((?:[^""\\]|\\.)+)""\s*:\s*""((?:[^""\\]|\\.)*)""\s*,?\s*$", RegexOptions.Compiled);

    private static readonly Regex BbCodeRx = new(@"\[/?[^\[\]]{0,40}\]", RegexOptions.Compiled);
    private static readonly Regex PlaceholderRx =
        new(@"![A-Za-z0-9_]{1,12}!|\{[^{}]{0,80}\}", RegexOptions.Compiled);
    private static readonly Regex SpaceRx = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// 검색 비교용 정규화. 화면에 <b>보이지 않는</b> 것(JSON 이스케이프·<c>[color]</c> 태그·
    /// <c>!D!</c>/<c>{…}</c> 자리표시자)을 지워, 게임에서 본 문장을 그대로 붙여넣어도 걸리게 한다.
    /// 검색어와 대상 양쪽에 똑같이 적용해야 의미가 있다.
    /// </summary>
    private static string NormForFind(string s)
    {
        if (s.Length == 0) return "";
        if (s.IndexOf('\\') >= 0)
            s = s.Replace("\\n", " ").Replace("\\t", " ").Replace("\\\"", "\"").Replace("\\\\", "\\");
        if (s.IndexOf('[') >= 0) s = BbCodeRx.Replace(s, " ");
        if (s.IndexOf('!') >= 0 || s.IndexOf('{') >= 0) s = PlaceholderRx.Replace(s, " ");
        return SpaceRx.Replace(s, " ").Trim().ToLowerInvariant();
    }

    /// <summary>
    /// 검색어와 일치하는 항목 줄 목록. <b>원문·번역·키</b> 중 하나라도 포함하면 일치 —
    /// 아직 번역하지 않은 항목은 화면에 원문이 그대로 나오므로 원문도 반드시 훑어야 한다.
    /// </summary>
    private static List<int> FindMatchLines(TextEdit ed, string needle)
    {
        var lines = new List<int>();
        if (needle.Length == 0) return lines;
        var src = _mod != null && _mod.EngByTable.TryGetValue(_table, out var e) ? e : null;
        int n = ed.GetLineCount();
        for (int i = 0; i < n; i++)
        {
            var m = EntryPairRx.Match(ed.GetLine(i));
            if (!m.Success) continue;
            string key = m.Groups[1].Value;
            if (NormForFind(m.Groups[2].Value).Contains(needle, StringComparison.Ordinal)
                || key.ToLowerInvariant().Contains(needle, StringComparison.Ordinal)
                || (src != null && src.TryGetValue(key, out var orig)
                    && NormForFind(orig).Contains(needle, StringComparison.Ordinal)))
                lines.Add(i);
        }
        return lines;
    }

    /// <summary>검색어가 비면 아무 일도 하지 않는다(빈 검색어로 전 줄을 훑지 않도록 먼저 끊는다).</summary>
    private static void UpdateFindCount()
    {
        if (_findEdit == null || !GodotObject.IsInstanceValid(_findEdit)) return;
        if (_findLbl == null || !GodotObject.IsInstanceValid(_findLbl)) return;
        _findQuery = NormForFind(_findEdit.Text);
        if (_findQuery.Length == 0) { _findLbl.Text = ""; return; }
        if (_editor == null || !GodotObject.IsInstanceValid(_editor)) return;
        int n = FindMatchLines(_editor, _findQuery).Count;
        _findLbl.Text = n == 0 ? "no match  " : $"{n} match{(n == 1 ? "" : "es")}  ";
        _findLbl.AddThemeColorOverride("font_color", n == 0 ? GRAY : GOLD);
    }

    /// <summary>
    /// 캐럿 다음의 일치 항목으로 점프(끝이면 처음으로 wrap). 참조 패널은 캐럿을 따라 같이 움직인다.
    /// ★포커스는 검색 상자에 둔다 — Enter 를 계속 눌러 일치 항목을 순회하는 편이, 첫 일치에서
    /// 편집기로 끌려가 Enter 가 줄바꿈이 돼 버리는 것보다 낫다(고칠 줄은 클릭해서 들어간다).
    /// </summary>
    private static void JumpToNextMatch()
    {
        if (_editor == null || !GodotObject.IsInstanceValid(_editor)) return;
        if (_findEdit == null || !GodotObject.IsInstanceValid(_findEdit)) return;
        _findQuery = NormForFind(_findEdit.Text);
        if (_findQuery.Length == 0)
        {
            SetStatus("Type the text you're looking for in the Find box.", true, false);
            return;
        }
        var lines = FindMatchLines(_editor, _findQuery);
        UpdateFindCount();
        if (lines.Count == 0)
        {
            SetStatus(
                $"Nothing in this file contains \"{Ellipsize(_findEdit.Text, 40)}\" — "
                + "try a shorter phrase (a single distinctive word), or another file.",
                true, false);
            return;
        }
        int cur = _editor.GetCaretLine();
        int next = lines.FirstOrDefault(l => l > cur, lines[0]); // wrap-around
        SelectQueryInLine(_editor, next, _findEdit.Text.Trim());
        _editor.CenterViewportToCaret();
        ApplyFindHighlight();
        SyncRefToCaret(); // 캐럿을 코드로 옮겼을 때 CaretChanged 가 안 오는 경우까지 대비
        SetStatus($"Match {lines.IndexOf(next) + 1}/{lines.Count} (line {next + 1}). Enter for the next one.",
            true, false);
    }

    /// <summary>
    /// 검색어를 <b>양쪽 패널</b>(번역·참조)에 칠한다. Godot TextEdit 의 검색 하이라이트는
    /// <b>있는 그대로</b> 대조하므로(정규화 없음), 마크업을 사이에 낀 문장은 점프는 되지만 칠해지지
    /// 않는다 — 칠하기는 보조 신호고 판정은 <see cref="FindMatchLines"/> 가 한다. 대소문자는 무시.
    /// </summary>
    private static void ApplyFindHighlight()
    {
        string q = _findEdit != null && GodotObject.IsInstanceValid(_findEdit) ? _findEdit.Text.Trim() : "";
        foreach (var ed in new[] { _editor, _srcEdit })
        {
            if (ed == null || !GodotObject.IsInstanceValid(ed)) continue;
            ed.SetSearchFlags(0); // 대소문자·단어경계 무시 — 찾는 쪽 규칙과 맞춘다
            ed.SetSearchText(q);
            ed.QueueRedraw();
        }
    }

    /// <summary>검색 하이라이트가 게임 패널(어두운 배경)에서 보이도록 색을 지정한다.</summary>
    private static void StyleFindHighlight(TextEdit ed)
    {
        ed.AddThemeColorOverride("search_result_color", new Color(0.93f, 0.77f, 0.40f, 0.30f));
        ed.AddThemeColorOverride("search_result_border_color", new Color(0.93f, 0.77f, 0.40f, 0.85f));
    }

    /// <summary>
    /// 점프한 줄에서 검색어 자리를 실제로 선택해 준다(원문에만 걸린 경우엔 값 끝에 캐럿).
    /// 하이라이트가 여러 개일 때 "지금 이거" 를 구분해 주는 역할.
    /// </summary>
    private static void SelectQueryInLine(TextEdit ed, int line, string rawQuery)
    {
        string text = ed.GetLine(line);
        int at = rawQuery.Length == 0 ? -1 : text.IndexOf(rawQuery, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            ed.SetCaretLine(line);
            ed.SetCaretColumn(at + rawQuery.Length);
            ed.Select(line, at, line, at + rawQuery.Length);
            return;
        }
        ed.SetCaretLine(line);
        int q = text.LastIndexOf('"');
        ed.SetCaretColumn(Math.Max(0, q));
        ed.Deselect();
    }

    /// <summary>편집기에서 Ctrl+F → 검색 상자로 포커스(이벤트를 소비해 'f' 가 입력되지 않게).</summary>
    private static void FocusFindOnCtrlF(InputEvent e)
    {
        if (e is not InputEventKey k || !k.Pressed || k.Echo) return;
        if (k.Keycode != Key.F || !k.CtrlPressed) return;
        if (_findEdit == null || !GodotObject.IsInstanceValid(_findEdit)) return;
        _findEdit.GrabFocus();
        _findEdit.SelectAll();
        _editor?.AcceptEvent();
    }

    // ── 자동 번역(DeepL) ────────────────────────────────────────
    private static bool _autoBusy;

    /// <summary>
    /// 편집기의 빈 값들을 DeepL 로 채운다(초안). 키가 없으면 먼저 입력 모달을 띄우고,
    /// 끝나면 결과를 편집기에 채워 넣되 *자동 저장하지 않는다*(사용자가 검수 후 Save).
    /// </summary>
    private static void OnAutoFill(Button btn)
    {
        if (_autoBusy || _mod == null || _editor == null) return;

        string key = TranslationStore.LoadApiKey();
        if (key.Length == 0)
        {
            PromptApiKey(() => OnAutoFill(btn)); // 키 저장 후 같은 동작 재시도
            return;
        }
        if (!AutoTranslator.SupportsLanguage(_lang))
        {
            SetStatus($"DeepL doesn't support '{_lang}'. You can still translate it by hand.", true, true);
            return;
        }

        var mod = _mod; string lang = _lang, table = _table, text = _editor.Text;
        _autoBusy = true;
        btn.Disabled = true;
        SetStatus("Auto-translating with DeepL…", true, false);

        _ = Task.Run(async () =>
        {
            var (ok, json, n, skipped, err) = await AutoTranslator.FillEditorAsync(mod, table, lang, text, key);
            // await 이후 연속실행은 Godot 메인 스레드가 아닐 수 있다 → 노드 접근은 CallDeferred 로 마샬.
            Callable.From(() =>
            {
                _autoBusy = false;
                if (GodotObject.IsInstanceValid(btn)) btn.Disabled = false;
                // 사용자가 그새 다른 뷰로 이동했으면 편집기에 쓰지 않는다.
                if (!ok) { SetStatus("Auto-translate failed: " + err, true, true); return; }
                TranslationStore.RecordTargetVersion(mod.Id, mod.Version); // 번역 기준 버전 기록
                if (_view == View.Editor && _editor != null && GodotObject.IsInstanceValid(_editor))
                {
                    _editor.Text = json;
                    UpdateEmptyCount();
                    int left = EmptyEntryLines(_editor).Count;
                    SetStatus(
                        $"Filled {n} entr{(n == 1 ? "y" : "ies")} via DeepL — review, then Save."
                        + (skipped > 0
                            ? $"  ({skipped} left empty: result failed the {{format}} safety check — translate by hand.)"
                            : "")
                        + (left > 0 ? $"  ({left} still empty — use \"Next empty ▼\" to find them.)" : ""),
                        true, skipped > 0);
                }
                else SetStatus($"Translated {n} entries (view changed — reopen to see).", true, false);
            }).CallDeferred();
        });
    }

    /// <summary>
    /// Files 뷰의 일괄 번역: 현재 모드/언어의 *모든* 테이블 빈 항목을 DeepL 로 채워 디스크에 저장.
    /// (편집기 검수 단계가 없어 바로 저장 — 이후 개별 파일을 편집기에서 수정 가능.)
    /// </summary>
    private static void OnAutoFillAll(Button btn)
    {
        if (_autoBusy || _mod == null) return;

        string key = TranslationStore.LoadApiKey();
        if (key.Length == 0) { PromptApiKey(() => OnAutoFillAll(btn)); return; }
        if (!AutoTranslator.SupportsLanguage(_lang))
        {
            SetStatus($"DeepL doesn't support '{_lang}'. You can still translate it by hand.", true, true);
            return;
        }

        var mod = _mod; string lang = _lang;
        Confirm(
            "Auto-fill all files",
            $"Machine-translate every empty entry for {lang} in {mod.Name} with DeepL and save? "
            + "Existing translations are kept; only blanks are filled. Review afterwards.",
            "Translate all",
            () =>
            {
                _autoBusy = true;
                btn.Disabled = true;
                SetStatus("Auto-translating all files…", true, false);
                _ = Task.Run(async () =>
                {
                    var (ok, filled, skipped, files, err) = await AutoTranslator.FillAllTablesAsync(
                        mod, lang, key,
                        (i, n, t) => Callable.From(() =>
                            SetStatus($"Translating {i}/{n}: {t}.json…", true, false)).CallDeferred());
                    Callable.From(() =>
                    {
                        _autoBusy = false;
                        if (GodotObject.IsInstanceValid(btn)) btn.Disabled = false;
                        if (!ok) { SetStatus("Auto-translate failed: " + err, true, true); return; }
                        TranslationStore.RecordTargetVersion(mod.Id, mod.Version); // 번역 기준 버전 기록
                        TranslationSync.ReloadFromDisk();
                        string warn = err.Length > 0 ? $"  (stopped early: {err})" : "";
                        string skip = skipped > 0
                            ? $"  ({skipped} left empty: result failed the {{format}} safety check — translate by hand.)"
                            : "";
                        SetStatus(
                            $"Filled {filled} entr{(filled == 1 ? "y" : "ies")} across {files} file(s) — "
                            + $"review & edit as needed.{skip}{warn}", true, err.Length > 0 || skipped > 0);
                        if (_view == View.Files) RebuildContent();
                    }).CallDeferred();
                });
            });
    }

    /// <summary>DeepL API 키 입력 모달(AcceptDialog + LineEdit). 저장 시 onSaved 실행.</summary>
    private static void PromptApiKey(Action? onSaved = null)
    {
        if (_root == null || !GodotObject.IsInstanceValid(_root)) return;
        var dlg = new AcceptDialog
        {
            Title = "DeepL API key",
            OkButtonText = "Save",
            MinSize = new Vector2I(620, 0),
        };
        var box = new VBoxContainer();
        box.AddChild(Lbl("Paste your DeepL API key. The free tier (500,000 chars/month) works —", GRAY));
        box.AddChild(Lbl("get one at deepl.com/pro-api. Stored locally only; never bundled into exports.", GRAY));
        var le = new LineEdit
        {
            Text = TranslationStore.LoadApiKey(),
            PlaceholderText = "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx:fx",
            CustomMinimumSize = new Vector2(560, 36),
        };
        box.AddChild(le);
        dlg.AddChild(box);

        dlg.Confirmed += () =>
        {
            TranslationStore.SaveApiKey(le.Text);
            if (le.Text.Trim().Length > 0) onSaved?.Invoke();
            if (GodotObject.IsInstanceValid(dlg)) dlg.QueueFree();
        };
        dlg.Canceled += () => { if (GodotObject.IsInstanceValid(dlg)) dlg.QueueFree(); };
        _root.AddChild(dlg);
        dlg.PopupCentered();
    }

    /// <summary>
    /// AI 에이전트로 번역을 시작하는 법을 안내. 킷 자체는 부팅마다 자동 생성되므로 여기서는
    /// 멱등하게 갱신만 하고(폴더를 손댔거나 세션 중 언어를 바꾼 경우 대비) 다음 행동을 알려준다.
    /// 탐색기만 열면 사용자는 거기서 터미널을 어떻게 여는지 모른다 — 경로 복사 + 3단계가 핵심.
    /// </summary>
    private static void PromptAiKit()
    {
        if (_root == null || !GodotObject.IsInstanceValid(_root)) return;
        string lang = TranslationSync.CurrentLanguage();
        AiKitWriter.Write(lang);

        string root = TranslationStore.Root;
        try { DisplayServer.ClipboardSet(root); }
        catch (Exception ex) { MainFile.Logger.Warn($"[Sts2ModTranslator] 클립보드 복사 실패: {ex.Message}"); }

        var dlg = new AcceptDialog
        {
            Title = "Translate with AI",
            OkButtonText = "Open folder",
            MinSize = new Vector2I(680, 0),
        };
        var box = new VBoxContainer();
        box.AddChild(Lbl("This folder is ready for AI coding agents (Claude Code and similar).", GRAY));
        box.AddChild(Lbl($"Translation rules and the official '{lang}' term glossary are already written here,", GRAY));
        box.AddChild(Lbl("so you don't need to explain the format or paste any path.", GRAY));
        var path = new LineEdit
        {
            Text = root,
            Editable = false,
            CustomMinimumSize = new Vector2(640, 36),
        };
        box.AddChild(path);
        box.AddChild(Lbl("  1.  Open a terminal in this folder   (path copied to clipboard)", WHITE));
        box.AddChild(Lbl("  2.  Run:  claude", WHITE));
        box.AddChild(Lbl($"  3.  Ask:  \"translate <mod name> into {lang}\"", WHITE));
        box.AddChild(Lbl("The agent picks up the rules on its own. Press Reload here when it finishes.", GRAY));
        dlg.AddChild(box);

        dlg.Confirmed += () => { OpenFolder(); if (GodotObject.IsInstanceValid(dlg)) dlg.QueueFree(); };
        dlg.Canceled += () => { if (GodotObject.IsInstanceValid(dlg)) dlg.QueueFree(); };
        _root.AddChild(dlg);
        dlg.PopupCentered();
    }

    private static void SaveEditor()
    {
        if (_mod == null || _editor == null) return;
        var src = _mod.EngByTable.TryGetValue(_table, out var e) ? e : null;
        var (ok, err) = TranslationStore.SaveOverrideText(_mod.Id, _lang, _table, _editor.Text, src);
        if (!ok) { SetStatus("Save failed: " + err, true, true); return; }
        // 이 대상 모드를 "지금 버전 기준으로 번역했다"고 기록(이후 모드 업데이트 시 싱크 경고 기준).
        TranslationStore.RecordTargetVersion(_mod.Id, _mod.Version);
        UpdateStaleState(); // 방금 저장으로 (재)번역된 stale 항목은 목록에서 빠진다
        UpdateTermState();  // 용어를 반영해 저장했으면 불일치가 해소된다
        var badKeys = TranslationSync.InvalidFormatKeys(_editor.Text);
        int n = TranslationSync.ReloadFromDisk();
        if (badKeys.Count > 0)
            SetStatus(
                $"Saved ({n} keys active), but {badKeys.Count} entr{(badKeys.Count == 1 ? "y has" : "ies have")} "
                + "invalid {format} syntax (unbalanced braces?) and were NOT applied: "
                + string.Join(", ", badKeys.Take(3)) + (badKeys.Count > 3 ? $", +{badKeys.Count - 3} more" : ""),
                true, true);
        else
            SetStatus($"Saved & applied ({n} keys active).", true, false);
    }

    /// <summary>주입에서 걸러진(문법 깨진) 항목이 있으면 상태 메시지에 경고를 덧붙인다.</summary>
    private static string WithFormatWarning(string msg)
    {
        int bad = TranslationSync.LastInjectInvalidCount;
        return bad == 0 ? msg
            : msg + $"  ⚠ {bad} entr{(bad == 1 ? "y has" : "ies have")} invalid {{format}} syntax and "
                  + "were NOT applied — fix them (see the log for keys).";
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
        string mid = _mod.Id, lang = _lang, t = table, ver = _mod.Version;
        var src = _mod.EngByTable.TryGetValue(table, out var e) ? e : null;
        dlg.FileSelected += (string path) =>
        {
            var (ok, err) = TranslationStore.ImportInto(mid, lang, t, path, src);
            if (ok)
            {
                TranslationStore.RecordTargetVersion(mid, ver); // 번역 기준 버전 기록
                TranslationSync.ReloadFromDisk(); SetStatus($"Uploaded {t}.json.", true, false); RebuildContent();
            }
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

    /// <summary>STS 언어 코드 → 사람이 읽는 이름(안내 문구용). 모르면 코드 그대로.</summary>
    internal static string LangDisplay(string stsLang) => (stsLang ?? "").ToLowerInvariant() switch
    {
        "eng" => "English",
        "kor" => "Korean",
        "jpn" => "Japanese",
        "zhs" or "chs" => "Chinese (Simplified)",
        "zht" or "cht" => "Chinese (Traditional)",
        "fra" or "fre" => "French",
        "deu" or "ger" => "German",
        "esp" or "spa" => "Spanish",
        "rus" => "Russian",
        "ptb" => "Portuguese (BR)",
        "por" => "Portuguese",
        "ita" => "Italian",
        "pol" => "Polish",
        "nld" or "dut" => "Dutch",
        "tur" => "Turkish",
        "ukr" => "Ukrainian",
        _ => stsLang ?? "",
    };
}
