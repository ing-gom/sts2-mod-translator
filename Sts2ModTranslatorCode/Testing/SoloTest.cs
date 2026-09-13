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
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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

            // DeepL 언어 매핑 커버리지 — 게임 언어 드롭다운(NLanguageDropdown)의 전 코드가
            // target_lang 으로 매핑돼야 한다. 누락되면 auto-fill 이 그 언어에서 통째로 막힌다.
            string[] gameLangs =
            {
                "ara", "ben", "cze", "deu", "dut", "eng", "esp", "fil", "fin", "fra",
                "gre", "hin", "ind", "ita", "jpn", "kor", "mal", "nor", "pol", "por",
                "ptb", "rus", "spa", "swe", "tha", "tur", "ukr", "vie", "zhs", "zht",
            };
            var unmapped = gameLangs.Where(l => AutoTranslator.DeepLTarget(l) == null).ToList();
            Assert(unmapped.Count == 0,
                unmapped.Count == 0
                    ? $"DeepL target mapping covers all {gameLangs.Length} game languages"
                    : "DeepL target mapping missing: " + string.Join(", ", unmapped));

            // OpenAI 호환 공급자는 벤더 코드가 아니라 영문 언어명을 프롬프트에 쓴다.
            // 매핑이 빠지면 코드가 그대로 새어 나가 ("ptb 로 번역하라") 번역 품질이 조용히 망가진다.
            var unnamed = gameLangs
                .Where(l => string.Equals(AutoTranslator.LangName(l), l, StringComparison.Ordinal))
                .ToList();
            Assert(unnamed.Count == 0,
                unnamed.Count == 0
                    ? $"LangName covers all {gameLangs.Length} game languages"
                    : "LangName missing (falls back to raw code): " + string.Join(", ", unnamed));

            // 엔드포인트 주소 정규화 — 사용자가 넣는 흔한 형태를 전부 흡수해야 한다.
            (string input, string want)[] urlCases =
            {
                ("https://api.openai.com/v1",  "https://api.openai.com/v1/chat/completions"),
                ("https://api.openai.com/v1/", "https://api.openai.com/v1/chat/completions"),
                ("https://api.openai.com/v1/chat/completions",
                 "https://api.openai.com/v1/chat/completions"),
                ("http://localhost:11434",     "http://localhost:11434/v1/chat/completions"),
                ("http://localhost:11434/",    "http://localhost:11434/v1/chat/completions"),
                ("",                           ""),
            };
            int urlPass = 0;
            foreach (var (ui, uw) in urlCases)
            {
                string got = AutoConfig.ChatUrl(ui);
                if (got == uw) urlPass++;
                else W($"  ChatUrl MISMATCH: '{ui}' -> '{got}' (want '{uw}')");
            }
            Assert(urlPass == urlCases.Length, $"AI endpoint URL normalization {urlPass}/{urlCases.Length}");

            // ★응답 파싱 — LLM 은 항목을 흘리거나 잡담·코드펜스를 섞는다. 한 항목의 실패가
            // 배치 전체를 죽이면 안 되고, 빠진 항목은 빈 문자열이어야 한다(호출부가 빈칸 유지).
            string Chat(string content) => JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content } } },
            });
            (string body, int n, string[] want, string what)[] chatCases =
            {
                (Chat("{\"1\":\"가\",\"2\":\"나\"}"), 2, new[] { "가", "나" }, "plain object"),
                (Chat("{\"2\":\"나\",\"1\":\"가\"}"), 2, new[] { "가", "나" },
                    "order-independent (keyed, not positional)"),
                (Chat("{\"1\":\"가\"}"), 2, new[] { "가", "" }, "missing item -> empty, not failure"),
                (Chat("```json\n{\"1\":\"가\",\"2\":\"나\"}\n```"), 2, new[] { "가", "나" },
                    "markdown fence tolerated"),
                (Chat("Sure! Here you go: {\"1\":\"가\",\"2\":\"나\"} Hope this helps."), 2,
                    new[] { "가", "나" }, "chatty preamble/epilogue tolerated"),
                (Chat("I cannot do that."), 2, new[] { "", "" }, "no JSON -> all empty"),
                (Chat("{\"1\": 42, \"2\":\"나\"}"), 2, new[] { "", "나" }, "non-string value -> empty"),
            };
            int chatPass = 0;
            foreach (var (cb, cn, cw, cwhat) in chatCases)
            {
                try
                {
                    var got = AutoTranslator.ParseChatTranslations(cb, cn);
                    if (got.Count == cn && got.SequenceEqual(cw)) chatPass++;
                    else W($"  ParseChat MISMATCH [{cwhat}]: got [{string.Join("|", got)}]"
                           + $" want [{string.Join("|", cw)}]");
                }
                catch (Exception ex) { W($"  ParseChat THREW [{cwhat}]: {ex.Message}"); }
            }
            Assert(chatPass == chatCases.Length,
                $"AI response parsing (missing/fenced/chatty) {chatPass}/{chatCases.Length}");

            // ★OpenAI 호환 경로 end-to-end — 루프백 서버로 실제 왕복시킨다.
            // 파싱은 위에서 봤지만 "요청을 어떻게 만들어 보내는가"(주소 정규화 결과·모델명·본문)는
            // 네트워크를 태워 보기 전에는 증명되지 않는다. HttpListener 는 Windows 에서 urlacl 이
            // 필요할 수 있어 TcpListener 로 최소 HTTP 를 직접 말한다(권한 불필요).
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                string received = "";

                var serve = Task.Run(async () =>
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    using var ns = client.GetStream();
                    var buf = new byte[64 * 1024];
                    var sb = new StringBuilder();
                    for (int r = 0; r < 4; r++)      // 헤더+본문이 한 번에 안 올 수 있다
                    {
                        int got = await ns.ReadAsync(buf, 0, buf.Length);
                        if (got <= 0) break;
                        sb.Append(Encoding.UTF8.GetString(buf, 0, got));
                        if (sb.ToString().Contains("\"model\"")) break;
                    }
                    received = sb.ToString();

                    string payload = JsonSerializer.Serialize(new
                    {
                        choices = new[]
                        {
                            new { message = new { content = "{\"1\":\"<ph>{0}</ph> 피해를 줍니다.\"}" } },
                        },
                    });
                    byte[] body = Encoding.UTF8.GetBytes(payload);
                    byte[] head = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "
                        + body.Length + "\r\nConnection: close\r\n\r\n");
                    await ns.WriteAsync(head, 0, head.Length);
                    await ns.WriteAsync(body, 0, body.Length);
                    await ns.FlushAsync();
                });

                var loopCfg = new AutoConfig
                {
                    Provider = AutoProvider.OpenAiCompatible,
                    BaseUrl = $"http://127.0.0.1:{port}",     // ★경로 없는 호스트 → /v1 보충돼야 함
                    Model = "solo-test-model",
                };
                var (tok, tmsg) = await AutoTranslator.TestAsync("kor", loopCfg);
                await Task.WhenAny(serve, Task.Delay(3000));
                listener.Stop();

                Assert(tok, $"OpenAI-compatible round trip over loopback -> {tmsg}");
                Assert(received.Contains("POST /v1/chat/completions", StringComparison.Ordinal),
                    "bare host got '/v1/chat/completions' appended  (got: "
                    + new string(received.TakeWhile(c => c != '\r').ToArray()) + ")");
                Assert(received.Contains("solo-test-model", StringComparison.Ordinal),
                    "model name travelled in the request body");
                // ★와이어에서 '<ph>' 를 찾으면 안 된다 — System.Text.Json 은 기본이 HTML-safe
                // 이스케이프라 '<'/'>' 가 \u003C/\u003E 로 나간다(수신측이 디코드하므로 기능은 무해).
                // 그래서 바이트가 아니라 "엔드포인트가 실제로 읽는 내용"을 확인한다.
                string sentUser = "";
                try
                {
                    int hdrEnd = received.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    string reqBody = hdrEnd >= 0 ? received.Substring(hdrEnd + 4) : received;
                    using var rd = JsonDocument.Parse(reqBody);
                    var msgs = rd.RootElement.GetProperty("messages");
                    sentUser = msgs[msgs.GetArrayLength() - 1].GetProperty("content").GetString() ?? "";
                }
                catch (Exception ex) { W("  request body parse failed: " + ex.Message); }

                Assert(sentUser.Contains("<ph>", StringComparison.Ordinal),
                    "masked placeholder tags reach the endpoint intact (after JSON decoding)");
                Assert(sentUser.TrimStart().StartsWith("{", StringComparison.Ordinal),
                    "items are sent as a number-keyed JSON object (the ordering contract)");
            }
            catch (Exception ex) { Assert(false, "OpenAI loopback round trip threw: " + ex.Message); }

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

            // ── 혼합(부분 번역) 원문 감지 — CJK↔라틴 + CJK끼리, 비율 무관(외국 항목 ≥2) ──────
            // 각 항목을 우세 언어로 분류(한글=한국어·가나=일본어·순수한자=중국어·라틴=영어권)한 뒤
            // ContentLang 과 다른 언어 항목이 2개 이상이면 mixed. 한자-only 는 中日 애매라 일본어 모드에선 미계상.
            static Dictionary<string, Dictionary<string, string>> Bld(params (string lang, int n)[] parts)
            {
                static string Sample(string lang) => lang switch
                {
                    "eng" => "Deal damage to the enemy and gain block.",  // 라틴
                    "zhs" => "对敌人造成伤害并获得格挡。",                    // 순수 한자
                    "kor" => "적에게 피해를 주고 방어도를 얻는다.",           // 한글 우세
                    "jpn" => "敵にダメージを与えてブロックを得る。",          // 가나 포함 = 일본어
                    "han" => "攻撃力上昇値界",                              // 한자-only(中日 애매)
                    _ => "?",
                };
                var inner = new Dictionary<string, string>();
                int idx = 0;
                foreach (var (lang, n) in parts)
                    for (int i = 0; i < n; i++) inner[$"{lang}{idx++}"] = Sample(lang);
                return new Dictionary<string, Dictionary<string, string>> { ["cards"] = inner };
            }
            (Dictionary<string, Dictionary<string, string>> tbl, string folder, string lang, bool mixed, string what)[] mixCases =
            {
                (Bld(("eng",20),("zhs",6)),  "eng", "eng", true,  "eng + chinese -> mixed"),
                (Bld(("eng",30)),            "eng", "eng", false, "pure english -> not mixed"),
                (Bld(("eng",60),("zhs",1)),  "eng", "eng", false, "eng + 1 chinese -> not mixed (floor 2)"),
                (Bld(("eng",60),("zhs",2)),  "eng", "eng", true,  "eng + 2 chinese -> mixed (low ratio caught)"),
                (Bld(("zhs",20)),            "zhs", "zhs", false, "pure chinese -> not mixed"),
                (Bld(("zhs",100),("jpn",3)), "zhs", "zhs", true,  "chinese + japanese(kana) -> mixed (CJK vs CJK)"),
                (Bld(("zhs",40),("kor",3)),  "zhs", "zhs", true,  "chinese + korean(hangul) -> mixed (CJK vs CJK)"),
                (Bld(("kor",30),("zhs",4)),  "kor", "kor", true,  "korean + chinese(hanja) -> mixed (CJK vs CJK)"),
                (Bld(("jpn",30)),            "jpn", "jpn", false, "pure japanese -> not mixed"),
                (Bld(("jpn",30),("han",4)),  "jpn", "jpn", false, "japanese + kanji-only -> not mixed (conservative)"),
            };
            int mixPass = 0;
            foreach (var (tbl, folder, expLang, expMix, what) in mixCases)
            {
                string got = ModLocScanner.DetectContentLang(tbl, folder, out bool gotMix);
                if (got == expLang && gotMix == expMix) mixPass++;
                else W($"  mixed-source MISMATCH: {what} expected=({expLang},{expMix}) got=({got},{gotMix})");
            }
            Assert(mixPass == mixCases.Length, $"mixed-source detection {mixPass}/{mixCases.Length}");

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

            // ── 원문 언어 override 주입(원문 위 리라이트) ─────────────────
            // 현재 kor 로 세팅돼 있으므로 ContentLang==kor 합성 모드로, "원문 언어일 때 비어있지 않은
            // override 만 원문 위에 덮어쓴다 / 없으면 no-op"을 base-game main_menu_ui 테이블에서 결정적 검증.
            {
                const string OrigMod = "ZZ_OrigOverrideSelfTest";
                const string OKey = "ZZ-ORIG-OVERRIDE";
                const string OVal = "덮어쓴 원문 {0}";

                // (a) LoadNonEmptyOverrides: 빈 값 제외, 비어있지 않은 값만.
                TranslationStore.SaveOverrideText(OrigMod, "kor", Table,
                    $"{{\"{OKey}\":\"{OVal}\",\"ZZ-EMPTY\":\"\"}}");
                var loaded = TranslationStore.LoadNonEmptyOverrides(OrigMod, "kor", Table);
                Assert(loaded.Count == 1 && loaded.TryGetValue(OKey, out var lv) && lv == OVal,
                    "LoadNonEmptyOverrides returns only non-empty entries");

                // (b) InjectOriginalOverrides: ContentLang==현재언어 모드의 non-empty override 가 실제 주입됨.
                var origMod = new SupportedMod { Id = OrigMod, Name = "OrigOverride", ContentLang = "kor", SourceLang = "eng" };
                origMod.ByLang["eng"] = new Dictionary<string, Dictionary<string, string>>
                { [Table] = new Dictionary<string, string> { [OKey] = "original {0}" } };
                int inj = TranslationSync.InjectOriginalOverrides(mgr, origMod, "kor");
                LocTable? ot = null; try { ot = mgr.GetTable(Table); } catch { /* asserted below */ }
                Assert(inj >= 1 && ot != null && ot.HasEntry(OKey) && ot.GetRawText(OKey) == OVal,
                    "original-language non-empty override IS injected over the original");

                // (c) no-op: override 없는 모드는 아무 것도 주입하지 않음(원문 그대로).
                var cleanMod = new SupportedMod { Id = "ZZ_CleanNoOverride", Name = "Clean", ContentLang = "kor", SourceLang = "eng" };
                cleanMod.ByLang["eng"] = new Dictionary<string, Dictionary<string, string>>
                { [Table] = new Dictionary<string, string> { ["ZZ-CLEAN-KEY"] = "original" } };
                int injClean = TranslationSync.InjectOriginalOverrides(mgr, cleanMod, "kor");
                LocTable? ct = null; try { ct = mgr.GetTable(Table); } catch { }
                Assert(injClean == 0 && (ct == null || !ct.HasEntry("ZZ-CLEAN-KEY")),
                    "mod with no overrides = no-op (original text untouched)");

                // 정리: 합성 override 를 빈 객체로 중화(다음 실행 오염 방지). 테이블 주입은 SetLanguage 로 리셋됨.
                TranslationStore.SaveOverrideText(OrigMod, "kor", Table, "{}");
            }

            // ── 원문 변경(stale) 감지 — baseline 스냅샷 vs 현재 원문 ─────────────
            // 게임 없이 결정적으로: 번역 저장 시 baseline 이 기록되고, 원문이 바뀐 항목만 stale 로
            // 잡히며, 무관한 키를 저장해도 낡음이 유지되고, 재번역하면 해소되는지 검증한다.
            {
                const string SMod = "ZZ_StaleSelfTest";
                const string SLang = "kor";
                const string STable = "cards";
                var srcV1 = new Dictionary<string, string> { ["A"] = "Deal 5 damage.", ["B"] = "Gain 5 block." };
                var modV1 = new SupportedMod { Id = SMod, Name = "Stale", Version = "1.0.0", SourceLang = "eng" };
                modV1.ByLang["eng"] = new Dictionary<string, Dictionary<string, string>> { [STable] = srcV1 };

                // 이전 실행 잔재 제거(override + baseline).
                TranslationStore.ResetOverride(modV1, SLang, STable);
                TranslationStore.ClearRecordedTargetVersion(SMod);

                // A·B 를 v1 원문 기준으로 번역 → baseline 이 srcV1 을 기록.
                TranslationStore.SaveOverrideText(SMod, SLang, STable,
                    "{\"A\":\"5 피해.\",\"B\":\"5 방어도.\"}", srcV1);
                bool st0 = TranslationStore.StaleKeys(modV1, SLang, STable).Count == 0; // 원문 그대로 → 없음

                // 모드 업데이트: A 의 원문이 바뀌고 B 는 그대로.
                var srcV2 = new Dictionary<string, string> { ["A"] = "Deal 8 damage.", ["B"] = "Gain 5 block." };
                var modV2 = new SupportedMod { Id = SMod, Name = "Stale", Version = "1.1.0", SourceLang = "eng" };
                modV2.ByLang["eng"] = new Dictionary<string, Dictionary<string, string>> { [STable] = srcV2 };
                var stale1 = TranslationStore.StaleKeys(modV2, SLang, STable);
                bool st1 = stale1.Count == 1 && stale1[0].key == "A"
                           && stale1[0].oldSource == "Deal 5 damage." && stale1[0].newSource == "Deal 8 damage.";

                // 무관한 키(B)만 편집·저장 → A 는 여전히 stale(건드리지 않았으므로 낡음 유지).
                TranslationStore.SaveOverrideText(SMod, SLang, STable,
                    "{\"A\":\"5 피해.\",\"B\":\"5 방어도 획득.\"}", srcV2);
                var stale2 = TranslationStore.StaleKeys(modV2, SLang, STable);
                bool st2 = stale2.Count == 1 && stale2[0].key == "A";

                // A 를 새 원문 기준으로 재번역·저장 → A 해소.
                TranslationStore.SaveOverrideText(SMod, SLang, STable,
                    "{\"A\":\"8 피해.\",\"B\":\"5 방어도 획득.\"}", srcV2);
                bool st3 = TranslationStore.StaleKeys(modV2, SLang, STable).Count == 0;

                if (!(st0 && st1 && st2 && st3))
                    W($"  stale detail: st0={st0} st1={st1} st2={st2} st3={st3}");
                Assert(st0 && st1 && st2 && st3,
                    "StaleKeys detects changed source, survives unrelated save, clears on re-translate");

                // 정리.
                TranslationStore.ResetOverride(modV2, SLang, STable);
                TranslationStore.ClearRecordedTargetVersion(SMod);
            }

            // ── 원문 단어 diff(SourceDiff) — 순수 알고리즘, 게임 무관 ───────────────
            {
                string d1 = SourceDiff.Describe("Deal 5 damage.", "Deal 8 damage.");
                string d2 = SourceDiff.Describe("Deal 5 damage.", "Deal 5 damage.");
                string d3 = SourceDiff.Describe("Gain Block.", "Gain 5 Block.");
                string d4 = SourceDiff.Describe("Deal 5 fire damage.", "Deal 5 damage.");
                if (!(d1.Contains("⟨5→8⟩") && d1.Contains("Deal") && d1.Contains("damage.")))
                    W($"  SourceDiff d1='{d1}'");
                if (d2.Length != 0) W($"  SourceDiff d2='{d2}' (expected empty)");
                if (!d3.Contains("⟨+5⟩")) W($"  SourceDiff d3='{d3}' (expected +5 insert)");
                if (!d4.Contains("⟨-")) W($"  SourceDiff d4='{d4}' (expected deletion)");
                Assert(d1.Contains("⟨5→8⟩") && d1.Contains("Deal") && d1.Contains("damage.")
                       && d2.Length == 0 && d3.Contains("⟨+5⟩") && d4.Contains("⟨-"),
                    "SourceDiff word-level: replace / identical / insert / delete");
            }

            // ── 모드별 용어집 저장 + 정합성 검사(GlossaryIssues) ───────────────────
            {
                const string GMod = "ZZ_GlossarySelfTest";
                const string GLang = "kor";
                const string GTable = "cards";
                var gmod = new SupportedMod { Id = GMod, Name = "Gloss", Version = "1.0.0", SourceLang = "eng" };
                gmod.ByLang["eng"] = new Dictionary<string, Dictionary<string, string>>
                {
                    [GTable] = new Dictionary<string, string>
                    { ["A"] = "Apply Vulnerable to the enemy.", ["B"] = "Deal damage." }
                };

                // 용어집: Vulnerable → 취약.
                TranslationStore.SaveModGlossary(GMod, GLang, new Dictionary<string, string> { ["Vulnerable"] = "취약" });
                var loaded = TranslationStore.LoadModGlossary(GMod, GLang);
                bool g0 = loaded.Count == 1 && loaded.TryGetValue("Vulnerable", out var gv) && gv == "취약";

                // 번역이 정식 표기를 씀 → 불일치 없음.
                TranslationStore.SaveOverrideText(GMod, GLang, GTable,
                    "{\"A\":\"적에게 취약을 부여.\",\"B\":\"피해를 줍니다.\"}");
                bool g1 = TranslationStore.GlossaryIssues(gmod, GLang, GTable).Count == 0;

                // A 번역이 '취약'을 안 씀(원문엔 Vulnerable 있음) → A 만 플래그.
                TranslationStore.SaveOverrideText(GMod, GLang, GTable,
                    "{\"A\":\"적에게 약화를 부여.\",\"B\":\"피해를 줍니다.\"}");
                var issues = TranslationStore.GlossaryIssues(gmod, GLang, GTable);
                bool g2 = issues.Count == 1 && issues[0].key == "A"
                          && issues[0].term == "Vulnerable" && issues[0].expected == "취약";

                // 용어집 비우면 검사도 없음.
                TranslationStore.SaveModGlossary(GMod, GLang, new Dictionary<string, string>());
                bool g3 = TranslationStore.GlossaryIssues(gmod, GLang, GTable).Count == 0;

                if (!(g0 && g1 && g2 && g3)) W($"  glossary detail: g0={g0} g1={g1} g2={g2} g3={g3}");
                Assert(g0 && g1 && g2 && g3,
                    "Mod glossary: save/load + flags entries missing the term, clears when emptied");

                TranslationStore.ResetOverride(gmod, GLang, GTable); // 정리
            }

            // ── UI 렌더 검증: 언어 목록 맨 위 '✎ original' 행이 실제로 그려지는가(v1.14.4) ──
            // BuildLanguages 는 solo 로직 assert 로는 안 타므로, 패널을 리플렉션으로 열어 언어뷰까지 몰고
            // 가 스크린샷을 남긴다. ContentLang=eng 모드(Black Souls 시나리오) 우선.
            try
            {
                var tp = typeof(TranslatorPanel);
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
                // 패널은 메인메뉴 버튼 패치가 붙여 준다 — 모드가 많은 환경에선 이 시점보다 늦을 수
                // 있어 즉시 판정하면 레이스로 스킵된다(실측: 3회 중 1회만 부착). 잠깐 기다린다.
                bool paneled = false;
                for (int w = 0; w < 40 && !paneled; w++)
                {
                    paneled = tp.GetField("_root", flags)?.GetValue(null) != null;
                    if (!paneled) await Task.Delay(200);
                }
                if (!paneled) W("UI drive: panel not attached (menu button missing) — skipping render shot");
                else
                {
                    tp.GetMethod("ShowPanel", flags)?.Invoke(null, null);
                    await Task.Delay(300);
                    await Shot("3_panel_mods");
                    var uiScan = TranslationSync.CurrentScan;
                    var mod = uiScan?.Supported.FirstOrDefault(m => string.Equals(m.ContentLang, "eng", StringComparison.OrdinalIgnoreCase))
                              ?? uiScan?.Supported.FirstOrDefault();
                    if (mod != null)
                    {
                        tp.GetField("_mod", flags)?.SetValue(null, mod);
                        var viewType = tp.GetNestedType("View", System.Reflection.BindingFlags.NonPublic);
                        var nav = tp.GetMethod("Navigate", flags);
                        nav?.Invoke(null, new[] { Enum.Parse(viewType!, "Languages") });
                        await Task.Delay(450);
                        W($"UI drive: opened Languages view for '{mod.Id}' (ContentLang={mod.ContentLang}) — '✎ original' row should be at top");
                        await Shot("4_languages_" + mod.ContentLang);
                    }
                    else W("UI drive: no supported mod to open");

                    // ── 자동번역 설정 모달(v1.19) — 공급자 선택 UI 가 실제로 그려지는지 ──
                    // 로직 assert 로는 레이아웃 붕괴(칸 겹침·잘림)를 못 잡는다. 두 공급자 상태를
                    // 모두 찍어 드롭다운 전환이 섹션을 실제로 바꾸는지까지 시각 증거로 남긴다.
                    tp.GetMethod("PromptApiKey", flags)?.Invoke(null, new object?[] { null });
                    await Task.Delay(400);

                    // 다이얼로그는 패널 루트(_root)의 자식이다 — SceneTree.Root 부터 훑으면
                    // 임베디드 서브윈도 배치에 따라 놓칠 수 있으므로 부모에서 바로 찾는다.
                    var panelRoot = tp.GetField("_root", flags)?.GetValue(null) as Node;

                    // ★스크린샷에 실제 API 키가 찍히지 않게 가린다. selftest.* 는 업로더·gitignore 가
                    // 제외하지만, 키를 디스크의 PNG 로 남길 이유 자체가 없다.
                    if (panelRoot != null)
                        foreach (var le in FindAllByType<LineEdit>(panelRoot))
                            if (le.Text.Length > 12 && !le.Text.StartsWith("http", StringComparison.Ordinal))
                                le.Text = new string('*', 8) + "-****-****-****-************:fx";
                    await Task.Delay(120);
                    await Shot("5_setup_deepl");

                    // 드롭다운을 'AI endpoint' 로 돌려 두 번째 섹션을 그린다.
                    var picker = panelRoot == null ? null : FindNodeByType<OptionButton>(panelRoot);
                    if (picker != null && picker.ItemCount >= 2)
                    {
                        picker.Selected = 1;
                        picker.EmitSignal(OptionButton.SignalName.ItemSelected, 1);
                        await Task.Delay(350);
                        W("UI drive: switched auto-fill provider dropdown to 'AI endpoint'");
                        await Shot("6_setup_ai");
                    }
                    else W("UI drive: provider dropdown not found — setup dialog may not have opened");

                    // ★모달을 반드시 닫는다 — 남겨 두면 Save 가 눌리지 않아 설정이 바뀌진 않지만,
                    // 이후 스크린샷과 패널 Hide 를 가린다.
                    if (panelRoot != null)
                        foreach (var dlg in FindAllByType<AcceptDialog>(panelRoot)) dlg.QueueFree();
                    await Task.Delay(150);

                    tp.GetMethod("Hide", flags)?.Invoke(null, null);
                    await Task.Delay(150);
                }
            }
            catch (Exception ex) { W("UI drive FAILED: " + ex.Message); }

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
    /// <summary>scene tree 에서 타입이 일치하는 첫 노드를 찾는다(UI 구동용).</summary>
    private static T? FindNodeByType<T>(Node root) where T : Node
    {
        if (root is T hit) return hit;
        foreach (var c in root.GetChildren())
            if (c is Node n) { var r = FindNodeByType<T>(n); if (r != null) return r; }
        return null;
    }

    /// <summary>scene tree 에서 타입이 일치하는 모든 노드(모달 정리용).</summary>
    private static List<T> FindAllByType<T>(Node root) where T : Node
    {
        var outp = new List<T>();
        void Walk(Node n)
        {
            if (n is T hit) outp.Add(hit);
            foreach (var c in n.GetChildren()) if (c is Node cn) Walk(cn);
        }
        Walk(root);
        return outp;
    }

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
