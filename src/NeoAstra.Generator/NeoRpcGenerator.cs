// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace NeoAstra.Generator;

/// <summary>Generates explicit RPC dispatch, serializer metadata, manifests, schemas, and TypeScript.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class NeoRpcGenerator : IIncrementalGenerator
{
    private const string ServiceAttribute = "NeoAstra.Rpc.NeoRpcServiceAttribute";
    private const string MethodAttribute = "NeoAstra.Rpc.NeoRpcMethodAttribute";
    private const string EventAttribute = "NeoAstra.Rpc.NeoRpcEventAttribute";
    private static readonly DiagnosticDescriptor InvalidName = Error("NEORPC001", "Invalid RPC wire name", "RPC wire name '{0}' is invalid; use an explicit bounded ASCII name");
    private static readonly DiagnosticDescriptor DuplicateCommand = Error("NEORPC002", "Duplicate RPC command", "RPC command '{0}' is declared more than once; wire names cannot be overloaded");
    private static readonly DiagnosticDescriptor InvalidSignature = Error("NEORPC003", "Unsupported RPC signature", "RPC method '{0}' has an unsupported signature: {1}");
    private static readonly DiagnosticDescriptor FrameworkParameter = Error("NEORPC004", "Invalid framework parameter", "RPC method '{0}' must place at most one NeoRpcContext and one CancellationToken after its single request DTO");
    private static readonly DiagnosticDescriptor UnsupportedContract = Error("NEORPC005", "Unsupported RPC contract type", "RPC contract type '{0}' is unsupported: {1}");
    private static readonly DiagnosticDescriptor DuplicateJsonName = Error("NEORPC006", "Duplicate JSON member name", "RPC DTO '{0}' contains duplicate JSON member name '{1}'");
    private static readonly DiagnosticDescriptor BaselineChanged = new("NEORPC007", "RPC contract differs from baseline", "The generated RPC contract differs from baseline '{0}'; retain an alias or version the wire contract", "NeoAstra.Rpc", DiagnosticSeverity.Warning, true);
    private static readonly DiagnosticDescriptor ArtifactFailure = Error("NEORPC008", "RPC artifact emission failed", "Could not write deterministic RPC artifact '{0}': {1}");
    private static readonly DiagnosticDescriptor MissingSerializer = Error("NEORPC009", "Missing RPC serializer metadata", "Add [assembly: NeoRpcJsonContext(typeof(MyJsonContext))] and [JsonSerializable] metadata for every RPC DTO");
    private static readonly DiagnosticDescriptor MissingSerializerRoot = Error("NEORPC009", "Missing RPC serializer metadata", "RPC root type '{0}' is missing [JsonSerializable] metadata on the declared RPC JSON context");
    private static readonly DiagnosticDescriptor TypeScriptCollision = Error("NEORPC010", "TypeScript symbol collision", "Generated TypeScript symbol '{0}' is produced by more than one RPC contract declaration; rename one declaration");
    private static readonly DiagnosticDescriptor SerializerPolicy = Error("NEORPC011", "Incompatible RPC serializer policy", "RPC enum contracts require [JsonSourceGenerationOptions(UseStringEnumConverter = true)] on the declared JSON context");
    private static readonly DiagnosticDescriptor NamingPolicy = Error("NEORPC012", "Incompatible RPC naming policy", "The RPC JSON context requires [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)] for generated TypeScript/schema parity");
    private static readonly DiagnosticDescriptor UnsupportedSerializerOptions = Error("NEORPC013", "Incompatible RPC serializer options", "The RPC JSON context cannot enable field inclusion or conditional global member omission because generated TypeScript/schema contracts are property-based and deterministic");
    private static readonly DiagnosticDescriptor GeneratedSymbolCollision = Error("NEORPC014", "Generated RPC symbol collision", "Generated {0} symbol '{1}' is ambiguous; rename one declaration or assign a distinct wire/member name");
    private static readonly DiagnosticDescriptor InvalidPermission = Error("NEORPC015", "Invalid RPC permission declaration", "Renderer-callable operation '{0}' declares a malformed Permission ID; omit it for a trusted application operation or use a bounded colon-separated ID");

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var services = context.SyntaxProvider.ForAttributeWithMetadataName(
            ServiceAttribute,
            static (node, _) => node is ClassDeclarationSyntax,
            static (syntax, _) => CreateService((INamedTypeSymbol)syntax.TargetSymbol, syntax.Attributes[0]));
        var events = context.SyntaxProvider.ForAttributeWithMetadataName(
            EventAttribute,
            static (node, _) => node is PropertyDeclarationSyntax or EventDeclarationSyntax or EventFieldDeclarationSyntax,
            static (syntax, _) => CreateEvent(syntax.TargetSymbol, syntax.Attributes[0]));
        var input = context.CompilationProvider.Combine(services.Collect()).Combine(events.Collect()).Combine(context.AnalyzerConfigOptionsProvider);
        context.RegisterSourceOutput(input, static (production, value) => Execute(production, value.Left.Left.Left, value.Left.Left.Right, value.Left.Right, value.Right));
    }

    private static ServiceModel CreateService(INamedTypeSymbol symbol, AttributeData attribute)
    {
        var name = attribute.ConstructorArguments.Length == 1 ? attribute.ConstructorArguments[0].Value as string ?? string.Empty : string.Empty;
        var version = 1;
        foreach (var argument in attribute.NamedArguments) if (argument.Key == "Version" && argument.Value.Value is int value) version = value;
        var methods = new List<MethodModel>();
        foreach (var method in symbol.GetMembers().OfType<IMethodSymbol>())
        {
            var methodAttribute = method.GetAttributes().FirstOrDefault(static candidate => candidate.AttributeClass != null && candidate.AttributeClass.ToDisplayString() == MethodAttribute);
            if (methodAttribute == null) continue;
            var methodName = methodAttribute.ConstructorArguments.Length == 1 ? methodAttribute.ConstructorArguments[0].Value as string ?? string.Empty : string.Empty;
            string? permission = null;
            var dispatch = 0;
            var timeout = 0;
            foreach (var argument in methodAttribute.NamedArguments)
            {
                if (argument.Key == "Permission") permission = argument.Value.Value as string;
                else if (argument.Key == "Dispatch" && argument.Value.Value is int dispatchValue) dispatch = dispatchValue;
                else if (argument.Key == "TimeoutMilliseconds" && argument.Value.Value is int timeoutValue) timeout = timeoutValue;
            }
            methods.Add(new MethodModel(method, methodName, permission, dispatch, timeout));
        }
        return new ServiceModel(symbol, name, version, methods);
    }

    private static EventModel CreateEvent(ISymbol symbol, AttributeData attribute)
    {
        var name = attribute.ConstructorArguments.Length == 1 ? attribute.ConstructorArguments[0].Value as string ?? string.Empty : string.Empty;
        string? permission = null;
        var overflow = 0;
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key == "Permission") permission = argument.Value.Value as string;
            else if (argument.Key == "OverflowBehavior" && argument.Value.Value is int value) overflow = value;
        }
        var payloadType = symbol is IPropertySymbol property ? property.Type : ((IEventSymbol)symbol).Type;
        if (symbol is IEventSymbol eventSymbol && eventSymbol.Type is INamedTypeSymbol delegateType && delegateType.DelegateInvokeMethod is { Parameters.Length: 1 } invoke)
            payloadType = invoke.Parameters[0].Type;
        return new EventModel(symbol, name, payloadType, permission, overflow);
    }

    private static void Execute(SourceProductionContext context, Compilation compilation, ImmutableArray<ServiceModel> serviceArray, ImmutableArray<EventModel> eventArray, AnalyzerConfigOptionsProvider options)
    {
        var services = serviceArray.OrderBy(static service => service.Name, StringComparer.Ordinal).ThenBy(static service => service.Symbol.ToDisplayString(), StringComparer.Ordinal).ToArray();
        var events = eventArray.OrderBy(static item => item.Name, StringComparer.Ordinal).ThenBy(static item => item.Symbol.ToDisplayString(), StringComparer.Ordinal).ToArray();
        if (services.Length == 0 && events.Length == 0)
            return;

        var contextAttribute = compilation.Assembly.GetAttributes().FirstOrDefault(static attribute => attribute.AttributeClass?.ToDisplayString() == "NeoAstra.Rpc.NeoRpcJsonContextAttribute");
        var serializerContext = contextAttribute?.ConstructorArguments.Length == 1 ? contextAttribute.ConstructorArguments[0].Value as INamedTypeSymbol : null;
        if ((services.Length != 0 || events.Length != 0) && serializerContext == null)
        {
            context.ReportDiagnostic(Diagnostic.Create(MissingSerializer, Location.None));
            return;
        }
        if (serializerContext is not null && !InheritsFrom(serializerContext, "System.Text.Json.Serialization.JsonSerializerContext"))
        {
            context.ReportDiagnostic(Diagnostic.Create(MissingSerializer, serializerContext.Locations.FirstOrDefault()));
            return;
        }
        var valid = new List<ServiceModel>();
        var commandNames = new HashSet<string>(StringComparer.Ordinal);
        var contractTypes = new Dictionary<ITypeSymbol, ContractDirection>(SymbolEqualityComparer.Default);
        var hasErrors = false;
        foreach (var service in services)
        {
            if (!IsWireName(service.Name) || service.Version < 1)
            {
                context.ReportDiagnostic(Diagnostic.Create(InvalidName, service.Symbol.Locations.FirstOrDefault(), service.Name));
                hasErrors = true;
                continue;
            }
            var validMethods = new List<MethodModel>();
            foreach (var method in service.Methods.OrderBy(static method => method.Name, StringComparer.Ordinal))
            {
                var command = service.Name + "." + method.Name;
                if (!IsWireName(method.Name)) { context.ReportDiagnostic(Diagnostic.Create(InvalidName, method.Symbol.Locations.FirstOrDefault(), method.Name)); hasErrors = true; continue; }
                if (method.Permission is not null && !IsPermission(method.Permission)) { context.ReportDiagnostic(Diagnostic.Create(InvalidPermission, method.Symbol.Locations.FirstOrDefault(), command)); hasErrors = true; continue; }
                if (!commandNames.Add(command)) { context.ReportDiagnostic(Diagnostic.Create(DuplicateCommand, method.Symbol.Locations.FirstOrDefault(), command)); hasErrors = true; continue; }
                if (!ValidateMethod(context, method, contractTypes)) { hasErrors = true; continue; }
                validMethods.Add(method);
            }
            valid.Add(new ServiceModel(service.Symbol, service.Name, service.Version, validMethods));
        }
        var validEvents = new List<EventModel>();
        foreach (var item in events)
        {
            if (!IsWireName(item.Name)) { context.ReportDiagnostic(Diagnostic.Create(InvalidName, item.Symbol.Locations.FirstOrDefault(), item.Name)); hasErrors = true; continue; }
            if (item.Permission is not null && !IsPermission(item.Permission)) { context.ReportDiagnostic(Diagnostic.Create(InvalidPermission, item.Symbol.Locations.FirstOrDefault(), item.Name)); hasErrors = true; continue; }
            if (!commandNames.Add(item.Name)) { context.ReportDiagnostic(Diagnostic.Create(DuplicateCommand, item.Symbol.Locations.FirstOrDefault(), item.Name)); hasErrors = true; continue; }
            if (item.Overflow is < 0 or > 3) { context.ReportDiagnostic(Diagnostic.Create(InvalidSignature, item.Symbol.Locations.FirstOrDefault(), item.Symbol.Name, "event overflow policy is invalid")); hasErrors = true; continue; }
            AddContractDirection(contractTypes, item.PayloadType, ContractDirection.Output);
            validEvents.Add(item);
        }

        if (hasErrors) return;
        var declaredRoots = new HashSet<ITypeSymbol>(serializerContext!.GetAttributes()
            .Where(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonSerializableAttribute" && attribute.ConstructorArguments.Length != 0)
            .Select(static attribute => attribute.ConstructorArguments[0].Value as ITypeSymbol)
            .Where(static type => type is not null).Cast<ITypeSymbol>(), SymbolEqualityComparer.Default);
        foreach (var root in contractTypes.Keys.OrderBy(static type => type.ToDisplayString(), StringComparer.Ordinal))
        {
            if (!declaredRoots.Contains(root)) { context.ReportDiagnostic(Diagnostic.Create(MissingSerializerRoot, serializerContext.Locations.FirstOrDefault(), root.ToDisplayString())); hasErrors = true; }
        }
        if (hasErrors) return;
        var orderedTypes = CollectContractTypes(context, contractTypes, ref hasErrors);
        if (hasErrors) return;
        if (!UsesCamelCase(serializerContext))
        {
            context.ReportDiagnostic(Diagnostic.Create(NamingPolicy, serializerContext.Locations.FirstOrDefault()));
            return;
        }
        if (HasUnsupportedSerializerOptions(serializerContext))
        {
            context.ReportDiagnostic(Diagnostic.Create(UnsupportedSerializerOptions, serializerContext.Locations.FirstOrDefault()));
            return;
        }
        if (orderedTypes.Any(static type => type.TypeKind == TypeKind.Enum) && !UsesStringEnums(serializerContext))
        {
            context.ReportDiagnostic(Diagnostic.Create(SerializerPolicy, serializerContext.Locations.FirstOrDefault()));
            return;
        }
        if (!ValidateTypeScriptSymbols(context, valid, validEvents, orderedTypes)) return;
        var manifest = EmitManifest(valid, validEvents, orderedTypes);
        var hash = Sha256(manifest);
        var source = EmitCSharp(valid, validEvents, serializerContext!, manifest, hash);
        context.AddSource("NeoRpcBindings.g.cs", SourceText.From(source, Encoding.UTF8));
        var typeScript = EmitTypeScript(valid, validEvents, orderedTypes, hash);
        var javaScriptImport = "@neoastra/client";
        if (options.GlobalOptions.TryGetValue("build_property.NeoRpcJavaScriptImport", out var configuredImport) && !string.IsNullOrWhiteSpace(configuredImport))
            javaScriptImport = configuredImport;
        var javaScript = EmitJavaScript(valid, validEvents, hash, javaScriptImport);
        var schema = EmitSchema(orderedTypes, hash);
        WriteArtifact(context, options, "NeoRpcTypeScriptOutput", typeScript);
        WriteArtifact(context, options, "NeoRpcJavaScriptOutput", javaScript);
        WriteArtifact(context, options, "NeoRpcManifestOutput", manifest);
        WriteArtifact(context, options, "NeoRpcSchemaOutput", schema);
        CheckBaseline(context, options, manifest);
    }

    private static bool ValidateMethod(SourceProductionContext context, MethodModel method, Dictionary<ITypeSymbol, ContractDirection> contractTypes)
    {
        var symbol = method.Symbol;
        if (symbol.DeclaredAccessibility != Accessibility.Public || symbol.IsStatic || symbol.IsGenericMethod || symbol.MethodKind != MethodKind.Ordinary)
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidSignature, symbol.Locations.FirstOrDefault(), symbol.Name, "methods must be public instance non-generic declarations"));
            return false;
        }
        IParameterSymbol? request = null;
        var seenContext = false;
        var seenCancellation = false;
        var frameworkStarted = false;
        foreach (var parameter in symbol.Parameters)
        {
            var type = parameter.Type.ToDisplayString();
            if (type == "NeoAstra.Rpc.NeoRpcContext") { if (seenContext) return FrameworkError(context, symbol); seenContext = true; frameworkStarted = true; }
            else if (type == "System.Threading.CancellationToken") { if (seenCancellation) return FrameworkError(context, symbol); seenCancellation = true; frameworkStarted = true; }
            else
            {
                if (frameworkStarted || request != null || parameter.RefKind != RefKind.None) return FrameworkError(context, symbol);
                request = parameter;
            }
        }
        if (request == null)
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidSignature, symbol.Locations.FirstOrDefault(), symbol.Name, "exactly one request DTO is required"));
            return false;
        }
        AddContractDirection(contractTypes, request.Type, ContractDirection.Input);
        if (!TryUnwrapReturn(symbol.ReturnType, out var result, out _))
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidSignature, symbol.Locations.FirstOrDefault(), symbol.Name, "return must be void, DTO, Task, Task<T>, ValueTask, ValueTask<T>, or NeoRpcChannel<T>"));
            return false;
        }
        if (result != null) AddContractDirection(contractTypes, result, ContractDirection.Output);
        if (method.TimeoutMilliseconds < 0 || method.TimeoutMilliseconds > 600000 || method.Dispatch is < 0 or > 1)
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidSignature, symbol.Locations.FirstOrDefault(), symbol.Name, "dispatch and timeout policy is invalid"));
            return false;
        }
        return true;
    }

    private static bool FrameworkError(SourceProductionContext context, IMethodSymbol method)
    {
        context.ReportDiagnostic(Diagnostic.Create(FrameworkParameter, method.Locations.FirstOrDefault(), method.Name));
        return false;
    }

    private static void AddContractDirection(Dictionary<ITypeSymbol, ContractDirection> contracts, ITypeSymbol type, ContractDirection direction)
    {
        contracts.TryGetValue(type, out var existing);
        contracts[type] = existing | direction;
    }

    private static List<ITypeSymbol> CollectContractTypes(SourceProductionContext context, Dictionary<ITypeSymbol, ContractDirection> roots, ref bool hasErrors)
    {
        var result = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var visiting = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var processed = new Dictionary<ITypeSymbol, ContractDirection>(SymbolEqualityComparer.Default);
        foreach (var root in roots.OrderBy(static item => item.Key.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), StringComparer.Ordinal))
            ValidateType(context, root.Key, null, root.Value, result, visiting, processed, ref hasErrors);
        return result.OrderBy(static type => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), StringComparer.Ordinal).ToList();
    }

    private static void ValidateType(SourceProductionContext context, ITypeSymbol type, ISymbol? owner, ContractDirection direction, HashSet<ITypeSymbol> result, HashSet<ITypeSymbol> visiting, Dictionary<ITypeSymbol, ContractDirection> processed, ref bool hasErrors)
    {
        if (type.NullableAnnotation == NullableAnnotation.Annotated && type is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            ValidateType(context, nullable.TypeArguments[0], owner, direction, result, visiting, processed, ref hasErrors); return;
        }
        if (type is IArrayTypeSymbol array) { ValidateType(context, array.ElementType, owner, direction, result, visiting, processed, ref hasErrors); result.Add(type); return; }
        if (IsPrimitive(type)) { result.Add(type); return; }
        if (type.SpecialType is SpecialType.System_Int64 or SpecialType.System_UInt64)
        {
            var policy = owner?.GetAttributes().FirstOrDefault(static attribute => attribute.AttributeClass?.ToDisplayString() == "NeoAstra.Rpc.NeoRpcInt64Attribute");
            var nullableProperty = owner is IPropertySymbol { Type: INamedTypeSymbol propertyType } && propertyType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T && SymbolEqualityComparer.Default.Equals(propertyType.TypeArguments[0], type);
            var expectedConverter = type.SpecialType == SpecialType.System_Int64
                ? nullableProperty ? "NeoAstra.Rpc.NeoRpcNullableInt64JsonConverter" : "NeoAstra.Rpc.NeoRpcInt64JsonConverter"
                : nullableProperty ? "NeoAstra.Rpc.NeoRpcNullableUInt64JsonConverter" : "NeoAstra.Rpc.NeoRpcUInt64JsonConverter";
            var hasConverter = owner?.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonConverterAttribute" && attribute.ConstructorArguments.Length == 1 && (attribute.ConstructorArguments[0].Value as ITypeSymbol)?.ToDisplayString() == expectedConverter) == true;
            var directProperty = nullableProperty || owner is IPropertySymbol property && SymbolEqualityComparer.Default.Equals(property.Type, type);
            if (!directProperty || policy == null || policy.ConstructorArguments.Length != 1 || policy.ConstructorArguments[0].Value is not 0 || !hasConverter)
            {
                context.ReportDiagnostic(Diagnostic.Create(UnsupportedContract, owner?.Locations.FirstOrDefault(), type.ToDisplayString(), $"64-bit integers require [NeoRpcInt64(NeoRpcInt64Policy.String)] and [JsonConverter(typeof({expectedConverter}))] decimal-string serialization")); hasErrors = true;
            }
            result.Add(type); return;
        }
        if (type.SpecialType is SpecialType.System_Object or SpecialType.System_IntPtr or SpecialType.System_UIntPtr || type.TypeKind is TypeKind.Pointer or TypeKind.Dynamic or TypeKind.TypeParameter || type.IsRefLikeType)
        {
            context.ReportDiagnostic(Diagnostic.Create(UnsupportedContract, owner?.Locations.FirstOrDefault(), type.ToDisplayString(), "object, pointer-width, ref-like, dynamic, and generic parameter types are forbidden")); hasErrors = true; return;
        }
        if (type is not INamedTypeSymbol named) { context.ReportDiagnostic(Diagnostic.Create(UnsupportedContract, owner?.Locations.FirstOrDefault(), type.ToDisplayString(), "type shape is not supported")); hasErrors = true; return; }
        if (named.TypeKind == TypeKind.Enum)
        {
            var enumNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in named.GetMembers().OfType<IFieldSymbol>().Where(static field => field.HasConstantValue))
            {
                var wireName = GetEnumWireName(field);
                if (!enumNames.Add(wireName)) { context.ReportDiagnostic(Diagnostic.Create(DuplicateJsonName, field.Locations.FirstOrDefault(), named.ToDisplayString(), wireName)); hasErrors = true; }
            }
            result.Add(type); return;
        }
        if (named.IsGenericType)
        {
            var original = named.ConstructedFrom.ToDisplayString();
            if (original is "System.Collections.Generic.List<T>" or "System.Collections.Generic.IReadOnlyList<T>" or "System.Collections.Generic.IEnumerable<T>")
            {
                ValidateType(context, named.TypeArguments[0], owner, direction, result, visiting, processed, ref hasErrors); result.Add(type); return;
            }
            if (original == "System.Collections.Generic.Dictionary<TKey, TValue>" || original == "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>")
            {
                if (named.TypeArguments[0].SpecialType != SpecialType.System_String) { context.ReportDiagnostic(Diagnostic.Create(UnsupportedContract, owner?.Locations.FirstOrDefault(), type.ToDisplayString(), "dictionary keys must be string")); hasErrors = true; return; }
                ValidateType(context, named.TypeArguments[1], owner, direction, result, visiting, processed, ref hasErrors); result.Add(type); return;
            }
            context.ReportDiagnostic(Diagnostic.Create(UnsupportedContract, owner?.Locations.FirstOrDefault(), type.ToDisplayString(), "generic collection is not in the supported set")); hasErrors = true; return;
        }
        var unionAttribute = named.GetAttributes().FirstOrDefault(static attribute => attribute.AttributeClass?.ToDisplayString() == "NeoAstra.Rpc.NeoRpcUnionAttribute");
        if (named.TypeKind is not (TypeKind.Class or TypeKind.Struct) && !(named.TypeKind == TypeKind.Interface && unionAttribute != null)) { context.ReportDiagnostic(Diagnostic.Create(UnsupportedContract, owner?.Locations.FirstOrDefault(), type.ToDisplayString(), "only explicit DTO classes, records, structs, and closed union interfaces are supported")); hasErrors = true; return; }
        if (visiting.Contains(type)) { context.ReportDiagnostic(Diagnostic.Create(UnsupportedContract, owner?.Locations.FirstOrDefault(), type.ToDisplayString(), "cyclic DTO graphs are forbidden")); hasErrors = true; return; }
        processed.TryGetValue(type, out var completedDirections);
        direction &= ~completedDirections;
        if (direction == ContractDirection.None) { result.Add(type); return; }
        processed[type] = completedDirections | direction;
        visiting.Add(type);
        result.Add(type);
        if (unionAttribute != null)
        {
            var cases = GetUnionCases(named);
            if (!(named.IsAbstract || named.TypeKind == TypeKind.Interface) || cases.Count == 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(UnsupportedContract, named.Locations.FirstOrDefault(), type.ToDisplayString(), "unions must be abstract and declare a closed set of [JsonDerivedType] cases"));
                hasErrors = true;
            }
            foreach (var unionCase in cases) ValidateType(context, unionCase.Type, named, direction, result, visiting, processed, ref hasErrors);
        }
        var jsonNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in GetContractProperties(named))
        {
            if (property.IsIndexer)
            {
                context.ReportDiagnostic(Diagnostic.Create(UnsupportedContract, property.Locations.FirstOrDefault(), property.Type.ToDisplayString(), "indexers are not serializable RPC properties"));
                hasErrors = true;
                continue;
            }
            if (property.GetAttributes().Any(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonExtensionDataAttribute"))
            {
                context.ReportDiagnostic(Diagnostic.Create(UnsupportedContract, property.Locations.FirstOrDefault(), property.Type.ToDisplayString(), "JSON extension-data members are not supported by the closed RPC schema"));
                hasErrors = true;
            }
            var ignoreCondition = property.GetAttributes().FirstOrDefault(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonIgnoreAttribute")?.NamedArguments.FirstOrDefault(static argument => argument.Key == "Condition").Value.Value;
            if (ignoreCondition is 3 && property.Type.IsValueType && !(property.Type is INamedTypeSymbol propertyNullable && propertyNullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T))
            {
                context.ReportDiagnostic(Diagnostic.Create(UnsupportedContract, property.Locations.FirstOrDefault(), property.Type.ToDisplayString(), "WhenWritingNull cannot be applied to a non-nullable value property"));
                hasErrors = true;
            }
            if ((property.IsRequired || property.GetAttributes().Any(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonRequiredAttribute")) && ignoreCondition is 2 or 3)
            {
                context.ReportDiagnostic(Diagnostic.Create(UnsupportedContract, property.Locations.FirstOrDefault(), property.Type.ToDisplayString(), "a required property cannot also be conditionally omitted"));
                hasErrors = true;
            }
            var jsonName = property.Name;
            var jsonAttribute = property.GetAttributes().FirstOrDefault(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonPropertyNameAttribute");
            if (jsonAttribute != null && jsonAttribute.ConstructorArguments.Length == 1) jsonName = jsonAttribute.ConstructorArguments[0].Value as string ?? jsonName;
            if (!jsonNames.Add(jsonName)) { context.ReportDiagnostic(Diagnostic.Create(DuplicateJsonName, property.Locations.FirstOrDefault(), named.ToDisplayString(), jsonName)); hasErrors = true; }
            if ((direction & ContractDirection.Output) != 0 && property.GetMethod?.DeclaredAccessibility != Accessibility.Public)
            {
                context.ReportDiagnostic(Diagnostic.Create(UnsupportedContract, property.Locations.FirstOrDefault(), property.Type.ToDisplayString(), $"output property '{property.Name}' requires a public getter"));
                hasErrors = true;
            }
            ValidateType(context, property.Type, property, direction, result, visiting, processed, ref hasErrors);
        }
        foreach (var field in named.GetMembers().OfType<IFieldSymbol>().Where(static field => !field.IsStatic && field.GetAttributes().Any(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonIncludeAttribute")))
        {
            context.ReportDiagnostic(Diagnostic.Create(UnsupportedContract, field.Locations.FirstOrDefault(), field.Type.ToDisplayString(), "[JsonInclude] fields are not supported; use a public property"));
            hasErrors = true;
        }
        ValidateDtoMembers(context, named, direction, completedDirections == ContractDirection.None, ref hasErrors);
        visiting.Remove(type);
    }

    private static bool TryUnwrapReturn(ITypeSymbol returnType, out ITypeSymbol? result, out bool channel)
    {
        result = null; channel = false;
        if (returnType.SpecialType == SpecialType.System_Void) return true;
        if (returnType is INamedTypeSymbol named && named.IsGenericType)
        {
            var original = named.ConstructedFrom.ToDisplayString();
            if (original is "System.Threading.Tasks.Task<TResult>" or "System.Threading.Tasks.ValueTask<TResult>")
            {
                result = named.TypeArguments[0];
                if (result is INamedTypeSymbol wrapped && wrapped.IsGenericType && wrapped.ConstructedFrom.ToDisplayString() == "NeoAstra.Rpc.NeoRpcChannel<T>") { result = wrapped.TypeArguments[0]; channel = true; }
                return true;
            }
            if (original == "NeoAstra.Rpc.NeoRpcChannel<T>") { result = named.TypeArguments[0]; channel = true; return true; }
        }
        if (returnType.ToDisplayString() is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask") return true;
        result = returnType; return returnType.TypeKind is not (TypeKind.Pointer or TypeKind.FunctionPointer) && !returnType.IsRefLikeType;
    }

    private static string EmitCSharp(IReadOnlyList<ServiceModel> services, IReadOnlyList<EventModel> events, INamedTypeSymbol serializerContext, string manifest, string hash)
    {
        var builder = Header();
        builder.AppendLine("#nullable enable");
        builder.AppendLine("internal static class NeoRpcGeneratedContract");
        builder.AppendLine("{");
        builder.Append("    internal const string Hash = \"").Append(Escape(hash)).AppendLine("\";");
        builder.Append("    internal const string Manifest = \"").Append(Escape(manifest)).AppendLine("\";");
        builder.AppendLine("}");
        EmitApplicationRegistration(builder, services, events, hash);
        foreach (var service in services)
        {
            var serviceType = TypeName(service.Symbol);
            var safe = SafeIdentifier(service.Symbol.Name);
            var registrationName = safe.EndsWith("Service", StringComparison.Ordinal) ? safe : safe + "Service";
            builder.Append("internal static class ").Append(safe).AppendLine("NeoRpcRegistrationExtensions");
            builder.AppendLine("{");
            builder.Append("    internal static global::NeoAstra.Rpc.NeoRpcBuilder Add").Append(registrationName).Append("(this global::NeoAstra.Rpc.NeoRpcBuilder builder, ").Append(serviceType).AppendLine(" service)");
            builder.AppendLine("    {");
            builder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(builder);");
            builder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(service);");
            foreach (var method in service.Methods) EmitRegistration(builder, service, method, "service", false, serializerContext);
            builder.AppendLine("        return builder;");
            builder.AppendLine("    }");
            builder.Append("    internal static global::NeoAstra.Rpc.NeoRpcBuilder Add").Append(registrationName).Append("(this global::NeoAstra.Rpc.NeoRpcBuilder builder, global::NeoAstra.Rpc.NeoRpcServiceFactory<").Append(serviceType).Append("> factory, global::NeoAstra.Rpc.NeoRpcServiceLifetime lifetime)").AppendLine();
            builder.AppendLine("    {");
            builder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(builder);");
            builder.Append("        var activator = new global::NeoAstra.Rpc.NeoRpcServiceActivator<").Append(serviceType).AppendLine(">(factory, lifetime);");
            builder.AppendLine("        builder.AddServiceActivator(activator);");
            foreach (var method in service.Methods) EmitRegistration(builder, service, method, "activator", true, serializerContext);
            builder.AppendLine("        return builder;");
            builder.AppendLine("    }");
            builder.AppendLine("}");
        }
        builder.AppendLine("internal static class NeoRpcGeneratedEventRegistrationExtensions");
        builder.AppendLine("{");
        foreach (var item in events)
        {
            var payloadType = TypeName(item.PayloadType);
            builder.Append("    internal static global::NeoAstra.Rpc.NeoRpcEvent<").Append(payloadType).Append("> Add").Append(SafeIdentifier(item.Symbol.ContainingType.Name)).Append(SafeIdentifier(item.Symbol.Name)).Append("Event(this global::NeoAstra.Rpc.NeoRpcBuilder builder)").AppendLine();
            builder.AppendLine("    {");
            builder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(builder);");
            builder.Append("        return builder.AddEvent<").Append(payloadType).Append(">(\"").Append(Escape(item.Name)).Append("\", (global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<").Append(payloadType).Append(">)").Append(TypeName(serializerContext)).Append(".Default.GetTypeInfo(typeof(").Append(payloadType).Append("))!, new global::NeoAstra.Rpc.NeoRpcEventOptions { OverflowBehavior = (global::NeoAstra.Rpc.NeoRpcOverflowBehavior)").Append(item.Overflow);
            if (item.Permission != null) builder.Append(", Permission = \"").Append(Escape(item.Permission)).Append('"');
            builder.AppendLine(" });");
            builder.AppendLine("    }");
        }
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void EmitApplicationRegistration(StringBuilder builder, IReadOnlyList<ServiceModel> services, IReadOnlyList<EventModel> events, string hash)
    {
        var permissions = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var service in services)
        {
            foreach (var method in service.Methods)
            {
                if (method.Permission == null) continue;
                if (!permissions.TryGetValue(method.Permission, out var commands)) permissions.Add(method.Permission, commands = new SortedSet<string>(StringComparer.Ordinal));
                commands.Add(service.Name + "." + method.Name);
            }
        }
        foreach (var item in events)
        {
            if (item.Permission == null) continue;
            if (!permissions.TryGetValue(item.Permission, out var commands)) permissions.Add(item.Permission, commands = new SortedSet<string>(StringComparer.Ordinal));
            commands.Add(item.Name);
        }

        builder.AppendLine("internal static class NeoRpcGeneratedApplicationExtensions");
        builder.AppendLine("{");
        builder.AppendLine("    internal static global::NeoAstra.NeoAppBuilder UseRpc(this global::NeoAstra.NeoAppBuilder app, global::System.Action<global::NeoAstra.Rpc.NeoRpcBuilder> configure)");
        builder.AppendLine("    {");
        builder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(app);");
        builder.Append("        return app.ConfigureGeneratedRpc(\"").Append(Escape(hash)).AppendLine("\", new global::NeoAstra.Rpc.NeoPermissionDeclaration[]");
        builder.AppendLine("        {");
        foreach (var permission in permissions)
        {
            builder.Append("            new global::NeoAstra.Rpc.NeoPermissionDeclaration(\"").Append(Escape(permission.Key)).Append("\", 1, new global::System.String[] { ");
            var first = true;
            foreach (var command in permission.Value)
            {
                if (!first) builder.Append(", ");
                first = false;
                builder.Append('"').Append(Escape(command)).Append('"');
            }
            builder.AppendLine(" }, global::NeoAstra.Rpc.NeoPermissionRisk.Low, global::NeoAstra.Rpc.NeoScopeFamily.None),");
        }
        builder.AppendLine("        }, configure);");
        builder.AppendLine("    }");
        builder.AppendLine("}");
    }

    private static void EmitRegistration(StringBuilder builder, ServiceModel service, MethodModel method, string target, bool activator, INamedTypeSymbol serializerContext)
    {
        var request = method.Symbol.Parameters.First(static parameter => parameter.Type.ToDisplayString() is not ("NeoAstra.Rpc.NeoRpcContext" or "System.Threading.CancellationToken"));
        TryUnwrapReturn(method.Symbol.ReturnType, out var result, out var channel);
        var requestType = TypeName(request.Type);
        var command = service.Name + "." + method.Name;
        builder.Append("        builder.").Append(channel ? "AddChannelCommand" : result == null ? "AddCommand" : "AddCommand").Append('<').Append(requestType);
        if (result != null) builder.Append(", ").Append(TypeName(result));
        builder.Append(">(\"").Append(Escape(command)).Append("\", ");
        builder.Append(activator ? BuildActivatorLambda(method, target, result != null) : BuildDirectLambda(method, target, result != null));
        var contextType = TypeName(serializerContext);
        builder.Append(", (global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<").Append(requestType).Append(">)").Append(contextType).Append(".Default.GetTypeInfo(typeof(").Append(requestType).Append("))!");
        if (result != null && !channel) builder.Append(", (global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<").Append(TypeName(result)).Append(">)").Append(contextType).Append(".Default.GetTypeInfo(typeof(").Append(TypeName(result)).Append("))!");
        builder.Append(", new global::NeoAstra.Rpc.NeoRpcCommandOptions { Dispatch = (global::NeoAstra.Rpc.NeoRpcDispatchMode)").Append(method.Dispatch);
        if (method.Permission != null) builder.Append(", Permission = \"").Append(Escape(method.Permission)).Append('"');
        if (method.TimeoutMilliseconds > 0) builder.Append(", Timeout = global::System.TimeSpan.FromMilliseconds(").Append(method.TimeoutMilliseconds).Append(')');
        builder.AppendLine(" });");
    }

    private static string BuildDirectLambda(MethodModel method, string target, bool hasResult)
    {
        var call = target + "." + method.Symbol.Name + "(" + BuildArguments(method) + ")";
        var returnType = method.Symbol.ReturnType.ToDisplayString();
        if (!hasResult)
        {
            if (returnType == "void") return "(request, context, cancellationToken) => { " + call + "; return global::System.Threading.Tasks.ValueTask.CompletedTask; }";
            if (returnType == "System.Threading.Tasks.Task") return "(request, context, cancellationToken) => new global::System.Threading.Tasks.ValueTask(" + call + ")";
            return "(request, context, cancellationToken) => " + call;
        }
        if (returnType.StartsWith("System.Threading.Tasks.Task<", StringComparison.Ordinal)) return "(request, context, cancellationToken) => new global::System.Threading.Tasks.ValueTask<" + TypeName(UnwrapTask(method.Symbol.ReturnType)) + ">(" + call + ")";
        if (returnType.StartsWith("System.Threading.Tasks.ValueTask<", StringComparison.Ordinal)) return "(request, context, cancellationToken) => " + call;
        return "(request, context, cancellationToken) => global::System.Threading.Tasks.ValueTask.FromResult(" + call + ")";
    }

    private static string BuildActivatorLambda(MethodModel method, string target, bool hasResult)
    {
        var serviceCall = "service." + method.Symbol.Name + "(" + BuildArguments(method) + ")";
        var returnType = method.Symbol.ReturnType.ToDisplayString();
        string inner;
        if (!hasResult)
        {
            if (returnType == "void") inner = "service => { " + serviceCall + "; return global::System.Threading.Tasks.ValueTask.CompletedTask; }";
            else if (returnType == "System.Threading.Tasks.Task") inner = "service => new global::System.Threading.Tasks.ValueTask(" + serviceCall + ")";
            else inner = "service => " + serviceCall;
            return "(request, context, cancellationToken) => " + target + ".InvokeAsync(context, " + inner + ")";
        }
        if (returnType.StartsWith("System.Threading.Tasks.Task<", StringComparison.Ordinal)) inner = "service => new global::System.Threading.Tasks.ValueTask<" + TypeName(UnwrapTask(method.Symbol.ReturnType)) + ">(" + serviceCall + ")";
        else if (returnType.StartsWith("System.Threading.Tasks.ValueTask<", StringComparison.Ordinal)) inner = "service => " + serviceCall;
        else inner = "service => global::System.Threading.Tasks.ValueTask.FromResult(" + serviceCall + ")";
        return "(request, context, cancellationToken) => " + target + ".InvokeAsync(context, " + inner + ")";
    }

    private static string BuildArguments(MethodModel method) => string.Join(", ", method.Symbol.Parameters.Select(static parameter => parameter.Type.ToDisplayString() == "NeoAstra.Rpc.NeoRpcContext" ? "context" : parameter.Type.ToDisplayString() == "System.Threading.CancellationToken" ? "cancellationToken" : "request"));
    private static ITypeSymbol UnwrapTask(ITypeSymbol type) => ((INamedTypeSymbol)type).TypeArguments[0];

    private static string EmitManifest(IReadOnlyList<ServiceModel> services, IReadOnlyList<EventModel> events, IReadOnlyList<ITypeSymbol> types)
    {
        var builder = new StringBuilder();
        builder.Append("{\"format\":1,\"services\":[");
        for (var i = 0; i < services.Count; i++)
        {
            if (i != 0) builder.Append(',');
            var service = services[i];
            builder.Append("{\"name\":\"").Append(JsonEscape(service.Name)).Append("\",\"version\":").Append(service.Version).Append(",\"commands\":[");
            for (var j = 0; j < service.Methods.Count; j++)
            {
                if (j != 0) builder.Append(',');
                var method = service.Methods[j];
                TryUnwrapReturn(method.Symbol.ReturnType, out var result, out var channel);
                var request = method.Symbol.Parameters.First(static parameter => parameter.Type.ToDisplayString() is not ("NeoAstra.Rpc.NeoRpcContext" or "System.Threading.CancellationToken"));
                builder.Append("{\"name\":\"").Append(JsonEscape(method.Name)).Append("\",\"request\":\"").Append(JsonEscape(request.Type.ToDisplayString())).Append("\",\"response\":");
                if (result == null) builder.Append("null"); else builder.Append('"').Append(JsonEscape(result.ToDisplayString())).Append('"');
                builder.Append(",\"channel\":").Append(channel ? "true" : "false").Append(",\"permission\":");
                if (method.Permission == null) builder.Append("null"); else builder.Append('"').Append(JsonEscape(method.Permission)).Append('"');
                builder.Append('}');
            }
            builder.Append("]}");
        }
        builder.Append("],\"events\":[");
        for (var i = 0; i < events.Count; i++)
        {
            if (i != 0) builder.Append(',');
            var item = events[i];
            builder.Append("{\"name\":\"").Append(JsonEscape(item.Name)).Append("\",\"payload\":\"").Append(JsonEscape(item.PayloadType.ToDisplayString())).Append("\",\"permission\":");
            if (item.Permission == null) builder.Append("null"); else builder.Append('"').Append(JsonEscape(item.Permission)).Append('"');
            builder.Append(",\"overflow\":").Append(item.Overflow).Append('}');
        }
        builder.Append("],\"types\":[");
        for (var i = 0; i < types.Count; i++) { if (i != 0) builder.Append(','); builder.Append('"').Append(JsonEscape(types[i].ToDisplayString())).Append('"'); }
        builder.Append("]}\n");
        return builder.ToString();
    }

    private static string EmitTypeScript(IReadOnlyList<ServiceModel> services, IReadOnlyList<EventModel> events, IReadOnlyList<ITypeSymbol> types, string hash)
    {
        var builder = new StringBuilder();
        builder.Append("// <auto-generated by NeoAstra.Generator; contract ").Append(hash).AppendLine(">");
        builder.AppendLine("import { invoke, invokeChannel, subscribe, type NeoRpcCallOptions, type NeoRpcUnsubscribe } from \"@neoastra/client\";");
        builder.Append("export const neoRpcContractHash = \"").Append(hash).AppendLine("\";");
        foreach (var type in types.OfType<INamedTypeSymbol>().Where(static type => !IsPrimitive(type) && !type.IsGenericType && !IsUnion(type) && type.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Enum).GroupBy(static type => type.ToDisplayString(), StringComparer.Ordinal).Select(static group => group.First()))
        {
            if (type.TypeKind == TypeKind.Enum)
            {
                builder.Append("export type ").Append(type.Name).Append(" = ").Append(string.Join(" | ", type.GetMembers().OfType<IFieldSymbol>().Where(static field => field.HasConstantValue).Select(static field => "\"" + Escape(GetEnumWireName(field)) + "\""))).AppendLine(";");
                continue;
            }
            builder.Append("export interface ").Append(type.Name).AppendLine(" {");
            foreach (var property in GetContractProperties(type).OrderBy(static property => property.Name, StringComparer.Ordinal))
                builder.Append("  readonly \"").Append(Escape(GetJsonName(property))).Append('"').Append(IsOptionalProperty(property) ? "?: " : ": ").Append(TypeScriptType(property.Type)).AppendLine(";");
            builder.AppendLine("}");
        }
        foreach (var union in types.OfType<INamedTypeSymbol>().Where(static type => IsUnion(type)).OrderBy(static type => type.Name, StringComparer.Ordinal))
        {
            var attribute = union.GetAttributes().First(static item => item.AttributeClass?.ToDisplayString() == "NeoAstra.Rpc.NeoRpcUnionAttribute");
            var discriminator = attribute.ConstructorArguments[0].Value as string ?? "kind";
            builder.Append("export type ").Append(union.Name).Append(" = ");
            var cases = GetUnionCases(union);
            for (var i = 0; i < cases.Count; i++)
            {
                if (i != 0) builder.Append(" | ");
                builder.Append('(').Append(cases[i].Type.Name).Append(" & { readonly \"").Append(Escape(discriminator)).Append("\": ").Append(cases[i].Discriminator).Append(" })");
            }
            builder.AppendLine(";");
        }
        foreach (var service in services)
        {
            builder.Append("export const ").Append(Camel(service.Symbol.Name.Replace("Service", string.Empty))).AppendLine(" = Object.freeze({");
            foreach (var method in service.Methods)
            {
                var request = method.Symbol.Parameters.First(static parameter => parameter.Type.ToDisplayString() is not ("NeoAstra.Rpc.NeoRpcContext" or "System.Threading.CancellationToken"));
                TryUnwrapReturn(method.Symbol.ReturnType, out var result, out var channel);
                var response = result == null ? "void" : channel ? "AsyncIterable<" + TypeScriptType(result) + ">" : TypeScriptType(result);
                var invokeFunction = channel ? "invokeChannel" : "invoke";
                var invokeResultType = channel ? TypeScriptType(result!) : response;
                builder.Append("  ").Append(Camel(method.Symbol.Name.Replace("Async", string.Empty))).Append(": (request: ").Append(TypeScriptType(request.Type)).Append(", options?: NeoRpcCallOptions): Promise<").Append(response).Append("> => ").Append(invokeFunction).Append('<').Append(TypeScriptType(request.Type)).Append(", ").Append(invokeResultType).Append(">(")
                    .Append('"').Append(service.Name).Append('.').Append(method.Name).Append("\", request, { ...options, contractHash: neoRpcContractHash }),").AppendLine();
            }
            foreach (var item in events.Where(item => item.Name.StartsWith(service.Name + ".", StringComparison.Ordinal)))
            {
                var suffix = item.Name.Substring(service.Name.Length + 1);
                builder.Append("  on").Append(PascalWireIdentifier(suffix)).Append(": (handler: (value: ").Append(TypeScriptType(item.PayloadType)).Append(") => void, options?: NeoRpcCallOptions): Promise<NeoRpcUnsubscribe> => subscribe<").Append(TypeScriptType(item.PayloadType)).Append(">(\"").Append(Escape(item.Name)).AppendLine("\", handler, { ...options, contractHash: neoRpcContractHash }),");
            }
            builder.AppendLine("});");
        }
        foreach (var item in events.Where(item => !services.Any(service => item.Name.StartsWith(service.Name + ".", StringComparison.Ordinal))))
        {
            builder.Append("export const subscribe").Append(SafeIdentifier(item.Symbol.ContainingType.Name)).Append(SafeIdentifier(item.Symbol.Name)).Append(" = (handler: (value: ").Append(TypeScriptType(item.PayloadType)).Append(") => void, options?: NeoRpcCallOptions): Promise<NeoRpcUnsubscribe> => subscribe<").Append(TypeScriptType(item.PayloadType)).Append(">(\"").Append(Escape(item.Name)).AppendLine("\", handler, { ...options, contractHash: neoRpcContractHash });");
        }
        return builder.ToString();
    }

    private static string EmitJavaScript(IReadOnlyList<ServiceModel> services, IReadOnlyList<EventModel> events, string hash, string clientImport)
    {
        var builder = new StringBuilder();
        builder.Append("// <auto-generated by NeoAstra.Generator; contract ").Append(hash).AppendLine(">");
        builder.Append("import { invoke, invokeChannel, subscribe } from \"").Append(Escape(clientImport)).AppendLine("\";");
        builder.Append("export const neoRpcContractHash = \"").Append(hash).AppendLine("\";");
        foreach (var service in services)
        {
            builder.Append("export const ").Append(Camel(service.Symbol.Name.Replace("Service", string.Empty))).AppendLine(" = Object.freeze({");
            foreach (var method in service.Methods)
            {
                TryUnwrapReturn(method.Symbol.ReturnType, out _, out var channel);
                var invokeFunction = channel ? "invokeChannel" : "invoke";
                builder.Append("  ").Append(Camel(method.Symbol.Name.Replace("Async", string.Empty))).Append(": (request, options) => ").Append(invokeFunction).Append('(')
                    .Append('"').Append(service.Name).Append('.').Append(method.Name).Append("\", request, { ...options, contractHash: neoRpcContractHash }),").AppendLine();
            }
            foreach (var item in events.Where(item => item.Name.StartsWith(service.Name + ".", StringComparison.Ordinal)))
            {
                var suffix = item.Name.Substring(service.Name.Length + 1);
                builder.Append("  on").Append(PascalWireIdentifier(suffix)).Append(": (handler, options) => subscribe(\"").Append(Escape(item.Name)).AppendLine("\", handler, { ...options, contractHash: neoRpcContractHash }),");
            }
            builder.AppendLine("});");
        }
        foreach (var item in events.Where(item => !services.Any(service => item.Name.StartsWith(service.Name + ".", StringComparison.Ordinal))))
        {
            builder.Append("export const subscribe").Append(SafeIdentifier(item.Symbol.ContainingType.Name)).Append(SafeIdentifier(item.Symbol.Name)).Append(" = (handler, options) => subscribe(\"").Append(Escape(item.Name)).AppendLine("\", handler, { ...options, contractHash: neoRpcContractHash });");
        }
        return builder.ToString();
    }

    private static string EmitSchema(IReadOnlyList<ITypeSymbol> types, string hash)
    {
        var builder = new StringBuilder();
        builder.Append("{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"$id\":\"urn:neoastra:rpc:").Append(hash).Append("\",\"$defs\":{");
        var dtos = types.OfType<INamedTypeSymbol>().Where(static type => !IsPrimitive(type) && !type.IsGenericType && !IsUnion(type) && type.TypeKind is TypeKind.Class or TypeKind.Struct).OrderBy(static type => type.Name, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < dtos.Length; i++)
        {
            if (i != 0) builder.Append(',');
            var dto = dtos[i];
            builder.Append('"').Append(JsonEscape(dto.Name)).Append("\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{");
            var properties = GetContractProperties(dto).OrderBy(static property => property.Name, StringComparer.Ordinal).ToArray();
            for (var j = 0; j < properties.Length; j++) { if (j != 0) builder.Append(','); builder.Append('"').Append(JsonEscape(GetJsonName(properties[j]))).Append("\":").Append(JsonSchemaType(properties[j].Type)); }
            builder.Append("},\"required\":[");
            var required = properties.Where(static property => !IsOptionalProperty(property)).ToArray();
            for (var j = 0; j < required.Length; j++) { if (j != 0) builder.Append(','); builder.Append('"').Append(JsonEscape(GetJsonName(required[j]))).Append('"'); }
            builder.Append("]}");
        }
        var unions = types.OfType<INamedTypeSymbol>().Where(static type => IsUnion(type)).OrderBy(static type => type.Name, StringComparer.Ordinal).ToArray();
        for (var unionIndex = 0; unionIndex < unions.Length; unionIndex++)
        {
            var union = unions[unionIndex];
            if (dtos.Length != 0 || unionIndex != 0) builder.Append(',');
            builder.Append('"').Append(JsonEscape(union.Name)).Append("\":{\"oneOf\":[");
            var cases = GetUnionCases(union);
            for (var i = 0; i < cases.Count; i++) { if (i != 0) builder.Append(','); builder.Append("{\"$ref\":\"#/$defs/").Append(JsonEscape(cases[i].Type.Name)).Append("\"}"); }
            builder.Append("]}");
        }
        builder.Append("}}\n");
        return builder.ToString();
    }

    private static string TypeScriptType(ITypeSymbol type)
    {
        var nullable = type.NullableAnnotation == NullableAnnotation.Annotated;
        if (type is INamedTypeSymbol namedNullable && namedNullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) return TypeScriptType(namedNullable.TypeArguments[0]) + " | null";
        string result;
        if (type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte }) result = "string";
        else if (type is IArrayTypeSymbol array) result = "ReadonlyArray<" + TypeScriptType(array.ElementType) + ">";
        else if (type.SpecialType == SpecialType.System_String) result = "string";
        else if (type.SpecialType == SpecialType.System_Boolean) result = "boolean";
        else if (type.SpecialType is >= SpecialType.System_SByte and <= SpecialType.System_Decimal) result = type.SpecialType is SpecialType.System_Int64 or SpecialType.System_UInt64 ? "string" : "number";
        else if (type is INamedTypeSymbol named && named.IsGenericType && named.ConstructedFrom.ToDisplayString().Contains("Dictionary", StringComparison.Ordinal)) result = "Readonly<Record<string, " + TypeScriptType(named.TypeArguments[1]) + ">>";
        else if (type is INamedTypeSymbol list && list.IsGenericType) result = "ReadonlyArray<" + TypeScriptType(list.TypeArguments[0]) + ">";
        else if (type.ToDisplayString() is "System.Guid" or "System.DateTime" or "System.DateTimeOffset" or "System.TimeSpan") result = "string";
        else result = type.Name;
        return nullable && type.IsReferenceType ? result + " | null" : result;
    }

    private static string JsonSchemaType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            return "{\"anyOf\":[" + JsonSchemaType(nullable.TypeArguments[0]) + ",{\"type\":\"null\"}]}";
        var schema = JsonSchemaNonNull(type);
        return type.NullableAnnotation == NullableAnnotation.Annotated && type.IsReferenceType
            ? "{\"anyOf\":[" + schema + ",{\"type\":\"null\"}]}"
            : schema;
    }

    private static string JsonSchemaNonNull(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte }) return "{\"type\":\"string\",\"contentEncoding\":\"base64\"}";
        if (type is IArrayTypeSymbol array) return "{\"type\":\"array\",\"items\":" + JsonSchemaType(array.ElementType) + "}";
        if (type is INamedTypeSymbol named && named.IsGenericType)
        {
            if (named.ConstructedFrom.ToDisplayString().Contains("Dictionary", StringComparison.Ordinal)) return "{\"type\":\"object\",\"additionalProperties\":" + JsonSchemaType(named.TypeArguments[1]) + "}";
            return "{\"type\":\"array\",\"items\":" + JsonSchemaType(named.TypeArguments[0]) + "}";
        }
        if (type.TypeKind == TypeKind.Enum) return "{\"type\":\"string\",\"enum\":[" + string.Join(",", type.GetMembers().OfType<IFieldSymbol>().Where(static field => field.HasConstantValue).Select(static field => "\"" + JsonEscape(GetEnumWireName(field)) + "\"")) + "]}";
        if (type.SpecialType == SpecialType.System_String) return "{\"type\":\"string\"}";
        if (type.SpecialType == SpecialType.System_Boolean) return "{\"type\":\"boolean\"}";
        if (type.SpecialType == SpecialType.System_Int64) return "{\"type\":\"string\",\"pattern\":\"^-?(0|[1-9][0-9]*)$\"}";
        if (type.SpecialType == SpecialType.System_UInt64) return "{\"type\":\"string\",\"pattern\":\"^(0|[1-9][0-9]*)$\"}";
        if (type.SpecialType is SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32) return "{\"type\":\"integer\"}";
        if (type.SpecialType is SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal) return "{\"type\":\"number\"}";
        if (type.ToDisplayString() == "System.Guid") return "{\"type\":\"string\",\"format\":\"uuid\"}";
        if (type.ToDisplayString() is "System.DateTime" or "System.DateTimeOffset") return "{\"type\":\"string\",\"format\":\"date-time\"}";
        if (type.ToDisplayString() == "System.TimeSpan") return "{\"type\":\"string\"}";
        return "{\"$ref\":\"#/$defs/" + JsonEscape(type.Name) + "\"}";
    }

    private static bool UsesStringEnums(INamedTypeSymbol serializerContext) => serializerContext.GetAttributes()
        .Where(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonSourceGenerationOptionsAttribute")
        .SelectMany(static attribute => attribute.NamedArguments)
        .Any(static argument => argument.Key == "UseStringEnumConverter" && argument.Value.Value is true);

    private static bool InheritsFrom(INamedTypeSymbol type, string baseType)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            if (current.ToDisplayString() == baseType) return true;
        return false;
    }

    private static bool UsesCamelCase(INamedTypeSymbol serializerContext) => serializerContext.GetAttributes()
        .Where(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonSourceGenerationOptionsAttribute")
        .SelectMany(static attribute => attribute.NamedArguments)
        .Any(static argument => argument.Key == "PropertyNamingPolicy" && argument.Value.Value is 1);

    private static bool HasUnsupportedSerializerOptions(INamedTypeSymbol serializerContext) => serializerContext.GetAttributes()
        .Where(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonSourceGenerationOptionsAttribute")
        .SelectMany(static attribute => attribute.NamedArguments)
        .Any(static argument => argument.Key is "IncludeFields" or "IgnoreReadOnlyFields" or "IgnoreReadOnlyProperties" && argument.Value.Value is true || argument.Key == "DefaultIgnoreCondition" && argument.Value.Value is not 0);

    private static IEnumerable<IPropertySymbol> GetContractProperties(INamedTypeSymbol type)
    {
        var properties = new Dictionary<string, IPropertySymbol>(StringComparer.Ordinal);
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>().Where(static property => !property.IsStatic && property.DeclaredAccessibility == Accessibility.Public && !IsAlwaysIgnored(property)))
                if (!properties.ContainsKey(property.Name)) properties.Add(property.Name, property);
        }
        return properties.Values;
    }

    private static void ValidateDtoMembers(SourceProductionContext context, INamedTypeSymbol type, ContractDirection direction, bool validateShape, ref bool hasErrors)
    {
        var allProperties = new List<IPropertySymbol>();
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            allProperties.AddRange(current.GetMembers().OfType<IPropertySymbol>().Where(static property => !property.IsStatic && !IsAlwaysIgnored(property) &&
                (property.DeclaredAccessibility == Accessibility.Public || property.GetAttributes().Any(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonIncludeAttribute"))));
        foreach (var hidden in allProperties.GroupBy(static property => property.Name, StringComparer.Ordinal).Where(_ => validateShape && _.Skip(1).Any()))
        {
            var declarations = hidden.ToArray();
            if (declarations.Select(GetOverrideRoot).Distinct(SymbolEqualityComparer.Default).Skip(1).Any())
            {
                context.ReportDiagnostic(Diagnostic.Create(UnsupportedContract, declarations[0].Locations.FirstOrDefault(), type.ToDisplayString(), $"hidden inherited property '{hidden.Key}' is ambiguous to System.Text.Json"));
                hasErrors = true;
            }
        }
        foreach (var property in allProperties.Where(property => validateShape && property.GetAttributes().Any(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonIncludeAttribute") && (property.DeclaredAccessibility != Accessibility.Public || property.GetMethod?.DeclaredAccessibility != Accessibility.Public)))
        {
            context.ReportDiagnostic(Diagnostic.Create(UnsupportedContract, property.Locations.FirstOrDefault(), property.Type.ToDisplayString(), "non-public [JsonInclude] accessors are not supported by generated RPC contracts"));
            hasErrors = true;
        }
        if ((direction & ContractDirection.Input) == 0 || type.IsAbstract || type.TypeKind == TypeKind.Interface) return;
        var constructors = type.InstanceConstructors.Where(static constructor => !constructor.IsStatic).ToArray();
        var attributed = constructors.Where(static constructor => constructor.GetAttributes().Any(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonConstructorAttribute")).ToArray();
        if (attributed.Any(static constructor => constructor.DeclaredAccessibility != Accessibility.Public) || attributed.Length > 1)
        {
            context.ReportDiagnostic(Diagnostic.Create(UnsupportedContract, type.Locations.FirstOrDefault(), type.ToDisplayString(), "exactly one public [JsonConstructor] is required"));
            hasErrors = true;
            return;
        }
        var publicConstructors = constructors.Where(static constructor => constructor.DeclaredAccessibility == Accessibility.Public).ToArray();
        var selected = attributed.FirstOrDefault() ?? publicConstructors.FirstOrDefault(static constructor => constructor.Parameters.Length == 0) ?? (publicConstructors.Length == 1 ? publicConstructors[0] : null);
        if (type.TypeKind == TypeKind.Class && selected is null)
        {
            context.ReportDiagnostic(Diagnostic.Create(UnsupportedContract, type.Locations.FirstOrDefault(), type.ToDisplayString(), "a public parameterless constructor, a single public parameterized constructor, or a public [JsonConstructor] is required"));
            hasErrors = true;
            return;
        }
        var serializableProperties = GetContractProperties(type).ToArray();
        var constructorParameters = new HashSet<string>(selected?.Parameters.Select(static parameter => parameter.Name) ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (selected is not null)
        {
            foreach (var parameter in selected.Parameters)
            {
                var matchingProperty = serializableProperties.FirstOrDefault(property => string.Equals(property.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
                if (matchingProperty is null || !SymbolEqualityComparer.Default.Equals(matchingProperty.Type, parameter.Type))
                {
                    context.ReportDiagnostic(Diagnostic.Create(UnsupportedContract, parameter.Locations.FirstOrDefault(), parameter.Type.ToDisplayString(), $"JSON constructor parameter '{parameter.Name}' must match a serializable property name and type"));
                    hasErrors = true;
                }
            }
        }
        foreach (var property in serializableProperties)
        {
            var publicSetter = property.SetMethod?.DeclaredAccessibility == Accessibility.Public;
            if (!publicSetter && !constructorParameters.Contains(property.Name))
            {
                context.ReportDiagnostic(Diagnostic.Create(UnsupportedContract, property.Locations.FirstOrDefault(), property.Type.ToDisplayString(), $"property '{property.Name}' has no public setter and is not bound by the selected JSON constructor"));
                hasErrors = true;
            }
        }
    }

    private static IPropertySymbol GetOverrideRoot(IPropertySymbol property)
    {
        while (property.OverriddenProperty is { } overridden) property = overridden;
        return property;
    }

    private static bool IsOptionalProperty(IPropertySymbol property) => property.GetAttributes().Any(static attribute =>
        attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonIgnoreAttribute" &&
        attribute.NamedArguments.Any(static argument => argument.Key == "Condition" && argument.Value.Value is 2 or 3));

    private static bool IsAlwaysIgnored(IPropertySymbol property) => property.GetAttributes().Any(static attribute =>
        attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonIgnoreAttribute" &&
        (attribute.NamedArguments.Length == 0 || attribute.NamedArguments.Any(static argument => argument.Key == "Condition" && argument.Value.Value is 1)));

    private static bool ValidateTypeScriptSymbols(SourceProductionContext context, List<ServiceModel> services, List<EventModel> events, IReadOnlyList<ITypeSymbol> types)
    {
        var symbols = new List<(string Name, ISymbol Symbol)>();
        symbols.AddRange(types.OfType<INamedTypeSymbol>().Where(static type => type.SpecialType == SpecialType.None && !type.IsGenericType).Select(static type => (type.Name, (ISymbol)type)));
        symbols.AddRange(services.Select(static service => (Camel(service.Symbol.Name.Replace("Service", string.Empty)), (ISymbol)service.Symbol)));
        symbols.AddRange(events.Where(item => !services.Any(service => item.Name.StartsWith(service.Name + ".", StringComparison.Ordinal))).Select(static item => ("subscribe" + SafeIdentifier(item.Symbol.ContainingType.Name) + SafeIdentifier(item.Symbol.Name), (ISymbol)item.Symbol)));
        var valid = true;
        foreach (var collision in symbols.GroupBy(static item => item.Name, StringComparer.Ordinal).Where(static group => group.Select(item => item.Symbol.ToDisplayString()).Distinct(StringComparer.Ordinal).Skip(1).Any()))
        {
            context.ReportDiagnostic(Diagnostic.Create(TypeScriptCollision, collision.First().Symbol.Locations.FirstOrDefault(), collision.Key));
            valid = false;
        }
        foreach (var service in services)
        {
            var members = service.Methods.Select(static method => (Name: Camel(method.Symbol.Name.Replace("Async", string.Empty)), Symbol: (ISymbol)method.Symbol))
                .Concat(events.Where(item => item.Name.StartsWith(service.Name + ".", StringComparison.Ordinal)).Select(item => ("on" + PascalWireIdentifier(item.Name.Substring(service.Name.Length + 1)), (ISymbol)item.Symbol)));
            foreach (var collision in members.GroupBy(static item => item.Item1, StringComparer.Ordinal).Where(static group => group.Skip(1).Any()))
            {
                context.ReportDiagnostic(Diagnostic.Create(GeneratedSymbolCollision, collision.First().Item2.Locations.FirstOrDefault(), "TypeScript service member", collision.Key));
                valid = false;
            }
        }
        foreach (var collision in events.GroupBy(static item => "Add" + SafeIdentifier(item.Symbol.ContainingType.Name) + SafeIdentifier(item.Symbol.Name) + "Event", StringComparer.Ordinal).Where(static group => group.Skip(1).Any()))
        {
            context.ReportDiagnostic(Diagnostic.Create(GeneratedSymbolCollision, collision.First().Symbol.Locations.FirstOrDefault(), "C# event registration method", collision.Key));
            valid = false;
        }
        return valid;
    }

    private static void WriteArtifact(SourceProductionContext context, AnalyzerConfigOptionsProvider options, string property, string content)
    {
        if (!options.GlobalOptions.TryGetValue("build_property." + property, out var path) || string.IsNullOrWhiteSpace(path)) return;
        try
        {
            content = Normalize(content);
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            if (File.Exists(fullPath) && File.ReadAllText(fullPath, Encoding.UTF8) == content) return;
            var temporary = fullPath + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            if (File.Exists(fullPath)) File.Delete(fullPath);
            File.Move(temporary, fullPath);
        }
        catch (Exception exception) { context.ReportDiagnostic(Diagnostic.Create(ArtifactFailure, Location.None, path, exception.Message)); }
    }

    private static void CheckBaseline(SourceProductionContext context, AnalyzerConfigOptionsProvider options, string manifest)
    {
        if (!options.GlobalOptions.TryGetValue("build_property.NeoRpcBaselineManifest", out var path) || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try { if (Normalize(File.ReadAllText(path, Encoding.UTF8)) != Normalize(manifest)) context.ReportDiagnostic(Diagnostic.Create(BaselineChanged, Location.None, path)); }
        catch (Exception exception) { context.ReportDiagnostic(Diagnostic.Create(ArtifactFailure, Location.None, path, exception.Message)); }
    }

    private static bool IsPrimitive(ITypeSymbol type) => type.SpecialType is SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal or SpecialType.System_String || type.ToDisplayString() is "System.Guid" or "System.DateTime" or "System.DateTimeOffset" or "System.TimeSpan";
    private static bool IsUnion(INamedTypeSymbol type) => type.GetAttributes().Any(static attribute => attribute.AttributeClass?.ToDisplayString() == "NeoAstra.Rpc.NeoRpcUnionAttribute");
    private static List<UnionCaseModel> GetUnionCases(INamedTypeSymbol type)
    {
        var result = new List<UnionCaseModel>();
        foreach (var attribute in type.GetAttributes().Where(static item => item.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonDerivedTypeAttribute"))
        {
            if (attribute.ConstructorArguments.Length < 2 || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol derived) continue;
            var value = attribute.ConstructorArguments[1].Value;
            var discriminator = value is string text ? "\"" + Escape(text) + "\"" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "0";
            result.Add(new UnionCaseModel(derived, discriminator));
        }
        return result.OrderBy(static item => item.Discriminator, StringComparer.Ordinal).ToList();
    }
    private static string GetJsonName(IPropertySymbol property)
    {
        var attribute = property.GetAttributes().FirstOrDefault(static candidate => candidate.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonPropertyNameAttribute");
        return attribute?.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is string name ? name : Camel(property.Name);
    }
    private static string GetEnumWireName(IFieldSymbol field)
    {
        var attribute = field.GetAttributes().FirstOrDefault(static item => item.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonStringEnumMemberNameAttribute");
        return attribute?.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is string name ? name : field.Name;
    }
    private static bool IsWireName(string value) => !string.IsNullOrEmpty(value) && value.Length <= 128 && value[0] is not ('.' or '-' or ':') && value.All(static character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.' or ':');
    private static string TypeName(ITypeSymbol symbol) => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    private static string SafeIdentifier(string value) => value.Replace("`", string.Empty);
    private static string Camel(string value) => string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value.Substring(1);
    private static string Pascal(string value) => string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value.Substring(1);
    private static string PascalWireIdentifier(string value) => string.Concat(value.Split(new[] { '.', ':', '-', '_' }, StringSplitOptions.RemoveEmptyEntries).Select(Pascal));
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    private static string JsonEscape(string value) => Escape(value);
    private static string Normalize(string value) => value.Replace("\r\n", "\n").Replace("\r", "\n");
    private static string Sha256(string value) { using (var algorithm = SHA256.Create()) return string.Concat(algorithm.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(static value => value.ToString("x2"))); }
    private static StringBuilder Header() => new StringBuilder("// <auto-generated/>\n// NeoAstra.Generator deterministic output\n");
    private static DiagnosticDescriptor Error(string id, string title, string message) => new(id, title, message, "NeoAstra.Rpc", DiagnosticSeverity.Error, true);

    private static bool IsPermission(string? value)
    {
        if (value == null || value.Length > 192) return false;
        var segments = value.Split(':');
        return segments.Length >= 2 && segments.All(IsWireName);
    }

    [Flags]
    private enum ContractDirection
    {
        None = 0,
        Input = 1,
        Output = 2,
    }

    private sealed class ServiceModel
    {
        internal ServiceModel(INamedTypeSymbol symbol, string name, int version, IReadOnlyList<MethodModel> methods) { Symbol = symbol; Name = name; Version = version; Methods = methods; }
        internal INamedTypeSymbol Symbol { get; }
        internal string Name { get; }
        internal int Version { get; }
        internal IReadOnlyList<MethodModel> Methods { get; }
    }

    private sealed class EventModel
    {
        internal EventModel(ISymbol symbol, string name, ITypeSymbol payloadType, string? permission, int overflow)
        {
            Symbol = symbol; Name = name; PayloadType = payloadType; Permission = permission; Overflow = overflow;
        }
        internal ISymbol Symbol { get; }
        internal string Name { get; }
        internal ITypeSymbol PayloadType { get; }
        internal string? Permission { get; }
        internal int Overflow { get; }
    }

    private sealed class UnionCaseModel
    {
        internal UnionCaseModel(INamedTypeSymbol type, string discriminator) { Type = type; Discriminator = discriminator; }
        internal INamedTypeSymbol Type { get; }
        internal string Discriminator { get; }
    }

    private sealed class MethodModel
    {
        internal MethodModel(IMethodSymbol symbol, string name, string? permission, int dispatch, int timeoutMilliseconds) { Symbol = symbol; Name = name; Permission = permission; Dispatch = dispatch; TimeoutMilliseconds = timeoutMilliseconds; }
        internal IMethodSymbol Symbol { get; }
        internal string Name { get; }
        internal string? Permission { get; }
        internal int Dispatch { get; }
        internal int TimeoutMilliseconds { get; }
    }
}
