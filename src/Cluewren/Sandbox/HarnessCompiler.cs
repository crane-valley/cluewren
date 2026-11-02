using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Cluewren.Kit;

namespace Cluewren.Sandbox;

public sealed record CompileProblem(string File, int Line, string Message)
{
    public override string ToString() => $"{File}({Line}): {Message}";
}

public sealed record Compiled(string? Assembly, string? Type, string? Method, IReadOnlyList<CompileProblem> Problems)
{
    public bool Ok => Problems.Count == 0;

    public string ProblemText => string.Join("\n", Problems);
}

// Runs no model code: no analyzers, no generators, and the output is only ever loaded by the child.
public sealed class HarnessCompiler(string runFolder)
{
    const string GlobalUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Threading;
        global using System.Threading.Tasks;
        global using Cluewren.Kit;
        global using static Cluewren.Kit.Control;
        global using static Cluewren.Kit.Geom;
        global using static Cluewren.Kit.Research;
        """;

    static readonly string[] ScriptUsings =
    [
        "System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Threading", "System.Threading.Tasks", "Cluewren.Kit",
        "Cluewren.Kit.Control", "Cluewren.Kit.Geom", "Cluewren.Kit.Research",
    ];

    static readonly Lazy<IReadOnlyList<MetadataReference>> Framework = new(LoadFramework);

    // A script's globals type is named by its runtime identity, System.Private.CoreLib included, which reference
    // assemblies don't hold.
    static readonly Lazy<IReadOnlyList<MetadataReference>> Implementation = new(LoadImplementation);

    static readonly CSharpParseOptions Source = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);

    readonly ConcurrentDictionary<string, Compiled> _done = new();

    string Bin => Path.Combine(runFolder, "bin");

    static IReadOnlyList<MetadataReference> LoadFramework()
    {
        var runtime = RuntimeEnvironment.GetRuntimeDirectory();
        var version = new DirectoryInfo(runtime.TrimEnd(Path.DirectorySeparatorChar)).Name;
        var root = Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
        var pack = Path.Combine(root, "packs", "Microsoft.NETCore.App.Ref");
        if (Directory.Exists(pack))
        {
            var major = version.Split('.')[0] + ".";
            var chosen = Directory.GetDirectories(pack).Select(Path.GetFileName).Where(name => name!.StartsWith(major, StringComparison.Ordinal))
                .OrderByDescending(name => Version.TryParse(name, out var v) ? v : new Version()).FirstOrDefault();
            if (chosen is not null)
            {
                var files = Directory.GetFiles(Path.Combine(pack, chosen, "ref", $"net{major}0"), "*.dll");
                if (files.Length > 0)
                    return files.Select(file => (MetadataReference)MetadataReference.CreateFromFile(file)).ToList();
            }
        }
        return Implementation.Value;
    }

    static IReadOnlyList<MetadataReference> LoadImplementation()
    {
        var folder = Path.GetFullPath(RuntimeEnvironment.GetRuntimeDirectory());
        return ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(file => string.Equals(Path.GetFullPath(Path.GetDirectoryName(file)! + Path.DirectorySeparatorChar), folder, StringComparison.OrdinalIgnoreCase))
            .Select(file => (MetadataReference)MetadataReference.CreateFromFile(file)).ToList();
    }

    static List<MetadataReference> References(string? lib, IReadOnlyList<MetadataReference>? framework = null)
    {
        var references = new List<MetadataReference>(framework ?? Framework.Value)
        {
            MetadataReference.CreateFromFile(typeof(Game).Assembly.Location),
        };
        if (lib is not null)
            references.Add(MetadataReference.CreateFromFile(lib));
        return references;
    }

    static string Hash(params string?[] parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in parts)
            hash.AppendData(Encoding.UTF8.GetBytes((part ?? "") + "\0"));
        return Convert.ToHexStringLower(hash.GetHashAndReset())[..16];
    }

    static List<CompileProblem> Problems(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d =>
        {
            var span = d.Location.GetLineSpan();
            return new CompileProblem(Path.GetFileName(span.Path), span.StartLinePosition.Line + 1, $"{d.Id}: {d.GetMessage(CultureInfo.InvariantCulture)}");
        }).ToList();

    static SyntaxTree Parse(string text, string path) =>
        CSharpSyntaxTree.ParseText(SourceText.From(text, Encoding.UTF8), Source, path);

    static CSharpCompilationOptions Options => new(OutputKind.DynamicallyLinkedLibrary,
        optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Disable);

    IReadOnlyList<CompileProblem>? Emit(Compilation compilation, string path)
    {
        Directory.CreateDirectory(Bin);
        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded));
        if (!result.Success)
            return Problems(result.Diagnostics);
        File.WriteAllBytes(path, peStream.ToArray());
        return null;
    }

    // A process's first compile loads Roslyn and the references: done while the model reads, not while it waits on a load.
    public static void Warm()
    {
        var harness = Parse("sealed class Warm : Harness { public override List<Option> Options(Obs obs) => []; }", "warm.cs");
        var compilation = CSharpCompilation.Create("warm", [Parse(GlobalUsings, "usings.cs"), harness], References(null), Options);
        LastHarness(compilation, harness);
        compilation.Emit(Stream.Null);
    }

    // lib.cs is its own assembly, so probes can use it without the harness having to compile.
    public Compiled CompileLib(string? libPath)
    {
        if (libPath is null || !File.Exists(libPath))
            return new Compiled(null, null, null, []);
        var text = File.ReadAllText(libPath);
        var name = "lib-" + Hash(text);
        return _done.GetOrAdd(name, _ =>
        {
            var compilation = CSharpCompilation.Create(name, [Parse(GlobalUsings, "usings.cs"), Parse(text, "lib.cs")],
                References(null), Options);
            var path = Path.Combine(Bin, name + ".dll");
            return Emit(compilation, path) is { } problems ? new Compiled(null, null, null, problems) : new Compiled(path, null, null, []);
        });
    }

    public Compiled CompileHarness(string harnessPath, string? libPath)
    {
        if (!File.Exists(harnessPath))
            return new Compiled(null, null, null, [new CompileProblem("harness.cs", 0, "doesn't exist yet: write it first.")]);
        var lib = CompileLib(libPath);
        if (!lib.Ok)
            return lib;
        var text = File.ReadAllText(harnessPath);
        var name = "harness-" + Hash(text, lib.Assembly);
        return _done.GetOrAdd(name, _ =>
        {
            var tree = Parse(text, "harness.cs");
            var compilation = CSharpCompilation.Create(name, [Parse(GlobalUsings, "usings.cs"), tree], References(lib.Assembly), Options);
            var type = LastHarness(compilation, tree);
            var path = Path.Combine(Bin, name + ".dll");
            if (Emit(compilation, path) is { } problems)
                return new Compiled(null, null, null, problems);
            return type is null
                ? new Compiled(null, null, null, [new CompileProblem("harness.cs", 0, "defines no Harness subclass")])
                : new Compiled(path, type, null, []);
        });
    }

    static string? LastHarness(CSharpCompilation compilation, SyntaxTree tree)
    {
        var model = compilation.GetSemanticModel(tree);
        var harness = compilation.GetTypeByMetadataName(typeof(Harness).FullName!);
        string? last = null;
        foreach (var declared in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (declared.Parent is TypeDeclarationSyntax || model.GetDeclaredSymbol(declared) is not { IsAbstract: false } symbol)
                continue;
            for (var @base = symbol.BaseType; @base is not null; @base = @base.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(@base, harness))
                {
                    last = MetadataName(symbol);
                    break;
                }
            }
        }
        return last;
    }

    static string MetadataName(ISymbol symbol) =>
        symbol.ContainingNamespace is { IsGlobalNamespace: false } space
            ? space.ToDisplayString() + "." + symbol.MetadataName : symbol.MetadataName;

    public Compiled CompileProbe(string code, string? libPath)
    {
        var lib = CompileLib(libPath);
        if (!lib.Ok)
            return lib;
        var tree = CSharpSyntaxTree.ParseText(SourceText.From(code, Encoding.UTF8), Source.WithKind(SourceCodeKind.Script), "probe.cs");
        var name = "probe-" + Guid.NewGuid().ToString("N");
        var compilation = CSharpCompilation.CreateScriptCompilation(name, tree, References(lib.Assembly, Implementation.Value),
            Options.WithUsings(ScriptUsings), returnType: typeof(object), globalsType: typeof(ProbeGlobals));
        var entry = compilation.GetEntryPoint(default);
        var path = Path.Combine(Bin, name + ".dll");
        if (Emit(compilation, path) is { } problems)
            return new Compiled(null, null, null, problems);
        if (entry is null)
            return new Compiled(null, null, null, [new CompileProblem("probe.cs", 0, "no entry point")]);
        return new Compiled(path, MetadataName(entry.ContainingType), entry.MetadataName, []);
    }
}
