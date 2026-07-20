using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MegaCrit.Sts2.Core.Modding;

namespace Sts2ModTranslator.Core;

/// <summary>지원 모드 한 개의 추출된 로컬라이제이션 정보.</summary>
public sealed class SupportedMod
{
    public string Id = "";
    public string Name = "";
    public string Version = "";

    /// <summary>모드가 동봉한 언어 폴더 목록 (e.g. eng, esp, zhs).</summary>
    public List<string> ShipsLangs = new();

    /// <summary>모드가 동봉한 모든 언어: lang → 테이블명 → (loc key → 텍스트).</summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, string>>> ByLang = new();

    /// <summary>
    /// 원문(기준) 언어 <b>폴더</b>. 보통 eng 지만, eng 를 동봉하지 않은 모드는 동봉 언어 중 가장 키가 많은
    /// 언어를 원문으로 잡는다(<see cref="ModLocScanner.Scan"/>). 키 집합/템플릿/참조의 단일 출처이자
    /// <see cref="ByLang"/> 조회 키. <b>실제</b> 텍스트 언어는 <see cref="ContentLang"/> 를 볼 것.
    /// </summary>
    public string SourceLang = "eng";

    /// <summary>
    /// 원문 텍스트의 <b>실제</b> 언어(스크립트 감지 결과, STS 코드). 폴더 이름(<see cref="SourceLang"/>)과
    /// 다를 수 있다 — 모더가 네이티브 텍스트(한/일/중)를 게임 폴백 폴더인 eng/ 에 그대로 넣는 흔한 패턴
    /// (예: SlayTheUniverse = 한국어 in eng/). DeepL source_lang 과 UI 표기에 이 값을 쓴다.
    /// <b>ByLang 조회 키로는 쓰지 말 것</b>(그건 <see cref="SourceLang"/>). 라틴/판별불가면 SourceLang 과 동일.
    /// </summary>
    public string ContentLang = "eng";

    /// <summary>
    /// 원문 폴더가 <b>두 언어를 섞어</b> 담고 있는지(예: 대부분 영어 + 일부 중국어 — Black Souls 처럼
    /// 부분만 번역된 모드, 또는 중국어 + 일부 한국어/일본어). <see cref="ContentLang"/> 과 <b>다른 언어</b>인
    /// 항목이 2개 이상일 때 true(CJK↔라틴뿐 아니라 CJK끼리도 감지, 비율 무관). 이때는 원문 언어
    /// (<see cref="ContentLang"/>) 자체를 "✎ 텍스트 편집" 진입점으로 노출해, 섞인 외국어 항목만 골라
    /// 덮어쓸 수 있게 한다. 자동번역도 이 플래그가 켜지면 source_lang 을 생략해 항목별 언어를 DeepL 이
    /// 자동감지한다. 단일 언어 모드는 false.
    /// </summary>
    public bool HasMixedSource;

    /// <summary>원문(<see cref="SourceLang"/>) 테이블. 키 집합/템플릿의 단일 출처.</summary>
    public Dictionary<string, Dictionary<string, string>> EngByTable =>
        ByLang.TryGetValue(SourceLang, out var d) ? d : new();

    /// <summary>
    /// 번역 대상 키 수. <b>원문 값이 빈 키는 제외</b>한다 — 번역할 것이 없어 영원히 채워지지 않으므로
    /// 분모에 남겨두면 진행률이 100%에 도달하지 못한다(예: SlayTheUniverse 26/1479 → 최대 98.2%).
    /// </summary>
    public int TotalKeys => EngByTable.Values.Sum(d => d.Values.Count(IsTranslatable));

    /// <summary>원문이 비어 있으면 번역 대상이 아니다 — 진행률·빈칸 내비게이터 공통 판정.</summary>
    public static bool IsTranslatable(string? sourceValue) => !string.IsNullOrEmpty(sourceValue);
}

/// <summary>미지원 모드 한 개 + 사유.</summary>
public sealed class UnsupportedMod
{
    public string Id = "";
    public string Name = "";
    public string Reason = "";
}

public sealed class ScanResult
{
    public List<SupportedMod> Supported = new();
    public List<UnsupportedMod> Unsupported = new();

    /// <summary>
    /// 설치된 "번역 모드"(Sts2ModTranslator 를 참조해 번역 JSON 을 동봉한 모드)가 제공하는
    /// 번역의 집계본. 부팅 시 자동 감지되어 런타임 주입에 사용된다.
    /// </summary>
    public BundledTranslations Bundled = new();
}

/// <summary>
/// 로드된 모드를 게임의 로크 경로 규약(res://{id}/localization/{lang}/{file})으로 스캔.
/// localization 폴더 + 읽을 수 있는 언어 테이블이 하나라도 있는 모드를 "지원" 으로 분류한다.
/// 원문 기준 언어는 eng 우선, eng 가 없으면 가장 키가 많은 동봉 언어로 폴백한다.
/// </summary>
public static class ModLocScanner
{
    private const string PreferredSourceLang = "eng"; // 원문 우선 언어 (대부분 모드가 eng 베이스)

    public static ScanResult Scan()
    {
        var result = new ScanResult();

        foreach (var mod in ModManager.GetLoadedMods())
        {
            string id = mod.manifest?.id ?? "";
            string name = mod.manifest?.name ?? id;
            string version = mod.manifest?.version ?? "";
            if (string.IsNullOrEmpty(id)) continue;
            if (id == MainFile.ModId) continue; // 자기 자신 제외

            // 이 모드가 "번역 모드"(translations/ 동봉)인지 먼저 본다. 번역 JSON 을 모아 두고,
            // localization/ 이 없더라도 미지원으로 분류하지 않는다(번역 모드는 원래 localization 이 없음).
            bool isProvider = BundledTranslationScanner.TryRead(id, name, mod.path, result.Bundled);

            string locRoot = $"res://{id}/localization";
            if (!Godot.DirAccess.DirExistsAbsolute(locRoot))
            {
                if (isProvider) continue; // 번역 모드 — 미지원 아님
                result.Unsupported.Add(new UnsupportedMod
                {
                    Id = id, Name = name,
                    Reason = "no localization/ folder — hardcoded, runtime-registered, or cosmetic (no translatable text)"
                });
                continue;
            }

            var langs = SafeDirs(locRoot);
            var sm = new SupportedMod { Id = id, Name = name, Version = version, ShipsLangs = langs };

            // 동봉된 모든 언어를 읽는다 (번역 참조용). eng 는 키 집합의 기준.
            foreach (string lang in langs)
            {
                string ldir = $"{locRoot}/{lang}";
                var tables = new Dictionary<string, Dictionary<string, string>>();
                foreach (string file in SafeFiles(ldir).Where(f => f.EndsWith(".json")))
                {
                    string table = file.Substring(0, file.Length - ".json".Length);
                    var dict = ReadResJson($"{ldir}/{file}");
                    if (dict.Count > 0) tables[table] = dict;
                }
                if (tables.Count > 0) sm.ByLang[lang] = tables;
            }

            if (sm.ByLang.Count == 0)
            {
                // localization/ 은 있으나 표준 {lang}/{table}.json 하위구조가 없다.
                // 평면 localization/*.json (예: en.json) 은 모드가 자체 i18n 으로 직접 읽는 패턴 —
                // 게임 LocManager 를 거치지 않아 주입 불가(디컴파일로 확인된 ModConfig 케이스).
                bool flatFiles = SafeFiles(locRoot).Any(f => f.EndsWith(".json"));
                result.Unsupported.Add(new UnsupportedMod
                {
                    Id = id, Name = name,
                    Reason = flatFiles
                        ? "self-loaded localization (flat localization/*.json) — bypasses the game's LocManager, not injectable"
                        : "localization/ present but no readable {lang}/{table}.json tables"
                });
                continue;
            }

            // 원문 기준 언어 폴더: eng 우선, eng 가 없으면 가장 키가 많은 동봉 언어로 폴백.
            sm.SourceLang = sm.ByLang.ContainsKey(PreferredSourceLang)
                ? PreferredSourceLang
                : sm.ByLang.OrderByDescending(kv => kv.Value.Values.Sum(d => d.Count))
                           .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                           .First().Key;

            // 실제 텍스트 언어: 폴더 이름을 믿지 않고 원문 값의 스크립트를 스니핑해 보정.
            // 동시에 원문이 두 스크립트를 섞고 있는지(부분 번역 모드)도 판정.
            sm.ContentLang = DetectContentLang(sm.EngByTable, sm.SourceLang, out bool mixed);
            sm.HasMixedSource = mixed;

            result.Supported.Add(sm);
        }

        return result;
    }

    /// <summary>
    /// 원문 테이블 값들의 스크립트를 세어 실제 언어(STS 코드)를 추정한다. CJK 전반 대응 —
    /// 폴더 이름이 eng 라도 내용이 한/일/중이면 그 언어를 돌려준다(네이티브 텍스트를 eng 폴백
    /// 폴더에 넣는 모드 패턴). CJK 문자가 라틴 대비 미미하거나 없으면 <paramref name="folderLang"/>
    /// 를 그대로 신뢰한다. 키·<c>{Var:diff()}</c> 플레이스홀더는 라틴이므로 값(value)만 센다.
    /// </summary>
    public static string DetectContentLang(
        Dictionary<string, Dictionary<string, string>> byTable, string folderLang)
        => DetectContentLang(byTable, folderLang, out _);

    /// <inheritdoc cref="DetectContentLang(Dictionary{string, Dictionary{string, string}}, string)"/>
    /// <param name="mixed">
    /// 원문이 두 언어를 <b>섞고</b> 있으면 true(예: 대부분 영어 + 일부 중국어, 또는 중국어 + 일부 한국어).
    /// 판정: 각 항목을 우세 <b>언어</b>로 분류(한글=한국어, 가나=일본어, 순수 한자=중국어, 라틴=영어권)한 뒤,
    /// 감지된 <c>ContentLang</c> 과 <b>다른 언어</b>인 항목이 <b>2개 이상</b>이면 true(비율 무관 — 부분 번역은
    /// 잔여가 적어도 잡아야 하므로). CJK↔라틴뿐 아니라 <b>CJK끼리</b>(중↔한, 중↔일)도 감지. 단 순수 한자만으론
    /// 中日 구분이 불가하므로, 일본어 모드에서 한자-only 항목은 외국으로 세지 않는다(보수적, 오탐 방지).
    /// <see cref="SupportedMod.HasMixedSource"/> 로 흘러가 부분 번역 모드에서 원문 언어 편집을 열어 준다.
    /// </param>
    public static string DetectContentLang(
        Dictionary<string, Dictionary<string, string>> byTable, string folderLang, out bool mixed)
    {
        long hangul = 0, kana = 0, han = 0, latin = 0;
        // 항목별 '우세 언어' 버킷(혼합 판정용) — CJK 끼리도 구분: 한글=한국어, 가나=일본어, 순수 한자=중국어(애매).
        int latEntries = 0, korEntries = 0, jpnEntries = 0, hanEntries = 0;
        foreach (var tbl in byTable.Values)
        foreach (var v in tbl.Values)
        {
            if (string.IsNullOrEmpty(v)) continue;
            long eh = 0, ek = 0, ehan = 0, el = 0; // 이 항목 하나의 스크립트별 글자 수
            foreach (char c in v)
            {
                if ((c >= 0xAC00 && c <= 0xD7A3)                       // 한글 음절
                 || (c >= 0x1100 && c <= 0x11FF)                       // 한글 자모
                 || (c >= 0x3130 && c <= 0x318F)) eh++;                // 호환 자모
                else if ((c >= 0x3040 && c <= 0x309F)                  // 히라가나
                      || (c >= 0x30A0 && c <= 0x30FF)) ek++;           // 가타카나
                else if ((c >= 0x4E00 && c <= 0x9FFF)                  // CJK 통합 한자
                      || (c >= 0x3400 && c <= 0x4DBF)                  // 확장 A
                      || (c >= 0xF900 && c <= 0xFAFF)) ehan++;         // 호환 한자
                else if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) el++;
            }
            hangul += eh; kana += ek; han += ehan; latin += el;
            // 이 항목의 '우세 언어' 분류 — 혼합 판정에서 ContentLang 과 다른 언어 항목(foreign)을 센다.
            long ecjk = eh + ek + ehan;
            if (el >= ecjk) { if (el > 0) latEntries++; }              // 라틴 우세(영어권)
            else if (eh >= ek && eh >= ehan && eh > 0) korEntries++;   // 한글 우세 = 한국어
            else if (ek > 0) jpnEntries++;                             // 가나 존재 = 일본어(전용 문자)
            else hanEntries++;                                         // 순수 한자 = 중국어(또는 일본어 간지=애매)
        }

        long cjk = hangul + kana + han;
        string content;
        // CJK 가 없거나 라틴의 25% 미만(플레이스홀더·UI 잔재 수준)이면 폴더 선언을 신뢰.
        if (cjk == 0 || cjk * 4 < latin) content = folderLang;
        // 가나는 일본어 고유 — 유의미하면 일본어. 아니면 한글 우세=한국어, 그 외 한자=중국어(간체 기본).
        else if (kana > 0 && kana * 20 >= cjk) content = "jpn";
        else if (hangul >= han) content = "kor";
        else content = "zhs"; // DeepL source_lang 은 zhs/zht 모두 ZH 이므로 간체/번체 구분 불필요

        // 혼합(부분 번역) 판정: ContentLang 과 '다른 언어'인 항목 수(foreign). CJK↔라틴뿐 아니라 CJK끼리도.
        //   중국어 모드: 한글(한국어)·가나(일본어)·라틴 항목이 외국. 순수 한자는 같은 언어(중국어).
        //   일본어 모드: 한글·라틴만 외국. 순수 한자/가나는 일본어로 봄(한자만으론 中日 구분 불가 → 보수적).
        //   한국어 모드: 라틴·가나·한자 항목이 외국(현대 한국어는 한글 우세).
        //   라틴(영어권) 모드: 모든 CJK 우세 항목이 외국.
        int foreign = content switch
        {
            "kor" => latEntries + jpnEntries + hanEntries,
            "jpn" => latEntries + korEntries,
            "zhs" or "zht" or "chs" or "cht" => latEntries + korEntries + jpnEntries,
            _ => korEntries + jpnEntries + hanEntries, // 라틴 계열
        };
        // 비율 문턱은 제거(부분 번역은 잔여 비율이 작아도 감지) — 절대 최소 2개만(단일 이상치 노이즈 방지).
        mixed = foreign >= 2;
        return content;
    }

    // ── Godot res:// 헬퍼 ───────────────────────────────────────

    internal static List<string> SafeDirs(string resDir)
    {
        try { return Godot.DirAccess.GetDirectoriesAt(resDir).Where(s => !string.IsNullOrEmpty(s)).ToList(); }
        catch { return new(); }
    }

    internal static List<string> SafeFiles(string resDir)
    {
        try { return Godot.DirAccess.GetFilesAt(resDir).Where(s => !string.IsNullOrEmpty(s)).ToList(); }
        catch { return new(); }
    }

    /// <summary>res:// (pck 포함) 경로의 flat {string:string} JSON 을 읽는다. 실패 시 빈 dict.</summary>
    public static Dictionary<string, string> ReadResJson(string resPath)
    {
        try
        {
            using var f = Godot.FileAccess.Open(resPath, Godot.FileAccess.ModeFlags.Read);
            if (f == null) return new();
            string text = f.GetAsText();
            if (string.IsNullOrWhiteSpace(text)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(text) ?? new();
        }
        catch
        {
            return new();
        }
    }
}
