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
        var mgr = LocManager.Instance;
        if (mgr == null) return 0;
        var scan = EnsureScan();
        if (scan == null) return 0;
        string lang = mgr.Language;
        int n = 0;
        if (!string.Equals(lang, "eng", StringComparison.OrdinalIgnoreCase))
        {
            TranslationStore.WriteReport(scan, lang);
            n = Inject(mgr, scan, lang);
        }
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

        // eng 로 플레이 중이면 번역 대상 아님(원문). 템플릿/주입 모두 불필요.
        if (string.Equals(language, "eng", StringComparison.OrdinalIgnoreCase)) return;

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
            if (string.IsNullOrEmpty(lang) ||
                string.Equals(lang, "eng", StringComparison.OrdinalIgnoreCase)) return;
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

    private static int Inject(LocManager locMgr, ScanResult scan, string language)
    {
        int translated = 0;
        foreach (var mod in scan.Supported)
        {
            // 모든 테이블의 모든 키를 명시적으로 설정(번역값 or 원본 기본값).
            // → 값을 비우면 기본값으로 되돌아온다(MergeWith 는 제거를 못 하므로 필수).
            foreach (var table in mod.EngByTable.Keys)
            {
                var dict = TranslationStore.BuildInjectTable(mod, language, table);
                if (dict.Count == 0) continue;
                LocTable? lt = TryGetTable(locMgr, table);
                if (lt == null) continue; // 게임에 없는 테이블 — 스킵
                try { lt.MergeWith(dict); }
                catch (Exception ex)
                {
                    MainFile.Logger.Warn($"[Sts2ModTranslator] merge 실패 {mod.Id}/{table}: {ex.Message}");
                }
            }
            translated += TranslationStore.Coverage(mod, language).translated;
        }
        MainFile.Logger.Info(
            $"[Sts2ModTranslator] applied translations for '{language}': {translated} active overrides.");
        return translated;
    }

    private static LocTable? TryGetTable(LocManager locMgr, string table)
    {
        try { return locMgr.GetTable(table); }
        catch { return null; } // GetTable 은 미존재 시 LocException throw
    }
}
