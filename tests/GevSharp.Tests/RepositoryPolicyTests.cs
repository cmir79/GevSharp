using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace GevSharp.Tests;

/// <summary>
/// 저장소 자체에 거는 정책 — 의존성·커밋 대상·개행·비동기 규율. 코드가 아니라 저장소의 상태를 보는 검사라
/// 어느 한 곳을 고치는 것으로는 지킬 수 없고, 어기는 순간(새 패키지 추가, 벤더 XML 커밋, 개행 손상,
/// 동기 대기 삽입) 스위트가 붉어진다.
/// <para>
/// 저장소가 아닌 곳(소스 압축본 등)에서 돌리면 git 에 기대는 검사는 근거가 없다 — 그때는 건너뛰는 대신
/// 그 사실을 분명히 말하고 통과시킨다. CI 는 언제나 체크아웃이므로 실제로는 항상 검사한다.
/// </para>
/// </summary>
public class RepositoryPolicyTests
{
    /// <summary>이 라이브러리가 실을 수 있는 패키지의 전부. 늘리는 것은 판단이 필요한 일이라 여기에 손을 대야만 늘어난다.</summary>
    private static readonly string[] AllowedPackages =
    {
        // 빌드에만 쓰고 실려 나가지 않는 것
        "PolySharp",                                    // 언어 기능 폴리필 생성기 (PrivateAssets=all)
        "Microsoft.NET.ILLink.Tasks",                    // SDK 가 넣는 트리밍 태스크
        "NETStandard.Library",                           // netstandard 타겟팅 팩
        // netstandard2.0 자산에만 붙는 런타임 의존성 (전부 MIT / .NET Foundation)
        "Microsoft.Bcl.AsyncInterfaces",
        "System.Memory",
        "System.Threading.Tasks.Extensions",
        // 위 셋이 끌고 오는 것
        "Microsoft.NETCore.Platforms",
        "System.Buffers",
        "System.Numerics.Vectors",
        "System.Runtime.CompilerServices.Unsafe",
    };

    /// <summary>커밋해도 되는 XML·ZIP 의 전부 — 전부 손으로 쓴 픽스처다. 벤더 카메라 XML 은 하나도 들어오지 않는다.</summary>
    private static readonly string[] AllowedXmlAssets =
    {
        "tests/GevSharp.Sim/Assets/SimCamera.xml",
        "tests/GevSharp.Tests/Fixtures/GenApi/groups.xml",
        "tests/GevSharp.Tests/Fixtures/GenApi/minimal.xml",
        "tests/GevSharp.Tests/Fixtures/XmlLoaderMinimal.xml",
    };

    /// <summary>
    /// 라이브러리 코드에서 금지하는 동기 대기. 이름은 오류 메시지에 그대로 실린다.
    /// <see cref="Task.Run(Action)"/> 은 여기 없다 — 스레드 Join 이나 블로킹 소켓 대기를 스레드풀로 옮기는 정당한 쓰임이고,
    /// 비동기 결과를 동기로 기다리는 것과는 다르다.
    /// </summary>
    private static readonly (string Name, Regex Pattern)[] ForbiddenSyncWaits =
    {
        ("GetAwaiter().GetResult()", new Regex(@"GetAwaiter\(\)\s*\.\s*GetResult\(\)", RegexOptions.Compiled)),
        (".Result", new Regex(@"\.Result\b(?!\s*=)", RegexOptions.Compiled)),
        (".Wait()", new Regex(@"\.Wait\(\s*\)", RegexOptions.Compiled)),
        ("Task.WaitAll/WaitAny", new Regex(@"\bTask\s*\.\s*Wait(All|Any)\s*\(", RegexOptions.Compiled)),
        ("RunSynchronously()", new Regex(@"\.RunSynchronously\(", RegexOptions.Compiled)),
    };

    // ---------------------------------------------------------------- R22: 상업·카피레프트 의존성 없음

    [Fact]
    public void OnlyTheAllowedPackagesReachTheLibrary_IncludingTransitiveOnes()
    {
        // 직접 추가한 것만 보면 전이 의존성으로 들어오는 것을 놓친다 — 복원이 실제로 고른 목록을 본다.
        var assets = Path.Combine(RepoRoot, "src", "GevSharp", "obj", "project.assets.json");
        Assert.True(File.Exists(assets), $"{assets} is missing; run a restore before this test");

        var resolved = new SortedSet<string>(StringComparer.Ordinal);
        var frameworks = new List<string>();
        foreach (var (framework, libraries) in ReadAssetTargets(assets))
        {
            frameworks.Add(framework);
            foreach (var lib in libraries) resolved.Add(lib);
        }

        // 세 TFM 모두 복원되어 있어야 한다 — 하나가 빠지면 그 자산의 의존성을 본 적이 없는 것이다.
        Assert.Contains("netstandard2.0", frameworks);
        Assert.Contains("netstandard2.1", frameworks);
        Assert.Contains("net8.0", frameworks);

        var unexpected = resolved.Where(r => !AllowedPackages.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
        Assert.True(unexpected.Count == 0,
            "GevSharp resolved package(s) that are not on the allow-list in RepositoryPolicyTests.AllowedPackages: "
            + string.Join(", ", unexpected)
            + ". Every dependency of this library must be permissively licensed and deliberate — add it to the list only after checking its licence and its own transitive closure.");
    }

    // ---------------------------------------------------------------- R23: 벤더 XML 없음

    [Fact]
    public void NoXmlOrZipAssetIsCommittedBeyondTheHandWrittenFixtures()
    {
        if (!TryGit("ls-files -z -- *.xml *.XML *.zip *.ZIP", out var output)) return;

        var tracked = output.Split('\0', StringSplitOptions.RemoveEmptyEntries).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var unexpected = tracked.Where(t => !AllowedXmlAssets.Contains(t, StringComparer.Ordinal)).ToList();
        Assert.True(unexpected.Count == 0,
            "XML/ZIP file(s) committed that are not the hand-written fixtures: " + string.Join(", ", unexpected)
            + ". Vendor camera descriptions must never be committed — keep them local and point GEVSHARP_VENDOR_XML at them.");
        // 목록이 조용히 줄어드는 것도(픽스처가 사라지는 것도) 알아야 한다.
        Assert.Equal(AllowedXmlAssets.OrderBy(s => s, StringComparer.Ordinal), tracked);
    }

    // ---------------------------------------------------------------- R24: 개행

    [Fact]
    public void EveryTrackedTextFileIsStoredWithLfAndCheckedOutAsCrlf()
    {
        if (!TryGit("ls-files --eol", out var output)) return;

        // git 이 판정한 index 쪽 개행만 본다 — 작업 트리의 CR 하나가 clean 필터를 지나며 접혀
        // 파일을 바이트로 세면 0 인데 커밋에는 손상이 들어가는 경우가 있다.
        var bad = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (text.Length == 0) continue;
            var index = Regex.Match(text, @"^i/(\S+)");
            if (!index.Success) continue;
            var kind = index.Groups[1].Value;
            // i/-text = 바이너리(폰트·이미지). 텍스트 규칙은 걸리지 않는다.
            if (kind is "lf" or "-text") continue;
            bad.Add(text.Trim());
        }

        Assert.True(bad.Count == 0,
            "file(s) stored with the wrong line endings (the repository stores LF and checks out CRLF; i/crlf or i/mixed means a CR leaked into a commit): "
            + string.Join(" | ", bad));
    }

    // ---------------------------------------------------------------- R26: 동기 대기 없음

    [Fact]
    public void TheLibraryNeverBlocksOnAnAsyncResult()
    {
        var root = Path.Combine(RepoRoot, "src", "GevSharp");
        Assert.True(Directory.Exists(root), $"{root} is missing");

        var offences = new List<string>();
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            // 빌드 산출물(obj 안의 생성 코드)은 우리 코드가 아니다.
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            scanned++;
            var lines = File.ReadAllText(file).Split('\n');       // 개행 종류와 무관하게 자른다
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var (name, pattern) in ForbiddenSyncWaits)
                {
                    if (pattern.IsMatch(lines[i]))
                        offences.Add($"{Relative(file)}:{i + 1} uses {name}");
                }
            }
        }

        // 검사기가 죽지 않았는지 — 볼 파일이 있어야 한다.
        Assert.True(scanned > 20, $"only {scanned} library source file(s) were scanned; the scan is not looking where it should");
        Assert.True(offences.Count == 0,
            "register access must be async end to end; blocking on an async result starves the thread pool under load: "
            + string.Join(" | ", offences));
    }

    // ---------------------------------------------------------------- 공개 표면과 문서

    [Fact]
    public void EveryExportedTypeIsNamedInTheArchitectureDocument()
    {
        // 공개 타입은 계약이다 — 문서에 이름조차 없는 타입이 나가면 쓰는 쪽은 그것을 어떻게 다뤄야 하는지 알 길이 없다.
        // 한 번 세어 보는 스크립트로는 다음에 늘어난 것을 잡지 못하므로 스위트에 둔다.
        // docs/ 전체를 본다 — 모듈 경계는 architecture.md, GenApi XML 모델은 genapi-model.md 처럼 문서가 나뉘어 있다.
        var doc = string.Join(" ", Directory.EnumerateFiles(Path.Combine(RepoRoot, "docs"), "*.md").Select(File.ReadAllText));
        var exported = typeof(GevDevice).Assembly.GetExportedTypes();

        Assert.True(exported.Length > 80, $"only {exported.Length} exported types were found; the check is looking at the wrong assembly");

        var missing = new List<string>();
        foreach (var t in exported)
        {
            // 제네릭 표기(`1)와 중첩 타입 구분자를 뗀 이름으로 찾는다.
            var name = t.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0) name = name.Substring(0, tick);
            if (!doc.Contains(name, StringComparison.Ordinal)) missing.Add(t.FullName ?? name);
        }

        Assert.True(missing.Count == 0,
            "public type(s) that no document under docs/ names: " + string.Join(", ", missing)
            + ". Either document them (docs/architecture.md has a public surface index; the GenApi XML model lives in docs/genapi-model.md) or make them internal.");
    }

    // ---------------------------------------------------------------- 도구

    private static readonly string RepoRootValue = FindRepoRoot();

    private static string RepoRoot => RepoRootValue;

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GevSharp.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Relative(string path)
        => path.StartsWith(RepoRoot, StringComparison.Ordinal)
            ? path.Substring(RepoRoot.Length).TrimStart(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/')
            : path;

    /// <summary>
    /// git 명령을 저장소 뿌리에서 돌린다. 체크아웃이 아니거나 git 이 없으면 false — 그런 곳에서는 검사의 근거가 없다.
    /// 체크아웃인데 git 이 실패하면 그것은 감출 일이 아니므로 그대로 실패시킨다.
    /// </summary>
    private static bool TryGit(string arguments, out string output)
    {
        output = "";
        if (!Directory.Exists(Path.Combine(RepoRoot, ".git")) && !File.Exists(Path.Combine(RepoRoot, ".git"))) return false;

        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception) { return false; }          // git 이 PATH 에 없다
        Assert.NotNull(p);
        using (p!)
        {
            output = p.StandardOutput.ReadToEnd();
            var error = p.StandardError.ReadToEnd();
            p.WaitForExit(30_000);
            Assert.True(p.HasExited && p.ExitCode == 0, $"git {arguments} failed: {error}");
        }
        return true;
    }

    /// <summary>project.assets.json 의 targets 를 (TFM, 라이브러리 이름들) 로 읽는다.</summary>
    private static IEnumerable<(string Framework, IReadOnlyList<string> Libraries)> ReadAssetTargets(string path)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("targets", out var targets)) yield break;
        foreach (var framework in targets.EnumerateObject())
        {
            var libs = new List<string>();
            foreach (var lib in framework.Value.EnumerateObject())
            {
                var slash = lib.Name.IndexOf('/');
                libs.Add(slash < 0 ? lib.Name : lib.Name.Substring(0, slash));
            }
            yield return (framework.Name, libs);
        }
    }
}
