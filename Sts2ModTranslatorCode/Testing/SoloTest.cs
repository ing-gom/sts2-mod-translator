#if DEBUG
// solo-verify — SmartFormat 검증 게이트(v1.11.0) 실측.
//
// 이 모드는 런 상태를 바꾸지 않으므로 SP 런을 시작할 필요가 없다. 메인 메뉴 도달 후
// LocManager.SetLanguage("kor") 를 직접 호출해 실제 주입 경로(스캔→번들팩→FilterValidFormats)
// 전체를 구동하고, 게임 mods\ 에 미리 심어 둔 게이트 테스트 팩(Sts2GateTest_Translations:
// 유효 1건 + 깨진 {format} 1건, 대상 테이블=base-game "main_menu_ui")의 주입 결과를 검사한다.
//
// 판정: 깨진 항목은 테이블에 없어야 하고(LastInjectInvalidCount>=1), 유효 항목은 주입돼야 한다.
// SetLanguage 는 설정 저장을 하지 않으므로(디컴파일 주석 확인) 유저 언어 설정은 오염되지 않는다.
//
// 트리거 = DLL 옆 selftest.sp.flag (solo-selftest.ps1 이 관리). 결과 = selftest.sp.txt.
// 이 파일은 Debug 전용(csproj Compile Remove + #if DEBUG) — Release 배포본엔 포함되지 않는다.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes;
using Sts2ModTranslator.Core;
using Sts2ModTranslator.Ui;

namespace Sts2ModTranslator;

internal static class SoloTest
{
    private const string PackId = "Sts2GateTest_Translations";
    private const string Table = "main_menu_ui";
    private const string ValidKey = "SOLOGATE-VALID";
    private const string ValidText = "게이트 유효 {0}";
    private const string BrokenKey = "SOLOGATE-BROKEN";

    private static readonly StringBuilder _out = new();
    private static bool _started, _done;

    private static string ModDir() => Path.GetDirectoryName(typeof(SoloTest).Assembly.Location) ?? ".";

    /// <summary>모드 init 에서 호출. DLL 옆 selftest.sp.flag 가 없으면 no-op.</summary>
    public static void ArmIfRequested()
    {
        try
        {
            if (!File.Exists(Path.Combine(ModDir(), "selftest.sp.flag"))) return;
            W("solo selftest armed (SmartFormat gate)");
            Poll();
        }
        catch (Exception e) { MainFile.Logger.Warn($"[Sts2ModTranslator] solo arm failed: {e.Message}"); }
    }

    private static void Poll()
    {
        if (Engine.GetMainLoop() is not SceneTree tree || _done) return;
        try { Tick(); } catch (Exception e) { W("tick exception: " + e.Message); }
        if (!_done) tree.CreateTimer(2.0).Timeout += Poll;
    }

    private static void Tick()
    {
        if (_started) return;
        if (NGame.Instance == null || LocManager.Instance == null) { W("waiting for menu…"); return; }
        _started = true;
        _ = RunTest();
    }

    private static async Task RunTest()
    {
        try
        {
            var mgr = LocManager.Instance;
            string originalLang = mgr.Language;
            W($"menu ready, language={originalLang}");
            await Shot("1_menu");

            // 실제 경로 전체 구동: SetLanguage postfix → 스캔 → 번들팩 → FilterValidFormats → 주입.
            mgr.SetLanguage("kor");
            await Task.Delay(1500);
            await Shot("2_menu_kor");

            bool ok = true;
            void Assert(bool cond, string what)
            {
                W($"assert: {what} -> {(cond ? "OK" : "FAIL")}");
                ok &= cond;
            }

            var scan = TranslationSync.CurrentScan;
            Assert(scan != null, "scan present");
            Assert(scan?.Bundled.Providers.Any(p => p.Id == PackId) == true,
                $"gate-test pack '{PackId}' detected as provider");

            int invalid = TranslationSync.LastInjectInvalidCount;
            W($"LastInjectInvalidCount = {invalid}");
            Assert(invalid >= 1, "gate rejected >=1 broken entry during inject");

            LocTable? t = null;
            try { t = mgr.GetTable(Table); } catch { /* below assert reports */ }
            Assert(t != null, $"table '{Table}' exists");
            if (t != null)
            {
                bool validIn = t.HasEntry(ValidKey);
                Assert(validIn, "valid entry injected");
                if (validIn) Assert(t.GetRawText(ValidKey) == ValidText, "valid entry text intact");
                Assert(!t.HasEntry(BrokenKey), "broken entry NOT injected (gate filtered it)");
            }

            // 편집기 경고 경로의 단위 검증(같은 검증기 공유).
            var badKeys = TranslationSync.InvalidFormatKeys(
                "{\"A\":\"ok {0}\",\"B\":\"bad {Damage:diff(\"}");
            Assert(badKeys.Count == 1 && badKeys[0] == "B",
                "InvalidFormatKeys flags exactly the broken key");

            // DeepL 안전검사(IsSafeResult) 토큰 보존 — DeepL API 없이 결정적으로 검증.
            // (source, result, 기대 safe?)
            (string src, string res, bool safe)[] cases =
            {
                ("Deal {Damage} damage.", "{Damage} 피해를 줍니다.", true),          // 정상
                ("Deal {Damage} damage.", "피해를 줍니다.", false),                  // 중괄호 변수 유실
                ("Deal {Damage} damage.", "{Damage}{Block} 피해.", false),           // 변수 발명
                ("Gain [gold]Block[/gold].", "[gold]방어도[/gold]를 얻습니다.", true), // 하이라이트 보존
                ("Gain [gold]Block[/gold].", "방어도를 얻습니다.", false),            // 하이라이트 태그 유실
                ("#Deal !D! damage.", "#!D! 피해를 줍니다.", true),                   // BaseLib !Var! + # 보존
                ("#Deal !D! damage.", "#피해를 줍니다.", false),                      // !Var! 유실
                ("[img]res://x.png[/img] icon", "[img]res://x.png[/img] 아이콘", true), // 데이터 태그 보존
                ("[img]res://x.png[/img] icon", "아이콘", false),                     // 데이터 태그 유실
            };
            int passed = 0;
            foreach (var (cs, cr, expect) in cases)
            {
                bool got = AutoTranslator.IsSafeResult(cs, cr);
                if (got == expect) passed++;
                else W($"  IsSafeResult MISMATCH: src='{cs}' res='{cr}' expected={expect} got={got}");
            }
            Assert(passed == cases.Length, $"DeepL safety token-preservation cases {passed}/{cases.Length}");

            // ── 버전 기반 싱크 감지 ──────────────────────────────────
            Assert(TranslationStore.SameVersion("1.0.0", "1.0.0")
                   && TranslationStore.SameVersion("v1.0.0", "1.0.0")
                   && TranslationStore.SameVersion("", "1.0.0")            // 버전 미상 → 같음(헛경고 방지)
                   && !TranslationStore.SameVersion("1.0.0", "1.1.0"),
                   "SameVersion normalize/compare");

            const string Tid = "ZZ_SyncSelfTest";
            TranslationStore.ClearRecordedTargetVersion(Tid);
            var synScan = new ScanResult();
            var synMod = new SupportedMod { Id = Tid, Name = "SyncTest", Version = "1.1.0" };

            bool s0 = TranslatorPanel.SyncTag(synScan, synMod).Length == 0;          // 기록·팩 없음 → 경고 없음
            TranslationStore.RecordTargetVersion(Tid, "1.0.0");
            string s1 = TranslatorPanel.SyncTag(synScan, synMod);                    // 번역 v1.0.0, 모드 v1.1.0
            TranslationStore.RecordTargetVersion(Tid, "1.1.0");
            bool s2 = TranslatorPanel.SyncTag(synScan, synMod).Length == 0;          // 같은 버전 → 경고 없음
            TranslationStore.ClearRecordedTargetVersion(Tid);
            synScan.Bundled.SetSourceVersion(Tid, "0.9.0");
            string s3 = TranslatorPanel.SyncTag(synScan, synMod);                    // 팩 v0.9.0, 모드 v1.1.0
            TranslationStore.ClearRecordedTargetVersion(Tid);                        // 정리

            Assert(s0 && s1.Contains("translated for v1.0.0") && s1.Contains("v1.1.0")
                   && s2 && s3.Contains("pack for v0.9.0"),
                   "SyncTag detects local + pack version drift");

            // ── CJK 원문 감지(eng 폴더에 네이티브 텍스트) ─────────────────
            // 폴더 이름이 아니라 값의 스크립트로 실제 언어를 잡는지 결정적 검증.
            static Dictionary<string, Dictionary<string, string>> Tbl(params string[] vals)
            {
                var inner = new Dictionary<string, string>();
                for (int i = 0; i < vals.Length; i++) inner["k" + i] = vals[i];
                return new Dictionary<string, Dictionary<string, string>> { ["cards"] = inner };
            }
            (string[] vals, string folder, string expect, string what)[] detCases =
            {
                (new[] { "적에게 피해를 줍니다.", "방어도를 얻습니다." }, "eng", "kor", "korean-in-eng -> kor"),
                (new[] { "{Damage:diff()} 피해를 줍니다. \n 힘을 얻습니다." }, "eng", "kor", "kor with {ph}/latin tokens -> kor"),
                (new[] { "敵にダメージを与える。" }, "eng", "jpn", "kana present -> jpn"),
                (new[] { "对敌人造成伤害。" }, "eng", "zhs", "han only -> zhs"),
                (new[] { "Deal damage to the enemy." }, "eng", "eng", "english -> folderLang"),
                (new[] { "A fairly long english sentence with only one 가 char." }, "eng", "eng", "sparse CJK -> folderLang"),
            };
            int detPass = 0;
            foreach (var (vals, folder, expect, what) in detCases)
            {
                string got = ModLocScanner.DetectContentLang(Tbl(vals), folder);
                if (got == expect) detPass++;
                else W($"  DetectContentLang MISMATCH: {what} expected={expect} got={got}");
            }
            Assert(detPass == detCases.Length, $"CJK content-language detection {detPass}/{detCases.Length}");

            // 실제 보고된 모드로 end-to-end 확인(워크샵 구독본이 로드돼 있을 때만).
            var stu = scan?.Supported.FirstOrDefault(m => m.Id == "SlayTheUniverse");
            if (stu != null)
            {
                W($"SlayTheUniverse loaded: SourceLang={stu.SourceLang} ContentLang={stu.ContentLang} ships=[{string.Join(",", stu.ShipsLangs)}]");
                Assert(stu.SourceLang == "eng", "SlayTheUniverse source folder = eng");
                Assert(stu.ContentLang == "kor", $"SlayTheUniverse content detected = kor (got '{stu.ContentLang}')");
                // 게이팅: 현재 kor 로 플레이 중 → ContentLang==현재언어 라 이 모드는 주입 스킵 대상(자기 원문).
                // eng 는 이제 정상 번역 대상(ContentLang 만 제외되므로).
                Assert(!string.Equals("eng", stu.ContentLang, StringComparison.OrdinalIgnoreCase),
                    "eng is a valid translation target (only ContentLang excluded)");
            }
            else W("SlayTheUniverse not loaded — skipping real-mod assertion (synthetic detection cases still cover it)");

            // 유저가 이어서 쓸 수 있게 원래 언어 복귀(어차피 비저장이지만 시각적으로도 원상복구).
            if (!string.Equals(originalLang, "kor", StringComparison.OrdinalIgnoreCase))
            {
                mgr.SetLanguage(originalLang);
                await Task.Delay(500);
            }

            W("=== solo gate test done ===");
            Flush(ok);
        }
        catch (Exception e) { W("test exception: " + e); Flush(false); }
    }

    // Godot 루트 뷰포트를 selftest.sp.<name>.png 로 저장(시각 증거 필수).
    private static async Task Shot(string name)
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return;
            await Task.Delay(120);
            var img = tree.Root.GetTexture()?.GetImage();
            if (img == null) { W($"shot {name}: null image"); return; }
            string p = Path.Combine(ModDir(), $"selftest.sp.{name}.png");
            var err = img.SavePng(p);
            W($"shot {name}: {(err == Error.Ok ? $"saved {img.GetWidth()}x{img.GetHeight()} -> {Path.GetFileName(p)}" : "err " + err)}");
        }
        catch (Exception e) { W($"shot {name} failed: {e.Message}"); }
    }

    private static void W(string line)
    {
        _out.AppendLine(line);
        MainFile.Logger.Info($"[Sts2ModTranslator] SOLO | {line}");
    }

    private static void Flush(bool ok)
    {
        _done = true;
        _out.Insert(0, ok ? "RESULT: OK\n" : "RESULT: FAIL\n");
        try { File.WriteAllText(Path.Combine(ModDir(), "selftest.sp.txt"), _out.ToString()); } catch { }
    }
}
#endif
