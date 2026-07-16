using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;

namespace Sts2ModTranslator.Core;

/// <summary>
/// SetLanguage postfix 가 호출하는 오케스트레이터.
///   - 첫 진입(모드 로드 완료 후)에 스캔 + 템플릿/원문 추출 + 리포트 출력 (언어별 1회).
///   - 매 호출마다 해당 언어의 번역 override 를 로크 테이블에 주입.
/// </summary>
public static class TranslationSync
{
    private static ScanResult? _scan;                       // 최신 스캔 캐시
    private static int _lastLoadedCount = -1;               // 마지막 스캔 시점의 로드된 모드 수
    private static readonly HashSet<string> _prepped = new(); // "lang\0modId" → fs 준비 완료

    /// <summary>인게임 패널용: 최근 스캔 결과(지원/미지원 모드).</summary>
    public static ScanResult? CurrentScan => _scan;

    /// <summary>현재 게임 언어 코드(예: kor). 없으면 eng.</summary>
    public static string CurrentLanguage()
    {
        try { return LocManager.Instance?.Language ?? "eng"; }
        catch { return "eng"; }
    }

    /// <summary>
    /// 게임이 지원하는 전체 언어 코드(14종, 선언 순서). 번역기는 현재 설정 언어뿐 아니라
    /// 이 전체 목록을 편집 대상으로 노출한다. API 실패 시 현재 언어 1종만 폴백.
    /// </summary>
    public static IReadOnlyList<string> SupportedLanguages()
    {
        try
        {
            var langs = LocManager.Languages;
            if (langs != null && langs.Count > 0) return langs;
        }
        catch { /* 정적 생성자 미초기화 등 — 폴백 */ }
        return new[] { CurrentLanguage() };
    }

    /// <summary>
    /// 디스크의 override 를 다시 읽어 현재 언어에 재주입 + 리포트 갱신(패널 'Reload' 버튼).
    /// 비어 있지 않은 값만 적용하므로 재시작 없이 편집분이 반영된다. 주입된 키 수 반환.
    /// </summary>
    public static int ReloadFromDisk()
    {
        LastInjectInvalidCount = 0; // 재주입 전 이전 카운트 초기화
        var mgr = LocManager.Instance;
        if (mgr == null) return 0;
        var scan = EnsureScan();
        if (scan == null) return 0;
        string lang = mgr.Language;
        // eng 도 스킵하지 않는다 — eng 폴더에 비영어 원문을 담은 모드는 eng 번역을 재주입해야 한다.
        // 자기 원문 언어인 모드는 Inject 안에서 ContentLang 기준으로 걸러진다.
        TranslationStore.WriteReport(scan, lang);
        int n = Inject(mgr, scan, lang);
        RefreshLabels(mgr); // 이미 렌더된 라벨(메인메뉴 등)도 즉시 다시 읽게 통지
        return n;
    }

    /// <summary>
    /// 이미 렌더된 라벨/버튼을 즉시 다시 읽게 한다.
    /// 게임의 LocTextLabel/NButton 등은 Godot 번역변경 알림(NOTIFICATION_TRANSLATION_CHANGED=2010)으로
    /// 재로컬라이즈한다. LocManager.TriggerLocaleChange 는 같은 locale 이면 SetLocale 이 no-op 이라 알림이
    /// 안 뜨므로, 트리 전체에 알림 2010 을 직접 전파한다.
    /// </summary>
    private static void RefreshLabels(LocManager mgr)
    {
        try
        {
            if (Godot.Engine.GetMainLoop() is Godot.SceneTree tree && tree.Root != null)
                tree.Root.PropagateNotification(2010); // NOTIFICATION_TRANSLATION_CHANGED
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Sts2ModTranslator] locale refresh 실패: {ex.Message}");
        }
    }

    /// <summary>메인 메뉴 버튼 라벨 로크 키. "main_menu_ui" 테이블에 주입한다.</summary>
    public const string MenuLabelKey = "STS2MODTRANSLATOR-MENU";

    /// <summary>
    /// 우리 모드 자체 UI 문자열(메뉴 버튼 라벨 등)을 게임 로크 테이블에 주입.
    /// 언어 전환 시 테이블이 재생성되므로 매 SetLanguage 마다 다시 넣는다(영문 고정).
    /// </summary>
    public static void EnsureUiStrings(LocManager mgr)
    {
        if (mgr == null) return;
        try
        {
            mgr.GetTable("main_menu_ui").MergeWith(new Dictionary<string, string>
            {
                [MenuLabelKey] = "Mod Translator",
            });
        }
        catch { /* main_menu_ui 테이블 부재 등 — 라벨만 영향, 무시 */ }
    }

    public static void OnLanguageLoaded(LocManager locMgr, string language)
    {
        if (locMgr == null || string.IsNullOrEmpty(language)) return;

        EnsureUiStrings(locMgr);   // 메뉴 라벨은 모든 언어에서 필요

        // 모드가 아직 로드되지 않은 이른 SetLanguage 호출 → null(이후 호출에서 처리됨).
        var scan = EnsureScan();
        if (scan == null) return;

        // 전역 eng 스킵을 두지 않는다 — eng 폴더에 비영어 원문(예: 한국어)을 담은 모드는 eng 로
        // 플레이할 때 오히려 번역(영어 override)을 주입해야 한다. "자기 원문 언어면 스킵" 은
        // EnsureTemplates/Inject 안에서 모드별 ContentLang 기준으로 처리한다.
        EnsureTemplates(scan, language); // 신규(또는 늦게 로드된) 모드만 증분 준비
        Inject(locMgr, scan, language);
    }

    /// <summary>
    /// 메인 메뉴 준비 시점(= 모드 로드 완료가 보장되는 늦은 지점)에서 한 번 더 스캔+주입.
    /// 부팅 때 SetLanguage 가 모드 로드보다 먼저 불려 번역이 누락되던 레이스를 보정한다.
    /// 부팅 SetLanguage 가 단 한 번뿐인 환경(사용자가 언어를 바꾸지 않음)도 여기서 구제된다.
    /// </summary>
    public static void OnMainMenuReady(LocManager locMgr)
    {
        if (locMgr == null) return;
        try
        {
            var scan = EnsureScan();
            if (scan == null) return;
            string lang = locMgr.Language;
            if (string.IsNullOrEmpty(lang)) return;
            // eng 도 스킵하지 않는다 — 모드별 ContentLang 게이팅이 EnsureTemplates/Inject 안에서 처리.
            EnsureTemplates(scan, lang);
            Inject(locMgr, scan, lang);
            RefreshLabels(locMgr); // 이미 그려진 메인메뉴 라벨도 즉시 재로컬라이즈
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Sts2ModTranslator] main-menu refresh 실패: {ex.Message}");
        }
    }

    /// <summary>
    /// 로드된 모드를 (재)스캔. 첫 SetLanguage 시점에 아직 로드되지 않았던 모드가 있어도
    /// 로드된 모드 수가 바뀌면 다시 스캔해 이후에 합류시킨다(부분 스캔을 영구 캐시하던 레이스 수정).
    /// 반환: 지원/미지원 합쳐 1개 이상이면 결과, 아무 모드도 없으면 기존 캐시(없으면 null).
    /// </summary>
    private static ScanResult? EnsureScan()
    {
        int loaded;
        try { loaded = ModManager.GetLoadedMods().Count(); }
        catch { loaded = -1; }

        // 캐시가 있고 로드된 모드 수에 변화가 없으면 재스캔 불필요.
        if (_scan != null && loaded == _lastLoadedCount) return _scan;

        var scan = ModLocScanner.Scan();
        if (scan.Supported.Count == 0 && scan.Unsupported.Count == 0)
            return _scan; // 아직 로드된 모드 없음 — 기존 캐시 유지(없으면 null)

        _scan = scan;
        _lastLoadedCount = loaded;
        MainFile.Logger.Info(
            $"[Sts2ModTranslator] scan: supported={scan.Supported.Count} unsupported={scan.Unsupported.Count}");
        return _scan;
    }

    /// <summary>
    /// 아직 준비되지 않은 (언어, 모드) 조합에 대해서만 원문 추출 + 번역 템플릿 생성.
    /// 늦게 로드돼 새로 합류한 모드도 빠짐없이 준비된다. 신규 준비가 있으면 리포트도 갱신.
    /// </summary>
    private static void EnsureTemplates(ScanResult scan, string language)
    {
        bool wrote = false;
        foreach (var mod in scan.Supported)
        {
            // 이 모드의 실제 원문 언어로 플레이 중이면 번역 대상 아님(자기 원문) — 템플릿 불필요.
            if (string.Equals(language, mod.ContentLang, StringComparison.OrdinalIgnoreCase)) continue;
            string key = language + "\0" + mod.Id;
            if (!_prepped.Add(key)) continue; // 이미 준비됨
            try { TranslationStore.EnsureTemplates(mod, language); wrote = true; }
            catch (Exception ex)
            {
                _prepped.Remove(key); // 실패 → 다음 호출에서 재시도
                MainFile.Logger.Warn($"[Sts2ModTranslator] 템플릿 생성 실패 {mod.Id}: {ex.Message}");
            }
        }
        if (wrote)
        {
            TranslationStore.WriteReport(scan, language);
            MainFile.Logger.Info(
                $"[Sts2ModTranslator] templates+report ready for '{language}' → {TranslationStore.Root}");
        }
    }

    // ── SmartFormat 검증 게이트 ─────────────────────────────────

    /// <summary>직전 주입에서 걸러낸(문법 깨진) 항목 수. 패널 상태줄 표시용.</summary>
    public static int LastInjectInvalidCount { get; private set; }

    private const int MaxInvalidLogPerTable = 10;

    /// <summary>
    /// LocValidator 안전 래퍼. SmartFormat 파서는 일부 기형 문자열(예: "{X:diff(" — solo-verify
    /// 실측)에서 ParsingErrors 대신 IndexOutOfRangeException 을 던진다. 그런 문자열은 런타임
    /// 렌더링에서도 게임의 catch(FormattingException|ParsingErrors) 를 뚫고 나가므로(크래시 경로)
    /// 예외 = 무효로 판정해 반드시 걸러낸다.
    /// </summary>
    internal static bool TryValidateFormat(string text, out string? error)
    {
        try { return LocValidator.ValidateFormatString(text, out error); }
        catch (Exception ex)
        {
            error = $"format parser crashed ({ex.GetType().Name}) — invalid";
            return false;
        }
    }

    /// <summary>
    /// 주입 직전 SmartFormat 문법 검증. 게임 자체 localization_override 로더는 LocValidator 로
    /// 깨진 항목을 걸러 적용하지 않지만, MergeWith 런타임 주입은 그 검증을 우회한다.
    /// 깨진 포맷 문자열은 카드 설명이 그려질 때마다(파일 이동/타겟팅 호버/co-op 카드 인텐트)
    /// SmartFormat 예외 → StackTrace 생성 + 동기 로그 + Sentry 캡처를 반복시켜 전투 중 지속
    /// 스터터를 유발한다. 여기서 게임과 동일한 검증을 적용해 유효 항목만 통과시킨다.
    /// dict 는 SimpleLocCompat.ApplyAll 이 만든 새 dict 만 받는다(제자리 제거 안전).
    /// </summary>
    private static Dictionary<string, string> FilterValidFormats(Dictionary<string, string> dict, string where)
    {
        List<string>? bad = null;
        foreach (var kv in dict)
        {
            if (TryValidateFormat(kv.Value, out string? err)) continue;
            bad ??= new List<string>();
            bad.Add(kv.Key);
            if (bad.Count <= MaxInvalidLogPerTable)
                MainFile.Logger.Warn(
                    $"[Sts2ModTranslator] invalid format — not applied: {where}/{kv.Key}: {err}");
        }
        if (bad == null) return dict; // 전부 유효(일반 경로) — 추가 비용 없음
        if (bad.Count > MaxInvalidLogPerTable)
            MainFile.Logger.Warn(
                $"[Sts2ModTranslator] …and {bad.Count - MaxInvalidLogPerTable} more invalid entries in {where}.");
        LastInjectInvalidCount += bad.Count;
        foreach (var k in bad) dict.Remove(k);
        return dict;
    }

    /// <summary>
    /// override JSON 텍스트에서 SmartFormat 문법이 깨진 (비어 있지 않은) 값의 키 목록.
    /// 편집기 Save/파일 목록의 즉시 경고용 — 깨진 중괄호는 JSON 으로는 유효해서
    /// 기존 JSON 오류 경고에 잡히지 않는다. 주입 게이트와 동일하게 SimpleLoc 변환 후 검증.
    /// JSON 자체가 깨진 텍스트는 빈 목록(기존 JSON 오류 경고가 담당).
    /// </summary>
    public static List<string> InvalidFormatKeys(string json)
    {
        var bad = new List<string>();
        Dictionary<string, string>? d;
        try { d = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
        catch { return bad; }
        if (d == null) return bad;
        foreach (var kv in d)
        {
            if (string.IsNullOrEmpty(kv.Value)) continue;
            if (!TryValidateFormat(SimpleLocCompat.Apply(kv.Value), out _))
                bad.Add(kv.Key);
        }
        return bad;
    }

    private static int Inject(LocManager locMgr, ScanResult scan, string language)
    {
        LastInjectInvalidCount = 0;
        int translated = 0;
        var supportedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mod in scan.Supported)
        {
            supportedIds.Add(mod.Id); // 게이트보다 먼저 — bundled 루프가 중복 주입하지 않게.
            // 이 모드의 실제 원문 언어로 플레이 중이면 원문 그대로가 정답 — 주입 스킵.
            // (한국어를 eng/ 에 담은 모드는 eng≠ContentLang 이라 eng 주입이 정상 진행된다.)
            if (string.Equals(language, mod.ContentLang, StringComparison.OrdinalIgnoreCase)) continue;
            // 이 대상 모드에 설치된 번역 모드가 제공한 (언어별) 번역.
            var bundledForMod = scan.Bundled.ForTargetLang(mod.Id, language);

            // eng 테이블 ∪ 번역 모드가 제공한 테이블. 모든 키를 명시적으로 설정(번역값 or 원본 기본값).
            // → 로컬 번역을 비우면 번역 모드값→원문 순으로 되돌아온다(MergeWith 는 제거를 못 하므로 필수).
            var tables = new HashSet<string>(mod.EngByTable.Keys, StringComparer.Ordinal);
            if (bundledForMod != null) foreach (var t in bundledForMod.Keys) tables.Add(t);

            foreach (var table in tables)
            {
                var bundledTbl = bundledForMod != null && bundledForMod.TryGetValue(table, out var bt) ? bt : null;
                var dict = TranslationStore.BuildInjectTable(mod, language, table, bundledTbl);
                if (dict.Count == 0) continue;
                LocTable? lt = TryGetTable(locMgr, table);
                if (lt == null) continue; // 게임에 없는 테이블 — 스킵
                // 주입 전 BaseLib SimpleLoc 저작 문법(#, !Var!, *gold*, [E] 등)을 STS2 네이티브로 변환.
                // BaseLib 미사용 모드/일반 값은 그대로 통과(무해). → 게임에 '!Var!' 원형이 노출되던 문제 해소.
                // 변환 후 SmartFormat 문법 검증을 통과한 항목만 주입(깨진 항목은 렌더링마다 예외 유발).
                try { lt.MergeWith(FilterValidFormats(SimpleLocCompat.ApplyAll(dict), $"{mod.Id}/{table}")); }
                catch (Exception ex)
                {
                    MainFile.Logger.Warn($"[Sts2ModTranslator] merge 실패 {mod.Id}/{table}: {ex.Message}");
                }
            }
            translated += TranslationStore.Coverage(mod, language).translated;
        }

        // 스캔에서 "지원" 으로 잡히지 않은 대상(localization 폴더 없이 런타임에 로크 테이블을
        // 등록하는 모드 등)도 번역 모드가 번역했다면 직접 주입한다(원문 기준이 없으므로 그대로 merge).
        foreach (var (targetId, byLang) in scan.Bundled.ByTarget)
        {
            if (supportedIds.Contains(targetId)) continue;
            if (!byLang.TryGetValue(language, out var byTable)) continue;
            foreach (var (table, dict) in byTable)
            {
                if (dict.Count == 0) continue;
                LocTable? lt = TryGetTable(locMgr, table);
                if (lt == null) continue;
                // 번역 팩(bundled)도 동일하게 SimpleLoc 문법 변환 + 검증 후 주입(ApplyAll 이 새 dict 생성).
                try { lt.MergeWith(FilterValidFormats(SimpleLocCompat.ApplyAll(dict), $"{targetId}/{table}")); }
                catch (Exception ex)
                {
                    MainFile.Logger.Warn($"[Sts2ModTranslator] bundled merge 실패 {targetId}/{table}: {ex.Message}");
                }
            }
        }

        MainFile.Logger.Info(
            $"[Sts2ModTranslator] applied translations for '{language}': {translated} active overrides"
            + (scan.Bundled.Any ? $", {scan.Bundled.Providers.Count} translation pack(s)" : "") + ".");
        return translated;
    }

    private static LocTable? TryGetTable(LocManager locMgr, string table)
    {
        try { return locMgr.GetTable(table); }
        catch { return null; } // GetTable 은 미존재 시 LocException throw
    }
}
