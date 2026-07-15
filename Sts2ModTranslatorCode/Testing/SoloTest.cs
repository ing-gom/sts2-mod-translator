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
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes;
using Sts2ModTranslator.Core;

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
