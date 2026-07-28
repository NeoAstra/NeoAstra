// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NeoAstra.Generator;

namespace NeoAstra.Tests;

[TestClass]
public sealed class RpcGeneratorTests
{
    [TestMethod]
    public void GeneratorIsDeterministicAndEmitsRegistrationManifestAndAotMetadataUse()
    {
        var first = Run(ValidSource);
        var second = Run(ValidSource);
        Assert.AreEqual(0, first.Diagnostics.Count(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, first.Diagnostics));
        Assert.AreEqual(first.Generated, second.Generated);
        StringAssert.Contains(first.Generated, "AddDocumentsService");
        StringAssert.Contains(first.Generated, "AppJsonContext.Default.GetTypeInfo");
        StringAssert.Contains(first.Generated, "documents.open");
        StringAssert.Contains(first.Generated, "documents.changed");
        StringAssert.Contains(first.Generated, "NeoRpcGeneratedContract");
        StringAssert.Contains(first.Generated, "NeoRpcGeneratedApplicationExtensions");
        StringAssert.Contains(first.Generated, "ConfigureGeneratedRpc");
        StringAssert.Contains(first.Generated, "new global::NeoAstra.Rpc.NeoPermissionDeclaration(\"test:invoke\"");
    }

    [TestMethod]
    public void GeneratorDiagnosesDuplicateCommandsAndMissingSerializerMetadata()
    {
        var duplicate = Run(ValidSource.Replace("[NeoRpcMethod(\"open\", Permission = \"test:invoke\")]", "[NeoRpcMethod(\"open\", Permission = \"test:invoke\")]\n    public ValueTask<Response> Again(Request request) => ValueTask.FromResult(new Response());\n    [NeoRpcMethod(\"open\", Permission = \"test:invoke\")]"));
        Assert.IsTrue(duplicate.Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC002"));
        var missing = Run(ValidSource.Replace("[assembly: NeoRpcJsonContext(typeof(AppJsonContext))]", string.Empty));
        Assert.IsTrue(missing.Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC009"));
        var missingRoot = Run(ValidSource.Replace("[JsonSerializable(typeof(Response))]", string.Empty));
        Assert.IsTrue(missingRoot.Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC009"));
    }

    [TestMethod]
    public void GeneratorRequiresBoundedPermissionsForEveryRendererCallableOperation()
    {
        var missingMethod = Run(ValidSource.Replace("[NeoRpcMethod(\"open\", Permission = \"test:invoke\")]", "[NeoRpcMethod(\"open\")]"));
        Assert.IsTrue(missingMethod.Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC015"));
        var missingEvent = Run(ValidSource.Replace("[NeoRpcEvent(\"documents.changed\", Permission = \"test:event\")]", "[NeoRpcEvent(\"documents.changed\")]"));
        Assert.IsTrue(missingEvent.Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC015"));
        var malformed = Run(ValidSource.Replace("Permission = \"test:invoke\"", "Permission = \"Test.Invoke\""));
        Assert.IsTrue(malformed.Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC015"));
        var emptySegment = Run(ValidSource.Replace("Permission = \"test:invoke\"", "Permission = \"test:\""));
        Assert.IsTrue(emptySegment.Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC015"));
        var longValidPermission = $"{new string('a', 100)}:{new string('b', 50)}";
        var longValid = Run(ValidSource.Replace("test:invoke", longValidPermission));
        Assert.IsFalse(longValid.Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC015"), string.Join(Environment.NewLine, longValid.Diagnostics));
        var tooLongPermission = $"{new string('a', 100)}:{new string('b', 92)}";
        var tooLong = Run(ValidSource.Replace("test:invoke", tooLongPermission));
        Assert.IsTrue(tooLong.Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC015"));
    }

    [TestMethod]
    public void GeneratorDiagnosesUnsafeContractsAndFrameworkPlacement()
    {
        var unsafeContract = Run(ValidSource.Replace("public string Id { get; set; } = \"\";", "public object Id { get; set; } = new();"));
        Assert.IsTrue(unsafeContract.Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC005"));
        var placement = Run(ValidSource.Replace("Request request, NeoRpcContext context, CancellationToken cancellationToken", "NeoRpcContext context, Request request, CancellationToken cancellationToken"));
        Assert.IsTrue(placement.Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC004"));
    }

    [TestMethod]
    public void GeneratorAcceptsExplicitClosedDiscriminatedUnions()
    {
        var source = ValidSource
            .Replace("public sealed class Response { public string Title { get; set; } = \"\"; }", "[NeoRpcUnion(\"kind\")] [JsonDerivedType(typeof(TextResponse), \"text\")] public abstract class Response { } public sealed class TextResponse : Response { public string Title { get; set; } = \"\"; }")
            .Replace("new Response()", "new TextResponse()")
            .Replace("ValueTask.FromResult(new TextResponse())", "ValueTask.FromResult<Response>(new TextResponse())")
            .Replace("public Response Changed { get; } = new();", "public Response Changed { get; } = new TextResponse();")
            .Replace("[JsonSerializable(typeof(Response))]", "[JsonSerializable(typeof(Response))] [JsonSerializable(typeof(TextResponse))]");
        var result = Run(source);
        Assert.AreEqual(0, result.Diagnostics.Count(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, result.Diagnostics));
        StringAssert.Contains(result.Generated, "Response");
    }

    [TestMethod]
    public void GeneratorAcceptsTheDocumentedPortableTypeSet()
    {
        var dto = """
public sealed class Request {
 public bool Flag { get; set; } public byte Byte { get; set; } public short Short { get; set; }
 public int Int { get; set; } public float Float { get; set; } public double Double { get; set; }
 public decimal Decimal { get; set; } public string Text { get; set; } = "";
 public System.Guid Id { get; set; } public System.DateTime Time { get; set; }
 public System.DateTimeOffset Offset { get; set; } public System.TimeSpan Duration { get; set; }
 public int? Optional { get; set; } public byte[] Bytes { get; set; } = [];
 public System.Collections.Generic.List<string> Items { get; set; } = [];
 public System.Collections.Generic.Dictionary<string, int> Values { get; set; } = [];
 public ContractKind Kind { get; set; }
 [NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] public long Large { get; set; }
}
public enum ContractKind { First, Second }
""";
        var result = Run(ValidSource.Replace("public sealed class Request { public string Id { get; set; } = \"\"; }", dto));
        Assert.AreEqual(0, result.Diagnostics.Count(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, result.Diagnostics));
    }

    [TestMethod]
    public void GeneratorCompilesEverySupportedRegistrationShape()
    {
        const string original = "public ValueTask<Response> OpenAsync(Request request, NeoRpcContext context, CancellationToken cancellationToken) => ValueTask.FromResult(new Response());";
        const string shapes = """
    public Response Sync(Request request) => new();
    [NeoRpcMethod("taskValue", Permission = "test:invoke")] public Task<Response> TaskValueAsync(Request request) => Task.FromResult(new Response());
    [NeoRpcMethod("valueTaskValue", Permission = "test:invoke")] public ValueTask<Response> ValueTaskValueAsync(Request request) => ValueTask.FromResult(new Response());
    [NeoRpcMethod("syncVoid", Permission = "test:invoke")] public void SyncVoid(Request request) { }
    [NeoRpcMethod("taskVoid", Permission = "test:invoke")] public Task TaskVoidAsync(Request request) => Task.CompletedTask;
    [NeoRpcMethod("valueTaskVoid", Permission = "test:invoke")] public ValueTask ValueTaskVoidAsync(Request request) => ValueTask.CompletedTask;
    [NeoRpcMethod("syncChannel", Permission = "test:invoke")] public NeoRpcChannel<Response> SyncChannel(Request request) => throw new System.NotImplementedException();
    [NeoRpcMethod("taskChannel", Permission = "test:invoke")] public Task<NeoRpcChannel<Response>> TaskChannelAsync(Request request) => throw new System.NotImplementedException();
    [NeoRpcMethod("valueTaskChannel", Permission = "test:invoke")] public ValueTask<NeoRpcChannel<Response>> ValueTaskChannelAsync(Request request) => throw new System.NotImplementedException();
""";
        var result = Run(ValidSource.Replace(original, shapes));
        Assert.AreEqual(0, result.Diagnostics.Count(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, result.Diagnostics));
    }

    [TestMethod]
    public void GeneratorDiagnosesSerializerPolicyAndTypeScriptSymbolCollisions()
    {
        var enumContract = ValidSource
            .Replace("public string Id { get; set; } = \"\";", "public CollisionKind Kind { get; set; } public string Id { get; set; } = \"\";")
            .Replace("public sealed class Response", "public enum CollisionKind { First, Second } public sealed class Response")
            .Replace(", UseStringEnumConverter = true", string.Empty);
        Assert.IsTrue(Run(enumContract).Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC011"));

        var collision = ValidSource
            .Replace("public sealed class Request {", "namespace First { public sealed class Collision { public string Value { get; set; } = \"\"; } } namespace Second { public sealed class Collision { public string Value { get; set; } = \"\"; } } public sealed class Request {")
            .Replace("[NeoRpcMethod(\"open\", Permission = \"test:invoke\")]", "[NeoRpcMethod(\"first\", Permission = \"test:invoke\")] public ValueTask<First.Collision> FirstAsync(Request request) => ValueTask.FromResult(new First.Collision()); [NeoRpcMethod(\"second\", Permission = \"test:invoke\")] public ValueTask<Second.Collision> SecondAsync(Request request) => ValueTask.FromResult(new Second.Collision()); [NeoRpcMethod(\"open\", Permission = \"test:invoke\")]")
            .Replace("[JsonSerializable(typeof(Response))]", "[JsonSerializable(typeof(Response))] [JsonSerializable(typeof(First.Collision))] [JsonSerializable(typeof(Second.Collision))]");
        Assert.IsTrue(Run(collision).Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC010"));

        var missingConverter = ValidSource.Replace("public string Id { get; set; } = \"\";", "[NeoRpcInt64(NeoRpcInt64Policy.String)] public long Id { get; set; }");
        Assert.IsTrue(Run(missingConverter).Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC005"));
    }

    [TestMethod]
    public void GeneratorDiagnosesConstructionOmissionAccessorAndGeneratedNameAmbiguities()
    {
        var privateConstructor = ValidSource.Replace("public sealed class Request { public string Id { get; set; } = \"\"; }", "public sealed class Request { public string Id { get; } private Request(string id) { Id = id; } }");
        Assert.IsTrue(Run(privateConstructor).Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC005"));

        var inaccessibleMember = ValidSource.Replace("public sealed class Request { public string Id { get; set; } = \"\"; }", "public sealed class Request { public Request() { } public required string Id { get; private set; } }");
        Assert.IsTrue(Run(inaccessibleMember).Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC005"));

        var ignoredAccessor = ValidSource.Replace("public sealed class Request { public string Id { get; set; } = \"\"; }", "public sealed class Request { public Request() { } public string Id { get; } = \"\"; }");
        Assert.IsTrue(Run(ignoredAccessor).Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC005"));

        var unboundConstructor = ValidSource.Replace("public sealed class Request { public string Id { get; set; } = \"\"; }", "public sealed class Request { public string Id { get; } public Request(string other) { Id = other; } }");
        Assert.IsTrue(Run(unboundConstructor).Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC005"));

        var wrongConstructorType = ValidSource.Replace("public sealed class Request { public string Id { get; set; } = \"\"; }", "public sealed class Request { public string Id { get; } public Request(int id) { Id = id.ToString(); } }");
        Assert.IsTrue(Run(wrongConstructorType).Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC005"));

        var indexer = ValidSource.Replace("public string Id { get; set; } = \"\";", "public string Id { get; set; } = \"\"; public string this[int index] => Id;");
        Assert.IsTrue(Run(indexer).Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC005"));

        var impossibleOmission = ValidSource.Replace("public string Id { get; set; } = \"\";", "[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int Id { get; set; }");
        Assert.IsTrue(Run(impossibleOmission).Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC005"));

        var hidden = ValidSource.Replace("public sealed class Request { public string Id { get; set; } = \"\"; }", "public class RequestBase { public string Id { get; set; } = \"\"; } public sealed class Request : RequestBase { public new string Id { get; set; } = \"\"; }");
        Assert.IsTrue(Run(hidden).Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC005"));

        var methodCollision = ValidSource.Replace("[NeoRpcMethod(\"open\", Permission = \"test:invoke\")]", "[NeoRpcMethod(\"load\", Permission = \"test:invoke\")] public Response Load(Request request) => new(); [NeoRpcMethod(\"loadAsync\", Permission = \"test:invoke\")] public Response LoadAsync(Request request) => new(); [NeoRpcMethod(\"open\", Permission = \"test:invoke\")]");
        Assert.IsTrue(Run(methodCollision).Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC014"));

        var eventMemberCollision = ValidSource.Replace("[NeoRpcMethod(\"open\", Permission = \"test:invoke\")]", "[NeoRpcMethod(\"onChanged\", Permission = \"test:invoke\")] public Response OnChanged(Request request) => new(); [NeoRpcMethod(\"open\", Permission = \"test:invoke\")]");
        Assert.IsTrue(Run(eventMemberCollision).Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC014"));

        var eventRegistrationCollision = ValidSource
            .Replace("public sealed class Events", "namespace Other { public sealed class Events { [NeoRpcEvent(\"other.changed\", Permission = \"test:event\")] public global::Response Changed { get; } = new(); } } public sealed class Events");
        Assert.IsTrue(Run(eventRegistrationCollision).Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC014"));
    }

    [TestMethod]
    public void GeneratorArtifactsMatchInheritedConditionalNullableAndNestedWireShapes()
    {
        var source = ValidSource.Replace(
            "public sealed class Request { public string Id { get; set; } = \"\"; }",
            "public class RequestBase { public string BaseName { get; set; } = \"\"; } public sealed class Request : RequestBase { public string? Optional { get; set; } [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Omitted { get; set; } public System.Collections.Generic.List<Response?> Nested { get; set; } = []; }");
        var result = Run(source, captureArtifacts: true);
        Assert.AreEqual(0, result.Diagnostics.Count(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, result.Diagnostics));
        StringAssert.Contains(result.TypeScript, "export const neoRpcContractHash = \"");
        StringAssert.Contains(result.TypeScript, "contractHash: neoRpcContractHash");
        StringAssert.Contains(result.TypeScript, "readonly \"baseName\": string;");
        StringAssert.Contains(result.TypeScript, "readonly \"optional\": string | null;");
        StringAssert.Contains(result.TypeScript, "readonly \"omitted\"?: string | null;");
        StringAssert.Contains(result.TypeScript, "ReadonlyArray<Response | null>");
        StringAssert.Contains(result.Schema, "\"omitted\"");
        Assert.IsFalse(result.Schema.Contains("\"required\":[\"baseName\",\"nested\",\"omitted\"", StringComparison.Ordinal));
        StringAssert.Contains(result.Schema, "\"items\":{\"anyOf\"");
    }

    [TestMethod]
    public void GeneratorAppliesConstructionAndAccessorsByContractDirection()
    {
        var readOnlyOutput = ValidSource.Replace("public sealed class Response { public string Title { get; set; } = \"\"; }", "public sealed class Response { public string Title { get; } = \"read-only\"; }");
        var outputResult = Run(readOnlyOutput, captureArtifacts: true);
        Assert.AreEqual(0, outputResult.Diagnostics.Count(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, outputResult.Diagnostics));
        StringAssert.Contains(outputResult.TypeScript, "readonly \"title\": string;");

        var readOnlyInput = ValidSource.Replace("public sealed class Request { public string Id { get; set; } = \"\"; }", "public sealed class Request { public string Id { get; } = \"read-only\"; }");
        Assert.IsTrue(Run(readOnlyInput).Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC005" && diagnostic.GetMessage().Contains("no public setter", StringComparison.Ordinal)));

        var writeOnlyOutput = ValidSource.Replace("public sealed class Response { public string Title { get; set; } = \"\"; }", "public sealed class Response { private string _title = \"\"; public string Title { set => _title = value; } }");
        Assert.IsTrue(Run(writeOnlyOutput).Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC005" && diagnostic.GetMessage().Contains("public getter", StringComparison.Ordinal)));

        var inheritedNestedInput = ValidSource.Replace(
            "public sealed class Request { public string Id { get; set; } = \"\"; }",
            "public sealed class NestedInput { public string Value { get; } = \"\"; } public class RequestBase { public NestedInput Nested { get; set; } = new(); } public sealed class Request : RequestBase { public string Id { get; set; } = \"\"; }");
        var first = Run(inheritedNestedInput);
        var second = Run(inheritedNestedInput);
        Assert.IsTrue(first.Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC005" && diagnostic.GetMessage().Contains("property 'Value' has no public setter", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(first.Diagnostics.Select(static diagnostic => diagnostic.ToString()).ToArray(), second.Diagnostics.Select(static diagnostic => diagnostic.ToString()).ToArray());

        var inheritedNestedOutput = ValidSource.Replace(
            "public sealed class Response { public string Title { get; set; } = \"\"; }",
            "public sealed class NestedOutput { public string Value { get; } = \"\"; } public class ResponseBase { public NestedOutput Nested { get; } = new(); } public sealed class Response : ResponseBase { public string Title { get; } = \"\"; }");
        var nestedOutputResult = Run(inheritedNestedOutput, captureArtifacts: true);
        Assert.AreEqual(0, nestedOutputResult.Diagnostics.Count(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, nestedOutputResult.Diagnostics));
        StringAssert.Contains(nestedOutputResult.TypeScript, "readonly \"nested\": NestedOutput;");

        var bothDirections = readOnlyOutput.Replace("[NeoRpcMethod(\"open\", Permission = \"test:invoke\")]", "[NeoRpcMethod(\"echo\", Permission = \"test:invoke\")] public Response Echo(Response request) => request; [NeoRpcMethod(\"open\", Permission = \"test:invoke\")]");
        Assert.IsTrue(Run(bothDirections).Diagnostics.Any(static diagnostic => diagnostic.Id == "NEORPC005" && diagnostic.GetMessage().Contains("no public setter", StringComparison.Ordinal)));
    }

    private static (IReadOnlyList<Diagnostic> Diagnostics, string Generated, string TypeScript, string Schema) Run(string source, bool captureArtifacts = false)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path)).Cast<MetadataReference>().ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(global::NeoAstra.Rpc.NeoRpcBuilder).Assembly.Location));
        var compilation = CSharpCompilation.Create("GeneratorFixture", [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        string? directory = null;
        AnalyzerConfigOptionsProvider? optionsProvider = null;
        if (captureArtifacts)
        {
            directory = Path.Combine(Path.GetTempPath(), "neoastra-generator-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            optionsProvider = new TestOptionsProvider(new Dictionary<string, string>
            {
                ["build_property.NeoRpcTypeScriptOutput"] = Path.Combine(directory, "contract.ts"),
                ["build_property.NeoRpcSchemaOutput"] = Path.Combine(directory, "schema.json"),
            });
        }
        GeneratorDriver driver = optionsProvider is null
            ? CSharpGeneratorDriver.Create(new NeoRpcGenerator())
            : CSharpGeneratorDriver.Create([new NeoRpcGenerator().AsSourceGenerator()], optionsProvider: optionsProvider);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics);
        var result = driver.GetRunResult();
        var returned = (result.Diagnostics.Concat(generatorDiagnostics).Concat(result.Results.SelectMany(static item => item.Diagnostics)).Concat(outputCompilation.GetDiagnostics()).Distinct().ToArray(),
            string.Join("\n", result.Results.SelectMany(static item => item.GeneratedSources).Select(static item => item.SourceText.ToString())),
            directory is null || !File.Exists(Path.Combine(directory, "contract.ts")) ? string.Empty : File.ReadAllText(Path.Combine(directory, "contract.ts")),
            directory is null || !File.Exists(Path.Combine(directory, "schema.json")) ? string.Empty : File.ReadAllText(Path.Combine(directory, "schema.json")));
        if (directory is not null) Directory.Delete(directory, recursive: true);
        return returned;
    }

    private sealed class TestOptionsProvider(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigOptions _global = new TestOptions(values);
        public override AnalyzerConfigOptions GlobalOptions => _global;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => TestOptions.Empty;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => TestOptions.Empty;
    }

    private sealed class TestOptions(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptions
    {
        internal static TestOptions Empty { get; } = new(new Dictionary<string, string>());
        public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
    }

    private const string ValidSource = """
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Serialization;
using NeoAstra.Rpc;
[assembly: NeoRpcJsonContext(typeof(AppJsonContext))]
[NeoRpcService("documents")]
public sealed class DocumentsService
{
    [NeoRpcMethod("open", Permission = "test:invoke")]
    public ValueTask<Response> OpenAsync(Request request, NeoRpcContext context, CancellationToken cancellationToken) => ValueTask.FromResult(new Response());
}
public sealed class Request { public string Id { get; set; } = ""; }
public sealed class Response { public string Title { get; set; } = ""; }
public sealed class Events { [NeoRpcEvent("documents.changed", Permission = "test:event")] public Response Changed { get; } = new(); }
[JsonSerializable(typeof(Request))]
[JsonSerializable(typeof(Response))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
public sealed class AppJsonContext : JsonSerializerContext {
    private static readonly System.Text.Json.JsonSerializerOptions ContextOptions = new() { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(), PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
    public static AppJsonContext Default { get; } = new(ContextOptions);
    public AppJsonContext(System.Text.Json.JsonSerializerOptions options) : base(options) { }
    protected override System.Text.Json.JsonSerializerOptions? GeneratedSerializerOptions => ContextOptions;
    public override System.Text.Json.Serialization.Metadata.JsonTypeInfo? GetTypeInfo(System.Type type) => ContextOptions.GetTypeInfo(type);
}
""";
}
