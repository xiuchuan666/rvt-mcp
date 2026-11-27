using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace RvtMcp.Tests;

public class FailureDocumentLifetimeTests
{
    private static readonly HashSet<string> LifetimeMethods = new()
    { "OnDocumentClosed", "OnDocumentClosing", "PruneClosedDocuments", "PruneDocuments", "IsLiveDocument" };
    private static readonly HashSet<string> FamilyMethods = new()
    { "AuthorizeFamilySave", "AllowFamilySaveAs" };


    [Fact]
    public void Real_family_save_authorization_is_one_shot_and_only_for_the_owned_document_and_path()
    {
        var result = (object[])CompileFamilyProbe().Invoke(null, Array.Empty<object>())!;
        Assert.Equal(new object[] { true, false, false, false, false, false }, result);
    }

    private static MethodInfo CompileFamilyProbe([CallerFilePath] string file = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "..", "..", ".."));
        var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "src/shared/Failures/FailureNativeEvents.cs"))).GetRoot();
        var methods = syntax.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => FamilyMethods.Contains(method.Identifier.ValueText))
            .Select(method => method.ToFullString()
                .Replace("Handlers.OpenModelLocalPolicy", "OpenModelLocalPolicy")
                .Replace("Handlers.ParametricFamilyInput", "ParametricFamilyInput")
                .Replace("Active.SaveOperation();", ""));
        var types = syntax.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(type => type.Identifier.ValueText == "FamilyAuthoringAuthorization" || type.Identifier.ValueText == "ActionOnDispose")
            .Select(type => type.ToFullString());
        var text = "using System; using System.IO; using System.Collections.Generic; using RvtMcp.Plugin.Handlers; public class FamilyProbe {public static dynamic Active = null; public static class ParametricFamilyInput { public static string TemporaryOutputDirectory(string path)=>Path.GetDirectoryName(path); } " + string.Join("\n", types) + string.Join("\n", methods) + @"
static FamilyAuthoringAuthorization _familyAuthoring;
static bool Scoped()=>false;
static class McpDocument {public static Document CurrentOwnedDocument(Document doc)=>doc;}
public class Document {public bool IsValidObject=true,IsFamilyDocument=true,IsWorkshared;}
public class DocumentState {}
public class DocumentSavingAsEventArgs {public Document Document;public string PathName;public bool IsSavingAsCentralFile;}
public static object[] Run() {
 var dir=Path.Combine(Path.GetTempPath(),""family-save-probe-""+Guid.NewGuid().ToString(""N""));Directory.CreateDirectory(dir);
 var doc=new Document();var path=Path.Combine(dir,"".rvtmcp-test.rfa"");
 try {
  using(AuthorizeFamilySave(doc,path)) {
   var first=AllowFamilySaveAs(new DocumentSavingAsEventArgs{Document=doc,PathName=path},null);
   var once=AllowFamilySaveAs(new DocumentSavingAsEventArgs{Document=doc,PathName=path},null);
   return new object[]{first,once,
    AllowFamilySaveAs(new DocumentSavingAsEventArgs{Document=doc,PathName=Path.Combine(dir,""other.rfa"")},null),
    AllowFamilySaveAs(new DocumentSavingAsEventArgs{Document=doc,PathName=path,IsSavingAsCentralFile=true},null),
    AllowFamilySaveAs(new DocumentSavingAsEventArgs{Document=new Document(),PathName=path},null),
    AllowFamilySaveAs(new DocumentSavingAsEventArgs{Document=doc,PathName=dir},null)};
  }
 } finally {Directory.Delete(dir,true);}
}
}";
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(RvtMcp.Plugin.Handlers.OpenModelLocalPolicy).Assembly.Location).Distinct()
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("FamilySave_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(text) }, refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var emit = compilation.Emit(output);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        return Assembly.Load(output.ToArray()).GetType("FamilyProbe")!.GetMethod("Run")!;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Native_close_cleanup_never_hashes_a_dead_document_and_preserves_live_wrappers(bool succeeded)
    {
        var result = (object[])CompileProbe().Invoke(null, new object[] { succeeded })!;
        Assert.Null(result[0]);
        Assert.Equal(succeeded ? 0 : 1, result[1]);
        Assert.Equal(succeeded ? 0 : 1, result[2]);
        Assert.Equal(succeeded ? 0 : 1, result[3]);
        Assert.True((bool)result[4]);
    }

    [Theory]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    public void Actual_closing_handler_allows_owned_discard_cleanup_even_after_a_stop(
        bool authorized, bool modified, bool stopped, bool expectedCancelled)
    {
        var values = (object[])CompileProbe().DeclaringType!.GetMethod("ClosingProbe")!
            .Invoke(null, new object[] { authorized, modified, stopped })!;
        Assert.Equal(expectedCancelled, values[0]);
        Assert.Equal(authorized ? 0 : 1, values[1]);
    }

    private static MethodInfo CompileProbe([CallerFilePath] string file = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "..", "..", ".."));
        var methods = string.Join("\n", new[] { "FailureControl.cs", "FailureNativeEvents.cs" }
            .SelectMany(name => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "src/shared/Failures", name)))
                .GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
            .Where(method => LifetimeMethods.Contains(method.Identifier.ValueText))
            .Select(method => method.ToFullString()));
        var text = "using System; using System.Linq; using System.Collections.Generic; public class Probe {" + methods + Body + "}";
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(RvtMcp.Plugin.Handlers.OpenModelLocalPolicy).Assembly.Location).Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("Lifetime_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(text) }, refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var emit = compilation.Emit(output);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        return Assembly.Load(output.ToArray()).GetType("Probe")!.GetMethod("Run")!;
    }

    private const string Body = """
public class Life { public bool Valid = true; }
public class Document {
 public Life Life;
 public bool IsModified;
 public bool IsValidObject => Life.Valid;
 public bool IsFamilyDocument = true;
 public bool IsWorkshared;
 public override int GetHashCode() => IsValidObject ? 1 : throw new InvalidOperationException("dead_document_hash");
 public override bool Equals(object obj) {
  if (!IsValidObject) throw new InvalidOperationException("dead_document_equals");
  return obj is Document other && ReferenceEquals(Life, other.Life);
 }
}
public class Model { public string ModelScopeKey="model"; }
public class DocumentState { public Document Document; public Model Model = new(); }
public enum RevitAPIEventStatus { Succeeded, Cancelled }
public class DocumentClosedEventArgs { public int DocumentId; public RevitAPIEventStatus Status; }
public class DocumentClosingEventArgs { public int DocumentId; public Document Document; public bool Cancelled; }
static Dictionary<Document,DocumentState> Documents = new();
static Dictionary<Document,string> Saving = new(), SyncAuthorities = new();
static Dictionary<int,DocumentState> Closing = new();
static bool InScope, Stopped, Authorized;
static int StateCalls;
static bool Scoped() => InScope;
static class McpDocument {
 public static bool IsDiscardAuthorized(Document doc)=>Authorized;
 public static Document CurrentOwnedDocument(Document doc)=>doc.Life==null?null:doc;
}
static DocumentState State(Document doc) {StateCalls++;return new DocumentState {Document=doc};}
static void DenyIncomplete(DocumentClosingEventArgs args,DocumentState state,string action,string description) {args.Cancelled=true;}
static bool Guard(DocumentClosingEventArgs args,DocumentState state,Model model,string action,string hash,string revision,string description,bool grant,bool complete) {
 args.Cancelled=Stopped;return !Stopped;
}
static string Hash(string value)=>value;
static string Revision(DocumentState state)=>"revision";
static void Invalidate(DocumentState state) {}
static void BumpContextGeneration() {}
static void ObserveCancelledOrFailed(RevitAPIEventStatus status,string action,Document doc) {}
public static object[] ClosingProbe(bool authorized,bool modified,bool stopped) {
 InScope=true;Stopped=stopped;Authorized=authorized;
 var doc=new Document {Life=new Life(),IsModified=modified};
 Documents.Add(doc,new DocumentState {Document=doc});
 var args=new DocumentClosingEventArgs {DocumentId=1,Document=doc};
 OnDocumentClosing(null,args);
 return new object[]{args.Cancelled,StateCalls};
}
public static object[] Run(bool succeeded) {
 var life = new Life(); var doc = new Document { Life=life }; var state = new DocumentState { Document=doc };
 Documents.Add(doc,state); Saving.Add(doc,"save"); SyncAuthorities.Add(doc,"sync"); Closing.Add(1,state);
 life.Valid = !succeeded;
 string error=null;
 try { OnDocumentClosed(null,new DocumentClosedEventArgs { DocumentId=1, Status=succeeded?RevitAPIEventStatus.Succeeded:RevitAPIEventStatus.Cancelled }); }
 catch(Exception ex) { error=ex.Message; }
 var newLife = new Life(); var next = new Document { Life=newLife };
 bool equivalent=false;
 try {
  Documents.Add(next,new DocumentState { Document=next });
  equivalent=Documents.ContainsKey(new Document { Life=newLife });
  Documents.Remove(next);
 } catch(Exception ex) { error ??= ex.Message; }
 return new object[]{error,Documents.Count,Saving.Count,SyncAuthorities.Count,equivalent};
}
""";
}
