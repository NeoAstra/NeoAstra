// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NeoAstra.Desktop.Menus;
using NeoAstra.Desktop.Tray;

namespace NeoAstra.Tests;

/// <summary>Runs the production presenters with only their native imports replaced, without a desktop session.</summary>
internal sealed class TrayPresenterFixture : IAsyncDisposable
{
    private static readonly Lazy<Assembly> Presenters = new(CompilePresenters);
    private readonly Type _native;
    private readonly MethodInfo _receive;
    private readonly bool _mac;

    internal static Assembly PresenterAssembly => Presenters.Value;

    internal TrayPresenterFixture(bool mac)
    {
        _mac = mac;
        var assembly = Presenters.Value;
        _native = assembly.GetType("NeoAstra.Desktop.Tray.TrayTestNative", throwOnError: true)!;
        _native.GetMethod("Reset")!.Invoke(null, null);
        var type = assembly.GetType($"NeoAstra.Desktop.Tray.{(mac ? "Mac" : "Windows")}TrayPresenter", throwOnError: true)!;
        Commands = new NeoCommandService();
        Presenter = (INeoTrayPresenter)Activator.CreateInstance(type, Commands, new NeoDispatcher(null!, Environment.CurrentManagedThreadId))!;
        Service = new NeoTrayService(presenter: Presenter);
        _receive = type.GetMethod(mac ? "StatusActivated" : "Receive", BindingFlags.Instance | BindingFlags.NonPublic)!;
    }

    internal NeoCommandService Commands { get; }
    internal INeoTrayPresenter Presenter { get; }
    internal NeoTrayService Service { get; }
    internal int PopupCount => (int)Get("PopupCount");
    internal List<string> Violations => (List<string>)Get("Violations");
    internal int LiveResources => ((Dictionary<nint, int>)Get("References")).Count(static pair => pair.Value > 0);
    internal Action? DuringPopup { set => _native.GetField("DuringPopup")!.SetValue(null, value); }

    internal void Activate(bool secondary = true)
    {
        _native.GetField("Secondary")!.SetValue(null, secondary);
        _receive.Invoke(Presenter, _mac ? [Get("Button")] : [Get("NativeId"), secondary ? 0x0205u : 0x0202u]);
    }

    internal void ActivateMenu(nint sender)
        => Presenter.GetType().GetMethod("MenuActivated", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Presenter, [sender]);

    internal nint MenuItem => (nint)Get("MenuItem");

    public async ValueTask DisposeAsync()
    {
        await Service.DisposeAsync();
        await Commands.DisposeAsync();
    }

    private object Get(string name) => _native.GetField(name)!.GetValue(null)!;

    private static Assembly CompilePresenters()
    {
        var trees = new List<SyntaxTree>
        {
            CSharpSyntaxTree.ParseText("global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; global using System.Threading; global using System.Threading.Tasks;"),
            CSharpSyntaxTree.ParseText(NativeSource),
        };
        foreach (var file in new[] { "MacTray", "WindowsTray", "WindowsMenus" })
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "NeoAstra", "Desktop", file + ".cs"))) directory = directory.Parent;
            Assert.IsNotNull(directory, "The native lifetime regression needs the presenter source checkout.");
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(directory.FullName, "src", "NeoAstra", "Desktop", file + ".cs"))).GetRoot();
            if (file == "WindowsMenus")
            {
                // HWND discovery belongs to NeoWindow/native core, not the menu lifetime under test.
                var resolve = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(static node => node.Identifier.ValueText == "ResolveTarget");
                root = root.ReplaceNode(resolve, resolve.WithBody((BlockSyntax)SyntaxFactory.ParseStatement("{ return new(id, TargetKind.Context, global::NeoAstra.Desktop.Tray.TrayTestNative.ContextWindow); }")));
            }
            var native = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(static node => node.Identifier.ValueText == "Native");
            var replacement = native.WithMembers(SyntaxFactory.List<MemberDeclarationSyntax>(native.Members.OfType<MethodDeclarationSyntax>().Select(method =>
                method.WithAttributeLists(default)
                    .WithModifiers(SyntaxFactory.TokenList(method.Modifiers.Where(static token => !token.IsKind(SyntaxKind.PartialKeyword))))
                    .WithExpressionBody(null).WithSemicolonToken(default)
                    .WithBody((BlockSyntax)SyntaxFactory.ParseStatement(NativeBody(file.StartsWith("Mac", StringComparison.Ordinal) ? "Mac" : "Windows", method.Identifier.ValueText))))));
            // The shared recording boundary lives beside the tray presenters.
            replacement = replacement.ReplaceNodes(replacement.DescendantNodes().OfType<IdentifierNameSyntax>().Where(static node => node.Identifier.ValueText == "TrayTestNative"),
                static (_, _) => SyntaxFactory.ParseName("global::NeoAstra.Desktop.Tray.TrayTestNative"));
            trees.Add(CSharpSyntaxTree.Create((CSharpSyntaxNode)root.ReplaceNode(native, replacement)));
        }

        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(NeoTrayService).Assembly.Location).Append(typeof(NeoDispatcher).Assembly.Location).Distinct();
        // Keep the existing friend-assembly identity: the production presenters bind an internal desktop interface.
        var compilation = CSharpCompilation.Create("NeoAstra.Tests", trees, paths.Select(static path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.IsTrue(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
        return Assembly.Load(stream.ToArray());
    }

    private static string NativeBody(string platform, string method) => (platform, method) switch
    {
        ("Mac", "GetClass") => "{ return TrayTestNative.Name(name); }",
        ("Mac", "GetSelector") => "{ return TrayTestNative.Name(name); }",
        ("Mac", "objc_lookUpClass") => "{ return 1; }",
        ("Mac", "Send") => "{ return TrayTestNative.Send(target, selector); }",
        ("Mac", "SendDouble") => "{ return TrayTestNative.Allocate(0); }",
        ("Mac", "Send3") => "{ TrayTestNative.MenuItem = target; return target; }",
        ("Mac", "SendLong") => "{ return TrayTestNative.Secondary ? 1 : 0; }",
        ("Mac", "SendUtf8") => "{ return TrayTestNative.Allocate(0); }",
        ("Mac", "SendVoid") => "{ TrayTestNative.SendVoid(target, selector); }",
        ("Mac", "SendVoidArg") => "{ TrayTestNative.SendVoidArg(target, selector, value); }",
        ("Mac", "SendVoidBool" or "SendVoidLong" or "SendVoidULong") => "{ }",
        ("Windows", "CreateWindowEx" or "CreatePopupMenu") => "{ return TrayTestNative.Allocate(1); }",
        ("Windows", "SetWindowLongPtr") => "{ return 1; }",
        ("Windows", "LoadIcon") => "{ return 1; }",
        ("Windows", "ShellNotifyIcon") => "{ TrayTestNative.NativeId = data->Id; return true; }",
        ("Windows", "AppendMenu") => "{ return true; }",
        ("Windows", "DestroyMenu" or "DestroyWindow" or "DestroyIcon") => "{ TrayTestNative.Release(" + (method == "DestroyMenu" ? "menu" : method == "DestroyWindow" ? "window" : "icon") + "); return true; }",
        ("Windows", "GetCursorPos") => "{ point = default; return true; }",
        ("Windows", "ClientToScreen") => "{ return true; }",
        ("Windows", "IsWindow") => "{ return TrayTestNative.References.GetValueOrDefault(window) > 0; }",
        ("Windows", "SetForegroundWindow") => "{ TrayTestNative.Check(window); return true; }",
        ("Windows", "TrackPopupMenu") => "{ TrayTestNative.Popup(menu, window, 0); return 100; }",
        _ => "{ throw new NotSupportedException(\"Unexpected native call: " + method + "\"); }",
    };

    private const string NativeSource = """
        namespace NeoAstra.Desktop.Tray;
        public static class TrayTestNative
        {
            public static readonly Dictionary<nint, int> References = [];
            public static readonly List<string> Violations = [];
            private static readonly Dictionary<string, nint> Names = [];
            private static readonly Dictionary<nint, List<nint>> Children = [];
            private static nint _next;
            private static nint _target;
            public static nint Button, MenuItem, ContextWindow;
            public static uint NativeId;
            public static bool Secondary;
            public static bool OwnerCanClose;
            public static int PopupCount;
            public static Action? DuringPopup;

            public static void Reset()
            {
                References.Clear(); Violations.Clear(); Names.Clear(); Children.Clear();
                _next = 100; _target = Button = MenuItem = ContextWindow = 0; NativeId = 0; Secondary = true; OwnerCanClose = false; PopupCount = 0; DuringPopup = null;
            }
            public static nint Name(string name)
            {
                if (!Names.TryGetValue(name, out var value)) Names[name] = value = -(Names.Count + 1);
                return value;
            }
            public static nint Allocate(int references) { var value = ++_next; References.Add(value, references); return value; }
            public static nint Send(nint target, nint selector)
            {
                if (selector == Name("alloc")) return Allocate(1);
                if (selector == Name("init")) return target;
                if (selector == Name("button")) { Button = Allocate(1); Children[target] = [Button]; return Button; }
                if (selector == Name("systemStatusBar") || selector == Name("sharedApplication") || selector == Name("currentEvent")) return -100;
                throw new NotSupportedException("Unexpected Objective-C message");
            }
            public static void SendVoid(nint target, nint selector)
            {
                if (selector == Name("retain")) References[target]++;
                else if (selector == Name("release")) Release(target);
                else throw new NotSupportedException("Unexpected Objective-C void message");
            }
            public static void SendVoidArg(nint target, nint selector, nint value)
            {
                if (selector == Name("setTarget:") && value != 0) _target = value;
                if (selector == Name("addItem:")) { if (!Children.TryGetValue(target, out var children)) Children[target] = children = []; children.Add(value); References[value]++; }
                if (selector == Name("popUpStatusItemMenu:")) Popup(value, target, _target);
            }
            public static void Release(nint value)
            {
                if (!References.TryGetValue(value, out var count) || count <= 0) { Violations.Add("Resource released twice: " + value); return; }
                References[value] = count - 1;
                if (count == 1 && Children.Remove(value, out var children)) foreach (var child in children) Release(child);
            }
            public static void Check(nint value)
            {
                if (value != 0 && (!References.TryGetValue(value, out var count) || count <= 0)) Violations.Add("Use of released resource: " + value);
            }
            public static void Popup(nint menu, nint owner, nint target)
            {
                PopupCount++;
                Check(menu); Check(owner); Check(target);
                DuringPopup?.Invoke();
                Check(menu); if (!OwnerCanClose) Check(owner); Check(target);
            }
        }
        """;
}
