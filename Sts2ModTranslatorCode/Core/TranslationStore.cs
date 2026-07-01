using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Sts2ModTranslator.Core;

/// <summary>
/// %APPDATA%\Sts2ModTranslator\ 의 파일 입출력 담당.
///
/// 레이아웃(데이터 파일 확장자 = <see cref="DataExt"/>):
///   source/{id}/eng/{table}.txt        — 원문(영어) 참조본. 매번 새로 씀(읽기 전용 취급).
///   overrides/{id}/{lang}/{table}.txt   — 번역 입력칸. 값이 비어 있으면 미번역(원문 fallback).
///                                         기존 파일은 절대 덮어쓰지 않고, 새 키만 추가한다.
///   supported_mods.txt                  — 지원/미지원 모드 목록 + 진행률 리포트.
/// 파일 내용은 모두 JSON 이지만, 확장자를 .json 으로 두면 STS2 ModManager 가 mods\ 트리의
/// 모든 .json 을 매니페스트로 파싱 시도해 부팅 시 "missing id" 로그를 남기므로 .txt 로 저장한다.
/// </summary>
public static class TranslationStore
{
    private static string? _rootCache;

    /// <summary>
    /// 번역 데이터 파일 확장자. 내용은 JSON 이지만 .json 으로 두면 ModManager 가 매니페스트로
    /// 오인해 부팅 로그에 "missing id" 가 쌓이므로 .txt 를 쓴다. (진짜 매니페스트만 .json 유지.)
    /// </summary>
    public const string DataExt = ".txt";

    /// <summary>
    /// 번역 데이터 루트 = 모드 폴더 내부의 Translations\ (DLL 옆). %APPDATA% 폴백은 쓰지 않는다
    /// (루트 이중화로 번역이 갈라지던 문제 제거). 기존 %APPDATA% 데이터는 최초 1회 여기로 병합 이전한다.
    /// 게임 폴더가 쓰기 불가(예: Program Files 권한)면 경고만 남기고 동작은 best-effort.
    /// </summary>
    public static string Root
    {
        get
        {
            if (_rootCache != null) return _rootCache;

            string? modDir = OwnModDir();
            if (string.IsNullOrEmpty(modDir))
            {
                // 정상 환경에선 도달하지 않음(어셈블리 위치/Mod.path 모두 실패). 마지막 수단(비-%APPDATA%).
                MainFile.Logger.Warn("[Sts2ModTranslator] 모드 폴더 위치를 확인할 수 없습니다 — 번역 저장이 제한될 수 있습니다.");
                modDir = AppContext.BaseDirectory;
            }

            string chosen = Path.Combine(modDir!, "Translations");
            Directory.CreateDirectory(chosen);
            if (!TryEnsureWritable(chosen))
                MainFile.Logger.Warn(
                    $"[Sts2ModTranslator] '{chosen}' 에 쓸 수 없습니다(게임 폴더 권한). 번역이 저장되지 않을 수 있습니다.");

            _rootCache = chosen;
            MigrateFromAppData(chosen);     // 기존 %APPDATA% 작업 데이터 → 모드 폴더로 1회 병합 이전
            MigrateLegacyExtension(chosen); // 기존 .json 작업 파일 → .txt 1회 변환
            return chosen;
        }
    }

    /// <summary>이 모드 자신의 폴더(DLL/매니페스트 위치). 어셈블리 위치 → 실패 시 ModManager 의 Mod.path.</summary>
    private static string? OwnModDir()
    {
        try
        {
            string loc = typeof(TranslationStore).Assembly.Location;
            if (!string.IsNullOrEmpty(loc)) return Path.GetDirectoryName(loc);
        }
        catch { /* Location 비어있는 로드 컨텍스트 → 아래 폴백 */ }
        try
        {
            foreach (var m in MegaCrit.Sts2.Core.Modding.ModManager.GetLoadedMods())
                if (m.manifest?.id == MainFile.ModId && !string.IsNullOrEmpty(m.path))
                    return m.path;
        }
        catch { /* 모드 로드 전 등 — null */ }
        return null;
    }

    /// <summary>
    /// 이전 버전이 쓰던 %APPDATA%\Sts2ModTranslator\ 의 작업 데이터(overrides/source/author)를
    /// 모드 폴더 루트로 최초 1회 병합 이전한다. 키 단위로 비어 있지 않은 값을 보존(번역 유실 방지):
    /// 현재 모드 폴더 값이 비어 있고 %APPDATA% 에 값이 있으면 그 값을 채운다. exported\ 는 제외(스테이징).
    /// %APPDATA% 원본은 삭제하지 않는다(안전). best-effort.
    /// </summary>
    private static void MigrateFromAppData(string root)
    {
        try
        {
            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sts2ModTranslator");
            if (!Directory.Exists(appData)) return;
            if (string.Equals(Path.GetFullPath(appData).TrimEnd('\\'),
                              Path.GetFullPath(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return; // 동일 경로(있을 수 없지만 방어)

            int moved = 0;
            foreach (var sub in new[] { "overrides", "source" })
            {
                string srcRoot = Path.Combine(appData, sub);
                if (!Directory.Exists(srcRoot)) continue;
                foreach (var srcFile in Directory.GetFiles(srcRoot, "*", SearchOption.AllDirectories))
                {
                    if (!srcFile.EndsWith(DataExt, StringComparison.Ordinal) &&
                        !srcFile.EndsWith(".json", StringComparison.Ordinal)) continue;
                    string rel = Path.GetRelativePath(srcRoot, srcFile);
                    // 레거시 .json → .txt 로 목적지 확장자 정규화.
                    if (rel.EndsWith(".json", StringComparison.Ordinal))
                        rel = rel.Substring(0, rel.Length - ".json".Length) + DataExt;
                    string dstFile = Path.Combine(root, sub, rel);
                    if (MergePreferNonEmpty(srcFile, dstFile)) moved++;
                }
            }
            // author 이름.
            foreach (var ext in new[] { DataExt, ".json" })
            {
                string aSrc = Path.Combine(appData, "author" + ext);
                string aDst = Path.Combine(root, "author" + DataExt);
                if (File.Exists(aSrc) && !File.Exists(aDst))
                {
                    Directory.CreateDirectory(root);
                    File.Copy(aSrc, aDst);
                }
            }
            if (moved > 0)
                MainFile.Logger.Info($"[Sts2ModTranslator] %APPDATA% 번역 데이터 {moved}개 파일을 모드 폴더로 이전.");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Sts2ModTranslator] %APPDATA% 이전 실패(무시): {ex.Message}");
        }
    }

    /// <summary>src 의 (키→값)을 dst 로 병합. dst 값이 비어 있고 src 값이 있으면 채운다. 변경 시 true.</summary>
    private static bool MergePreferNonEmpty(string srcFile, string dstFile)
    {
        try
        {
            var src = ReadJsonRaw(srcFile);
            if (src.Count == 0) return false;
            if (!File.Exists(dstFile))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dstFile)!);
                var sorted = new SortedDictionary<string, string>(src, StringComparer.Ordinal);
                WriteJson(dstFile, sorted);
                return true;
            }
            var dst = ReadJsonRaw(dstFile);
            bool changed = false;
            var merged = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var k in dst.Keys.Union(src.Keys))
            {
                string dv = dst.TryGetValue(k, out var a) ? a : "";
                string sv = src.TryGetValue(k, out var b) ? b : "";
                string val = !string.IsNullOrEmpty(dv) ? dv : sv;
                merged[k] = val;
                if (val != dv) changed = true;
            }
            if (changed) WriteJson(dstFile, merged);
            return changed;
        }
        catch { return false; }
    }

    /// <summary>임의 경로의 flat {string:string} JSON 을 읽는다(병합 이전 전용). 실패 시 빈 dict.</summary>
    private static Dictionary<string, string> ReadJsonRaw(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            string text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(text) ?? new();
        }
        catch { return new(); }
    }

    /// <summary>
    /// 이전 버전이 .json 으로 저장한 작업 파일(overrides/source/리포트)을 .txt 로 1회 변환.
    /// exported\ 하위(배포용 매니페스트 등)는 건드리지 않는다. best-effort — 실패해도 무시.
    /// </summary>
    private static void MigrateLegacyExtension(string root)
    {
        foreach (var sub in new[] { "overrides", "source" })
        {
            string dir = Path.Combine(root, sub);
            if (!Directory.Exists(dir)) continue;
            try
            {
                // 열거 중 rename 으로 인한 문제 방지 — 목록을 먼저 확정.
                foreach (var json in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories))
                    RenameToDataExt(json);
            }
            catch { /* 열거 실패 무시 */ }
        }
        RenameToDataExt(Path.Combine(root, "supported_mods.json"));
    }

    private static void RenameToDataExt(string jsonPath)
    {
        try
        {
            if (!File.Exists(jsonPath)) return;
            string dst = jsonPath.Substring(0, jsonPath.Length - ".json".Length) + DataExt;
            if (File.Exists(dst)) File.Delete(jsonPath); // 이미 변환됨 → 중복 .json 제거
            else File.Move(jsonPath, dst);
        }
        catch { /* 개별 파일 실패 무시 */ }
    }

    private static bool TryEnsureWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".write_test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        // 한국어/중국어 등 비ASCII 를 \uXXXX 로 이스케이프하지 않아 번역자가 그대로 편집 가능.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string SourceDir(string id, string lang) =>
        Path.Combine(Root, "source", id, lang);

    private static string OverrideDir(string id, string lang) =>
        Path.Combine(Root, "overrides", id, lang);

    public static string OverridePath(string id, string lang, string table) =>
        Path.Combine(OverrideDir(id, lang), table + DataExt);

    /// <summary>
    /// 한 모드의 source(원문) 갱신 + override(번역칸) 템플릿 생성/증분.
    /// 기존 번역값은 보존하고, 모드 신규 키만 빈 값으로 추가한다.
    /// </summary>
    public static void EnsureTemplates(SupportedMod mod, string lang)
    {
        // 1) 원문 참조본: 모드가 동봉한 *모든* 언어를 기록(eng/esp/zhs…).
        //    한국어 번역 시 기존 중문/스페인어를 참고할 수 있게 한다. (항상 최신으로 덮어씀)
        foreach (var (refLang, tables) in mod.ByLang)
        {
            string srcDir = SourceDir(mod.Id, refLang);
            Directory.CreateDirectory(srcDir);
            foreach (var (table, dict) in tables)
                WriteJson(Path.Combine(srcDir, table + DataExt), Sorted(dict));
        }

        // 2) 번역 입력칸: eng 키 기준, 기존 번역 보존 + 신규 키만 빈 값으로 추가.
        foreach (var (table, engDict) in mod.EngByTable)
        {
            string ovrPath = OverridePath(mod.Id, lang, table);
            // JSON 이 깨진 파일은 절대 덮어쓰지 않는다(사용자 번역 유실 방지).
            // UI 가 '오류'로 표시하고, 편집기에서 고쳐 저장하면 정상화된다.
            if (File.Exists(ovrPath) && !TryReadJson(ovrPath, out _, out _)) continue;
            var existing = ReadJson(ovrPath); // 없음/빈/정상 → dict
            var merged = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in engDict.Keys)
                merged[key] = existing.TryGetValue(key, out var v) ? v : ""; // 빈 값 = 미번역
            Directory.CreateDirectory(OverrideDir(mod.Id, lang));
            WriteJson(ovrPath, merged);
        }
    }

    /// <summary>주어진 모드/언어의 (총 키 수, 번역된 키 수). 진행률 표시용.</summary>
    public static (int total, int translated) Coverage(SupportedMod mod, string lang)
    {
        int total = mod.TotalKeys;
        int tr = LoadNonEmptyOverrides(mod, lang).Values.Sum(d => d.Count);
        return (total, tr);
    }

    /// <summary>
    /// 한 테이블에 주입할 최종 dict 를 만든다. 키마다 우선순위:
    ///   1) 로컬 override 값(비어 있지 않으면) — 사용자가 인게임 에디터로 직접 한 번역
    ///   2) 설치된 번역 모드의 값(bundled, 비어 있지 않으면)
    ///   3) 모드가 현재 언어를 직접 동봉했으면 그 원본값
    ///   4) eng 기본값
    /// 모든 키를 명시적으로 설정하므로 "로컬 번역을 비우면 (번역 모드 → 원문 순으로) 복귀" 가 보장된다.
    /// bundled 는 설치된 번역 모드가 이 (대상모드, 언어, 테이블)에 제공한 (키→값). 없으면 null/빈 dict.
    /// </summary>
    public static Dictionary<string, string> BuildInjectTable(
        SupportedMod mod, string lang, string table, IReadOnlyDictionary<string, string>? bundled = null)
    {
        var eng = mod.EngByTable.TryGetValue(table, out var e) ? e : new Dictionary<string, string>();
        Dictionary<string, string>? shipped = null;
        if (mod.ByLang.TryGetValue(lang, out var byTable) && byTable.TryGetValue(table, out var st))
            shipped = st; // 모드가 현재 언어를 직접 동봉한 경우의 원본값

        var ov = ReadJson(OverridePath(mod.Id, lang, table));

        // eng 키 ∪ bundled 키 — 번역 모드가 eng 에 없는 키를 줘도 누락 없이 주입.
        var keys = new HashSet<string>(eng.Keys, StringComparer.Ordinal);
        if (bundled != null) foreach (var k in bundled.Keys) keys.Add(k);

        var result = new Dictionary<string, string>(keys.Count);
        foreach (var key in keys)
        {
            if (ov.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
                result[key] = v;                                   // 1) 로컬 번역값
            else if (bundled != null && bundled.TryGetValue(key, out var bv) && !string.IsNullOrEmpty(bv))
                result[key] = bv;                                  // 2) 설치된 번역 모드
            else if (shipped != null && shipped.TryGetValue(key, out var sv) && !string.IsNullOrEmpty(sv))
                result[key] = sv;                                  // 3) 동봉 원본(현재 언어)
            else if (eng.TryGetValue(key, out var ev))
                result[key] = ev;                                  // 4) eng 기본값
        }
        return result;
    }

    /// <summary>
    /// UI 파일 목록용 상태: (총 키, 번역된 키, JSON 깨짐 여부).
    /// invalid=true 면 파일을 파싱할 수 없어 번역이 적용되지 않는 상태(편집기에서 수정 필요).
    /// 번역 카운트는 템플릿(eng) 키 집합 안에서만 센다.
    /// </summary>
    public static (int total, int translated, bool invalid) TableStatus(
        SupportedMod mod, string lang, string table)
    {
        var keys = mod.EngByTable.TryGetValue(table, out var e) ? e : new Dictionary<string, string>();
        int total = keys.Count;
        if (!TryReadJson(OverridePath(mod.Id, lang, table), out var ov, out _))
            return (total, 0, true); // JSON 형식 오류 — 적용 안 됨
        int tr = ov.Count(kv => keys.ContainsKey(kv.Key) && !string.IsNullOrEmpty(kv.Value));
        return (total, tr, false);
    }

    /// <summary>override 파일의 raw 텍스트(편집기 표시용). 없으면 "{}".</summary>
    public static string OverrideText(string modId, string lang, string table)
    {
        try
        {
            string p = ReadPath(OverridePath(modId, lang, table));
            return File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8) : "{}";
        }
        catch { return "{}"; }
    }

    /// <summary>(키→값) dict 를 편집기 참조용 들여쓰기 JSON 문자열로. 키 정렬·비ASCII 그대로.</summary>
    public static string ToPrettyJson(IReadOnlyDictionary<string, string> dict)
    {
        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in dict) sorted[kv.Key] = kv.Value;
        return JsonSerializer.Serialize(sorted, WriteOpts);
    }

    /// <summary>원본(기본 eng) 로컬라이제이션 텍스트(편집기 참조용 read-only). 없으면 "{}".</summary>
    public static string SourceText(string modId, string table, string lang = "eng")
    {
        try
        {
            string p = ReadPath(Path.Combine(SourceDir(modId, lang), table + DataExt));
            return File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8) : "{}";
        }
        catch { return "{}"; }
    }

    /// <summary>편집기 텍스트를 검증(JSON object) 후 override 로 저장. (성공여부, 오류메시지).</summary>
    public static (bool ok, string error) SaveOverrideText(string modId, string lang, string table, string text)
    {
        try
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(text);
            if (d == null) return (false, "JSON 최상위가 객체가 아닙니다");
        }
        catch (Exception ex) { return (false, "JSON 파싱 오류: " + ex.Message); }
        try { WriteRaw(OverridePath(modId, lang, table), text); return (true, ""); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>한 테이블의 override 를 소스 키 + 빈 값으로 덮어쓴다(= 전부 원문 복귀).</summary>
    public static void ResetOverride(SupportedMod mod, string lang, string table)
    {
        var eng = mod.EngByTable.TryGetValue(table, out var e) ? e : new Dictionary<string, string>();
        var empty = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var k in eng.Keys) empty[k] = "";
        WriteJson(OverridePath(mod.Id, lang, table), empty);
    }

    /// <summary>한 모드/언어의 모든 테이블 override 를 초기화.</summary>
    public static void ResetLanguage(SupportedMod mod, string lang)
    {
        foreach (var table in mod.EngByTable.Keys) ResetOverride(mod, lang, table);
    }

    /// <summary>외부 JSON 파일을 업로드: 템플릿 키는 유지하고 일치하는 값만 반영. (성공여부, 오류).</summary>
    public static (bool ok, string error) ImportInto(string modId, string lang, string table, string externalPath)
    {
        Dictionary<string, string>? ext;
        try
        {
            ext = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(externalPath, Encoding.UTF8));
        }
        catch (Exception ex) { return (false, "업로드 파일 파싱 오류: " + ex.Message); }
        if (ext == null) return (false, "업로드 JSON 최상위가 객체가 아닙니다");

        try
        {
            var cur = ReadJson(OverridePath(modId, lang, table)); // 템플릿(키 집합)
            var merged = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (cur.Count > 0)
                foreach (var k in cur.Keys) merged[k] = ext.TryGetValue(k, out var v) ? v : cur[k];
            else
                foreach (var kv in ext) merged[kv.Key] = kv.Value; // 템플릿 없으면 업로드 그대로
            WriteJson(OverridePath(modId, lang, table), merged);
            return (true, "");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>주어진 모드/언어의 override 중 값이 비어 있지 않은 것만 테이블별로 반환.</summary>
    public static Dictionary<string, Dictionary<string, string>> LoadNonEmptyOverrides(
        SupportedMod mod, string lang)
    {
        var byTable = new Dictionary<string, Dictionary<string, string>>();
        foreach (var table in mod.EngByTable.Keys)
        {
            var dict = ReadJson(OverridePath(mod.Id, lang, table));
            var nonEmpty = dict.Where(kv => !string.IsNullOrEmpty(kv.Value))
                               .ToDictionary(kv => kv.Key, kv => kv.Value);
            if (nonEmpty.Count > 0) byTable[table] = nonEmpty;
        }
        return byTable;
    }

    // ── 번역 모드 내보내기(설치) ────────────────────────────────

    /// <summary>
    /// 게임의 mods\ 폴더 = 이 모드 자신의 폴더의 상위. 여기로 내보내면 곧바로 설치본이 되어
    /// 다음 부팅에 로드되고, 워크샵 업로드 대시보드도 '설치됨(installed)'으로 인식한다.
    /// 위치를 못 구하면 null.
    /// </summary>
    public static string? GameModsDir
    {
        get
        {
            string? modDir = OwnModDir();
            return string.IsNullOrEmpty(modDir) ? null : Path.GetDirectoryName(modDir); // mods\
        }
    }

    /// <summary>매니페스트 author 로 쓸 번역가 이름(세션 간 유지). 비어 있으면 "".</summary>
    public static string LoadAuthor()
    {
        try
        {
            string p = ReadPath(Path.Combine(Root, "author" + DataExt));
            return File.Exists(p) ? File.ReadAllText(p).Trim() : "";
        }
        catch { return ""; }
    }

    /// <summary>번역가 이름을 저장(다음 내보내기에 author 로 자동 사용).</summary>
    public static void SaveAuthor(string author)
    {
        try { WriteRaw(Path.Combine(Root, "author" + DataExt), (author ?? "").Trim()); }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// 자동 번역(DeepL)용 API 키. 루트에 평문 저장되며 *로컬 전용* — 내보내는 번역 모드에는
    /// 포함되지 않는다(ExportMod 는 translations\ 와 매니페스트만 쓴다). 비어 있으면 "".
    /// </summary>
    public static string LoadApiKey()
    {
        try
        {
            string p = ReadPath(Path.Combine(Root, "deepl_key" + DataExt));
            return File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8).Trim() : "";
        }
        catch { return ""; }
    }

    /// <summary>DeepL API 키를 로컬에 저장(다음 자동 번역에 재사용).</summary>
    public static void SaveApiKey(string key)
    {
        try { WriteRaw(Path.Combine(Root, "deepl_key" + DataExt), (key ?? "").Trim()); }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// 한 대상 모드의 (비어 있지 않은) 번역을 배포 가능한 독립 "번역 모드" 폴더로 내보낸다.
    /// 결과 레이아웃:
    ///   {destRoot}/{modId}_Translation/
    ///     {modId}_Translation.json                      — 매니페스트(dependencies: [Sts2ModTranslator])
    ///     translations/{대상id}/{lang}/{table}.txt      — 번역값(JSON 내용, 비어 있지 않은 키만)
    /// 사용자는 이 폴더를 STS2 mods\ 에 넣거나 Workshop 에 올려 배포할 수 있다.
    /// destRoot 가 null 이면 ExportRoot(%APPDATA%\...\exported), 게임 mods\ 를 주면 즉시 설치본이 된다.
    /// author 는 매니페스트 author(번역가 본인). 썸네일(image.png)은 창작마당 업로드 시 사용자가 직접 등록.
    /// 반환: (성공여부, 생성된 폴더 경로, 오류). 내보낼 번역이 하나도 없으면 실패.
    /// </summary>
    /// <summary>내보내기 시 생성되는 번역 모드의 폴더/매니페스트 id ("{원본id}_Translation").</summary>
    public static string ExportedModId(SupportedMod mod) => Sanitize(mod.Id) + "_Translation";

    /// <summary>
    /// destRoot(보통 게임 mods\)에 이미 설치된 이 번역 모드의 매니페스트 version. 설치 안 됐거나
    /// 읽기 실패면 null. UI 가 "설치됨: vX" 표기 + patch 자동 증가 제안에 쓴다.
    /// </summary>
    public static string? InstalledVersion(SupportedMod mod, string destRoot)
    {
        try
        {
            string id = ExportedModId(mod);
            string manifestPath = Path.Combine(destRoot, id, id + ".json");
            if (!File.Exists(manifestPath)) return null;
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                File.ReadAllText(manifestPath, Encoding.UTF8));
            if (doc != null && doc.TryGetValue("version", out var v) && v.ValueKind == JsonValueKind.String)
            {
                string s = (v.GetString() ?? "").Trim();
                return string.IsNullOrEmpty(s) ? null : s;
            }
        }
        catch { /* 읽기 실패 — 미설치로 취급 */ }
        return null;
    }

    /// <summary>
    /// 설치 버전의 다음 제안값. 마지막 숫자 세그먼트를 1 올린다("1.0.0"→"1.0.1", "v1.2"→"1.3").
    /// installed 가 비어 있으면(미설치) "1.0.0". 숫자로 못 끝나면 그대로 둔다(사용자가 직접 수정).
    /// </summary>
    public static string NextVersion(string? installed)
    {
        if (string.IsNullOrWhiteSpace(installed)) return "1.0.0";
        var parts = installed.Trim().TrimStart('v', 'V').Split('.');
        if (parts.Length > 0 && int.TryParse(parts[^1], out int last))
        {
            parts[^1] = (last + 1).ToString();
            return string.Join('.', parts);
        }
        return installed.Trim();
    }

    public static (bool ok, string path, string error) ExportMod(
        SupportedMod mod, string author, string destRoot, string version)
    {
        try
        {
            // 1) 비어 있지 않은 번역을 가진 언어/테이블만 수집.
            var langs = new List<(string lang, Dictionary<string, Dictionary<string, string>> tables)>();
            foreach (var lang in AllOverrideLangs(mod.Id))
            {
                var tables = LoadNonEmptyOverrides(mod, lang);
                if (tables.Count > 0) langs.Add((lang, tables));
            }
            if (langs.Count == 0)
                return (false, "", "내보낼 번역이 없습니다 — 먼저 한 항목 이상 번역하세요.");

            string ver = string.IsNullOrWhiteSpace(version) ? "1.0.0" : version.Trim();
            string newId = Sanitize(mod.Id) + "_Translation";
            string modDir = Path.Combine(destRoot, newId);
            // 기존 내보내기는 새로 덮어쓴다(매번 최신 번역 반영). translations 하위만 청소.
            string trDir = Path.Combine(modDir, BundledTranslationScanner.FolderName, mod.Id);
            if (Directory.Exists(trDir)) Directory.Delete(trDir, recursive: true);

            var langCodes = new List<string>();
            foreach (var (lang, tables) in langs)
            {
                langCodes.Add(lang);
                foreach (var (table, dict) in tables)
                {
                    var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
                    foreach (var kv in dict) sorted[kv.Key] = kv.Value;
                    // 번역 데이터는 .txt 로 — 설치 시 ModManager 의 'missing id' 로그 회피.
                    WriteJson(Path.Combine(trDir, lang, table + DataExt), sorted);
                }
            }

            // 2) 매니페스트.
            var manifest = new
            {
                id = newId,
                name = mod.Name + " Translation",
                author = author ?? "",
                description =
                    $"Translation pack for \"{mod.Name}\" ({string.Join(", ", langCodes)}). "
                    + "Requires the STS2 Mod Translator mod to apply.",
                version = ver,
                has_pck = false,
                has_dll = false,
                // STS2 v0.107+ 매니페스트: dependencies 는 {id, min_version} 객체 배열.
                // (옛 문자열 배열은 부팅 시 'old-style dependencies … will be removed' 경고를 띄움.)
                // min_version = 번역팩 소비 기능(BundledTranslationScanner)이 들어온 1.3.0.
                dependencies = new[] { new { id = MainFile.ModId, min_version = "1.3.0" } },
                affects_gameplay = false,
            };
            WriteRaw(Path.Combine(modDir, newId + ".json"),
                JsonSerializer.Serialize(manifest, WriteOpts));

            return (true, modDir, "");
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }

    /// <summary>해당 대상 모드의 overrides\{id}\ 아래 존재하는 언어 폴더 목록.</summary>
    private static List<string> AllOverrideLangs(string id)
    {
        string dir = Path.Combine(Root, "overrides", id);
        if (!Directory.Exists(dir)) return new();
        try
        {
            return Directory.GetDirectories(dir)
                .Select(Path.GetFileName)
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)
                .ToList();
        }
        catch { return new(); }
    }

    /// <summary>모드 id 로 쓸 수 있도록 파일시스템 안전 문자만 남긴다.</summary>
    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        string r = sb.ToString();
        return string.IsNullOrEmpty(r) ? "Mod" : r;
    }

    /// <summary>지원/미지원 목록 + 진행률 리포트를 supported_mods.txt(JSON 내용)로 출력.</summary>
    public static void WriteReport(ScanResult scan, string lang)
    {
        var supported = new List<object>();
        foreach (var m in scan.Supported.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            int total = m.TotalKeys;
            int translated = LoadNonEmptyOverrides(m, lang).Values.Sum(d => d.Count);
            supported.Add(new
            {
                id = m.Id,
                name = m.Name,
                version = m.Version,
                ships_langs = m.ShipsLangs.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                tables = m.EngByTable.Keys.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                keys_total = total,
                keys_translated = translated,
                coverage = total == 0 ? "n/a" : $"{(int)Math.Round(100.0 * translated / total)}%",
                edit_folder = OverrideDir(m.Id, lang),
            });
        }

        var report = new
        {
            schema = "sts2_mod_translator_report_v1",
            generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            target_lang = lang,
            supported_count = scan.Supported.Count,
            unsupported_count = scan.Unsupported.Count,
            supported,
            unsupported = scan.Unsupported
                .OrderBy(m => m.Id, StringComparer.Ordinal)
                .Select(m => new { id = m.Id, name = m.Name, reason = m.Reason })
                .ToArray(),
            // 설치된 번역 모드(Sts2ModTranslator 를 참조해 번역을 동봉한 모드)들.
            translation_packs = scan.Bundled.Providers
                .OrderBy(p => p.Id, StringComparer.Ordinal)
                .Select(p => new { id = p.Id, name = p.Name, targets = p.Targets.ToArray(), keys = p.Keys })
                .ToArray(),
        };

        WriteRaw(Path.Combine(Root, "supported_mods" + DataExt),
            JsonSerializer.Serialize(report, WriteOpts));
    }

    // ── JSON IO ─────────────────────────────────────────────────

    private static SortedDictionary<string, string> Sorted(Dictionary<string, string> d)
    {
        var s = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in d) s[kv.Key] = kv.Value;
        return s;
    }

    /// <summary>
    /// JSON 파일을 {string:string} 으로 읽기 시도.
    /// 반환 true = 정상(파일 없음/빈 파일 포함), false = JSON 형식 오류.
    /// dict 는 성공 시 내용(없음/빈 파일이면 빈 dict), error 에 실패 사유.
    /// </summary>
    private static bool TryReadJson(string path, out Dictionary<string, string> dict, out string error)
    {
        dict = new();
        error = "";
        try
        {
            path = ReadPath(path);
            if (!File.Exists(path)) return true;
            string text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return true;
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(text);
            if (d == null) { error = "최상위가 객체가 아닙니다"; return false; }
            dict = d;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static Dictionary<string, string> ReadJson(string path)
        => TryReadJson(path, out var d, out _) ? d : new();

    /// <summary>
    /// 읽기용 경로 보정: .txt 가 없고 레거시 .json 만 있으면 .json 경로를 돌려준다.
    /// (확장자 마이그레이션이 어떤 이유로 누락된 파일도 안전하게 읽기 위함.)
    /// </summary>
    private static string ReadPath(string path)
    {
        try
        {
            if (File.Exists(path) || !path.EndsWith(DataExt, StringComparison.Ordinal)) return path;
            string legacy = path.Substring(0, path.Length - DataExt.Length) + ".json";
            return File.Exists(legacy) ? legacy : path;
        }
        catch { return path; }
    }

    private static void WriteJson(string path, object dict) =>
        WriteRaw(path, JsonSerializer.Serialize(dict, WriteOpts));

    private static void WriteRaw(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
