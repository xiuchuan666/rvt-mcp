using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests;

/// <summary>
/// Compiles the unchanged handler against a test-only Revit API seam. The seam proves
/// control flow, stable-identity checks and file lifecycle, not native Revit behavior.
/// Reopening and flexing an actual .rfa remain hosted Revit 2027 E2E requirements.
/// </summary>
public sealed class CreateParametricFamilyHandlerTests
{
    private static readonly Lazy<Func<string, JObject>> R27Probe = new(() => HandlerProbe.Compile(R27Seam,
        new[] { "src/shared/Handlers/Families/CreateParametricFamilyHandler.cs",
            "src/shared/Handlers/Families/ParametricFamilyInput.cs", "src/shared/Infrastructure/RevitCompat.cs",
            "src/shared/Infrastructure/McpDocument.cs", "src/shared/Handlers/Documents/OpenModelLocalPolicy.cs" },
        symbols: new[] { "REVIT2027", "REVIT2024_OR_GREATER", "REVIT2027_OR_GREATER" }));

    private static readonly Lazy<Func<string, JObject>> R22Probe = new(() => HandlerProbe.Compile(UnsupportedSeam,
        new[] { "src/shared/Handlers/Families/CreateParametricFamilyHandler.cs" }, symbols: null));

    [Fact]
    public void Creates_type_parameters_associates_geometry_and_returns_readback()
    {
        var result = R27Probe.Value("success");
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        var data = result["data"]!;
        Assert.True(data.Value<bool>("created"));
        Assert.True(data.Value<bool>("saved"));
        Assert.True(data.Value<bool>("native_editable"));
        Assert.Equal("extrusion", data.Value<string>("geometry_kind"));
        Assert.Equal(3, data["parameters"]!.Count());
        Assert.All(data["parameters"]!, parameter =>
        {
            Assert.Equal("type", parameter.Value<string>("scope"));
            Assert.True(parameter.Value<bool>("associated"));
        });
        Assert.True(result.Value<bool>("initial_type_was_null"));
        Assert.True(result.Value<bool>("new_parameter_wrappers"));
        Assert.Equal(3, result.Value<int>("labelled_dimensions_and_extrusion_associations"));
        Assert.Equal(4, result.Value<int>("locked_profile_alignments"));
        Assert.Equal(1200, data["geometry"]!["bounding_box_mm"]!.Value<double>("length"), 6);
        Assert.Equal(600, data["geometry"]!["bounding_box_mm"]!.Value<double>("width"), 6);
        Assert.Equal(900, data["geometry"]!["bounding_box_mm"]!.Value<double>("height"), 6);
        Assert.True(data["geometry"]!.Value<bool>("dimensions_match"));
        Assert.Equal(1, data["geometry"]!.Value<int>("solid_count"));
        Assert.Equal(4, data["geometry"]!.Value<int>("reference_plane_count"));
        Assert.Equal(2, data["geometry"]!.Value<int>("labelled_dimension_count"));
        Assert.Equal(2, data["geometry"]!.Value<int>("origin_equality_count"));
        Assert.Equal(4, data["geometry"]!.Value<int>("locked_alignment_count"));
        Assert.Equal(1, result.Value<int>("transaction_commits"));
        Assert.Equal(1, result.Value<int>("document_closes"));
        Assert.Empty(result["temporary_files"]!);
    }

    [Theory]
    [InlineData("geometry_failure", 0)]
    [InlineData("template_failure", 0)]
    [InlineData("save_failure", 0)]
    [InlineData("profile_wrapper_failure", 0)]
    public void Failure_never_saves_a_partial_family_and_closes_any_owned_document(string scenario, int saves)
    {
        var result = R27Probe.Value(scenario);
        Assert.False(result.Value<bool>("success"));
        Assert.Contains("no requested output file was written", result.Value<string>("error"));
        if (scenario != "template_failure")
            Assert.Contains("closed without saving to output_path", result.Value<string>("error"));
        Assert.Equal(saves, result.Value<int>("saves"));
        Assert.False(result.Value<bool>("output_exists"));
        if (scenario != "template_failure" && scenario != "save_failure")
            Assert.Equal(1, result.Value<int>("transaction_rollbacks"));
        Assert.Equal(scenario == "template_failure" ? 0 : 1, result.Value<int>("document_closes"));
        Assert.Empty(result["temporary_files"]!);
    }

    [Fact]
    public void Active_family_is_never_closed_and_never_saved_when_revit_activates_it_on_open()
    {
        var result = R27Probe.Value("active_document");
        Assert.False(result.Value<bool>("success"));
        Assert.Contains("active document", result.Value<string>("error"));
        Assert.Contains("remains open for user review", result.Value<string>("error"));
        Assert.Contains("no requested output file was written", result.Value<string>("error"));
        // Fail-fast at NewFamilyDocument: no transaction, no SaveAs, no temp file, no close attempt.
        Assert.Equal(0, result.Value<int>("document_closes"));
        Assert.Equal(0, result.Value<int>("saves"));
        Assert.Equal(0, result.Value<int>("transaction_commits"));
        Assert.False(result.Value<bool>("output_exists"));
        Assert.Empty(result["temporary_files"]!);
    }

    [Fact]
    public void Solid_readback_requires_the_extrusions_own_positive_volume_element_geometry()
    {
        var result = R27Probe.Value("foreign_geometry");
        Assert.False(result.Value<bool>("success"));
        Assert.Contains("own element geometry", result.Value<string>("error"));
        Assert.False(result.Value<bool>("output_exists"));
    }

    [Fact]
    public void Moving_before_close_would_fail_because_native_save_locks_the_file()
    {
        var result = R27Probe.Value("success");
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.True(result.Value<bool>("save_lock_released_on_close"));
        Assert.True(result.Value<bool>("locked_while_saved"));
    }

    [Theory]
    [InlineData("close_false")]
    [InlineData("close_exception")]
    public void A_failed_close_retains_the_temporary_file_and_never_moves_it(string scenario)
    {
        var result = R27Probe.Value(scenario);
        Assert.False(result.Value<bool>("success"));
        Assert.Contains("The family document was not closed", result.Value<string>("error"));
        Assert.Contains("retained for inspection", result.Value<string>("error"));
        Assert.False(result.Value<bool>("output_exists"));
        Assert.Single(result["temporary_files"]!);
        Assert.Equal(1, result.Value<int>("document_closes"));
    }

    [Fact]
    public void A_family_activated_after_save_is_never_closed_and_keeps_its_temporary_file()
    {
        // Second IsActiveDocument guard: a third-party event activated the family during SaveAs.
        var result = R27Probe.Value("active_after_save");
        Assert.False(result.Value<bool>("success"));
        Assert.Contains("active document", result.Value<string>("error"));
        Assert.Contains("remains open for user review", result.Value<string>("error"));
        Assert.Equal(1, result.Value<int>("saves"));
        Assert.Equal(0, result.Value<int>("document_closes"));
        Assert.False(result.Value<bool>("output_exists"));
        Assert.Single(result["temporary_files"]!);
    }

    [Fact]
    public void Fully_discarded_family_is_marked_private_and_diagnostic_survives_in_structured_data()
    {
        // save_failure: authorization throws before SaveAs; the owned family is closed and no temp
        // file exists, so the private-family-discarded flag is set and no external mutation is implied.
        var result = R27Probe.Value("save_failure");
        Assert.False(result.Value<bool>("success"));
        Assert.True(result.Value<bool>("private_family_discarded"));
        var diagnostic = result["data"]?.Value<string>("lifecycle_diagnostic");
        Assert.False(string.IsNullOrEmpty(diagnostic));
        Assert.Contains("no requested output file was written", diagnostic);
    }

    [Fact]
    public void Retained_open_family_is_not_marked_as_fully_discarded()
    {
        // close_false: the document stayed open, so PrivateFamilyDiscarded must remain false even
        // though no output was written — the temporary family still exists for user review.
        var result = R27Probe.Value("close_false");
        Assert.False(result.Value<bool>("success"));
        Assert.False(result.Value<bool>("private_family_discarded"));
        Assert.False(string.IsNullOrEmpty(result["data"]?.Value<string>("lifecycle_diagnostic")));
    }

    [Fact]
    public void Existing_compatible_type_and_parameters_are_reused()
    {
        var result = R27Probe.Value("existing_compatible");
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal(0, result.Value<int>("new_types"));
        Assert.Equal(0, result.Value<int>("new_parameters"));
        Assert.Equal(3, result.Value<int>("set_values"));
    }

    [Theory]
    [InlineData("existing_instance")]
    [InlineData("existing_text")]
    [InlineData("existing_formula")]
    [InlineData("existing_associated")]
    [InlineData("existing_reporting")]
    public void Incompatible_existing_parameters_are_not_overwritten(string scenario)
    {
        var result = R27Probe.Value(scenario);
        Assert.False(result.Value<bool>("success"));
        Assert.Contains("incompatible or already-used", result.Value<string>("error"));
        Assert.False(result.Value<bool>("output_exists"));
        Assert.Equal(1, result.Value<int>("transaction_rollbacks"));
    }

    [Fact]
    public void Invalid_parameters_do_not_open_a_revit_document()
    {
        var result = R27Probe.Value("invalid_dimensions");
        Assert.False(result.Value<bool>("success"));
        Assert.Contains("length_mm", result.Value<string>("error"));
        Assert.Equal(0, result.Value<int>("document_opens"));
    }

    [Fact]
    public void Dimensions_require_regeneration_and_geometric_references_before_creation()
    {
        // Real Revit 2027 failure: "The references are not geometric references" when NewDimension
        // receives ReferencePlane.GetReference() from planes that have not been regenerated. The seam
        // now models that: a plane's element reference is non-geometric until Document.Regenerate()
        // runs, after which its PlanarFace exposes a genuine geometric reference. The handler must
        // regenerate after creating the four planes and before any NewDimension call.
        var result = R27Probe.Value("success");
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.True(result.Value<bool>("dimensions_created_after_regeneration"));
        Assert.Equal(3, result.Value<int>("labelled_dimensions_and_extrusion_associations"));
        Assert.Equal(2, result.Value<int>("origin_equality_count"));
    }

    [Fact]
    public void Older_shells_return_unsupported_version_without_parsing_or_opening_a_document()
    {
        var result = R22Probe.Value("ignored");
        Assert.False(result.Value<bool>("success"));
        Assert.StartsWith("UNSUPPORTED_VERSION:", result.Value<string>("error"));
        Assert.Equal("create_parametric_family", result.Value<string>("name"));
    }

    private const string UnsupportedSeam = """
using Newtonsoft.Json.Linq;
namespace Autodesk.Revit.DB { public class Unused { } }namespace Autodesk.Revit.UI { public class UIApplication { } }
namespace RvtMcp.Plugin { public interface IRevitCommand { } public class CommandResult {
 public bool Success; public string Error; public static CommandResult Fail(string error)=>new CommandResult{Error=error}; } }
public static class Probe { public static string Run(string scenario) {
 var handler=new RvtMcp.Plugin.Handlers.CreateParametricFamilyHandler(); var r=handler.Execute(new Autodesk.Revit.UI.UIApplication(),"not json");
 return new JObject{["success"]=r.Success,["error"]=r.Error,["name"]=handler.Name}.ToString(); } }
""";

    private const string R27Seam = """
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
namespace Autodesk.Revit.Creation { public class FamilyItemFactory { public Autodesk.Revit.DB.Document Document;
 public Autodesk.Revit.DB.Extrusion NewExtrusion(bool solid,Autodesk.Revit.DB.CurveArrArray loops,Autodesk.Revit.DB.SketchPlane plane,double height){
  var e=new Autodesk.Revit.DB.Extrusion(Document){Height=height}; e.SetLoops(loops); Document.Extrusion=e; Document.Items.Add(e); return e; }
 public Autodesk.Revit.DB.ReferencePlane NewReferencePlane(Autodesk.Revit.DB.XYZ a,Autodesk.Revit.DB.XYZ b,Autodesk.Revit.DB.XYZ c,Autodesk.Revit.DB.ViewPlan v){
  var p=new Autodesk.Revit.DB.ReferencePlane(Document,a,b,c); Document.Items.Add(p); return p; }
 public Autodesk.Revit.DB.Dimension NewDimension(Autodesk.Revit.DB.ViewPlan v,Autodesk.Revit.DB.Line l,Autodesk.Revit.DB.ReferenceArray r){
  if(r.Items.Any(x=>x==null||!x.IsGeometric))throw new ArgumentException("The references are not geometric references. Parameter name: references");
  Document.DimensionsAfterRegeneration++;
  var d=new Autodesk.Revit.DB.Dimension(Document,r.Items.Count); if(r.Items.Count==2)Document.Associations++; Document.Items.Add(d); return d; }
 public Autodesk.Revit.DB.Dimension NewAlignment(Autodesk.Revit.DB.ViewPlan v,Autodesk.Revit.DB.Reference a,Autodesk.Revit.DB.Reference b){
  if(a==null||b==null||b.Curve==null||(a.Curve!=null&&a.Curve!=b.Curve))throw new Exception("References must belong to the same native ModelCurve geometry.");
  var d=new Autodesk.Revit.DB.Dimension(Document,2){IsLocked=true}; Document.Items.Add(d); return d; } } }
namespace Autodesk.Revit.DB {
 public enum TransactionStatus { Started,Committed,RolledBack }
 public enum ViewType { FloorPlan }
 public enum BuiltInParameter { EXTRUSION_START_PARAM,EXTRUSION_END_PARAM }
 public enum StorageType { Double,String }
 public class ElementId { public long Value; public ElementId(long id){Value=id;} }
 public class XYZ { public double X,Y,Z; public XYZ(double x,double y,double z){X=x;Y=y;Z=z;}
  public static XYZ Zero=new XYZ(0,0,0); public static XYZ BasisX=new XYZ(1,0,0); public static XYZ BasisY=new XYZ(0,1,0); public static XYZ BasisZ=new XYZ(0,0,1);
  public XYZ Add(XYZ o)=>new XYZ(X+o.X,Y+o.Y,Z+o.Z); public XYZ Multiply(double n)=>new XYZ(X*n,Y*n,Z*n); }
 public abstract class Curve { public XYZ A,B; public Reference NativeReference; public virtual Reference Reference=>null;
  public XYZ Direction=>new XYZ(B.X-A.X,B.Y-A.Y,B.Z-A.Z); public XYZ GetEndPoint(int i)=>i==0?A:B; }
 public class Line:Curve { public override Reference Reference=>NativeReference; public static Line CreateBound(XYZ a,XYZ b)=>new Line{A=a,B=b}; }
 public class CurveArray:IEnumerable<Curve> { public List<Curve> Items=new List<Curve>(); public void Append(Curve c){Items.Add(c);}
  public IEnumerator<Curve> GetEnumerator()=>Items.GetEnumerator(); IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator(); }
 public class CurveArrArray { public List<CurveArray> Items=new List<CurveArray>(); public void Append(CurveArray c){Items.Add(c);} }
 public static class GroupTypeId { public static object Geometry=new object(); }
 public static class SpecTypeId { public static object Length=new object(); }
 public class Reference { public Curve Curve; public bool IsGeometric; public Reference(){} public Reference(Curve curve){Curve=curve;IsGeometric=true;} }
 public class ReferenceArray { public List<Reference> Items=new List<Reference>(); public void Append(Reference r){Items.Add(r);} }
 public class PlanarFace { public Reference Reference=new Reference{IsGeometric=true}; }
 public class PlaneGeometryElement:IEnumerable<PlanarFace> { private bool HasGeometry; public PlaneGeometryElement(bool hasGeometry){HasGeometry=hasGeometry;}
  public IEnumerator<PlanarFace> GetEnumerator(){if(HasGeometry)yield return new PlanarFace();}
  IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator(); }
 public class Element { public ElementId Id; public string Name; public Document Document; public Element(Document d){Document=d;Id=new ElementId(d.NextId++);} }
 public class Level:Element { public double Elevation; public Level(Document d):base(d){} }
 public class Plane { public XYZ Origin,Normal; public static Plane CreateByOriginAndBasis(XYZ o,XYZ x,XYZ y)=>new Plane{Origin=o,Normal=new XYZ(0,0,1)}; }
 public class ReferencePlane:Element { private Plane Plane; public ReferencePlane(Document d,XYZ a,XYZ b,XYZ normal):base(d){
  var vertical=Math.Abs(b.Y-a.Y)>Math.Abs(b.X-a.X); Plane=new Plane{Origin=a,Normal=vertical?new XYZ(1,0,0):new XYZ(0,1,0)}; }
  public Plane GetPlane()=>Plane;
  // Before regeneration the plane has no resolved geometry, so GetReference() is a
  // non-geometric element reference; NewDimension must reject it. After regeneration the
  // plane's PlanarFace exposes a genuine geometric reference (see PlaneGeometryElement).
  public Reference GetReference()=>new Reference{IsGeometric=Document.Regenerated};
  public PlaneGeometryElement get_Geometry(Options o)=>new PlaneGeometryElement(Document.Regenerated); }
 public class ViewPlan:Element { public bool IsTemplate; public ViewType ViewType=ViewType.FloorPlan; public Level GenLevel; public ViewPlan(Document d):base(d){} }
 public class SketchPlane { public static SketchPlane Create(Document doc,Plane plane)=>new SketchPlane(); }

 public class Sketch { private Document Doc; private List<ModelCurve> Curves=new List<ModelCurve>(); public Sketch(Document d){Doc=d;} public void Add(ModelCurve c){Curves.Add(c);}
  public IEnumerable<ElementId> GetAllElements()=>Curves.Select(c=>c.Id).ToArray(); }
 public class ModelCurve:Element { private Line Line; public ModelCurve(Document d,Line l):base(d){Line=l;}
  public Curve GeometryCurve=>Document.Scenario=="profile_wrapper_failure"?new WrapperLine{A=Line.A,B=Line.B}:Line; }
 public class WrapperLine:Line { public override Reference Reference=>null; }
 public class BoundingBoxXYZ { public XYZ Min,Max; }
 public class Definition { public string Name; }
 public class Parameter { public bool IsReadOnly; public double Value; public Definition Definition; public double AsDouble()=>Value; }
 public class ParameterSet { public int Size; }
 public class FamilyParameter:Element { public FamilyParameter(Document d,string name):base(d){Definition=new Definition{Name=name};Name=name;}
  public Definition Definition; public double Value; public bool IsInstance; public bool IsReporting; public StorageType StorageType=StorageType.Double; public object Unit=SpecTypeId.Length; public string Formula;
  public ParameterSet AssociatedParameters=new ParameterSet(); public object GetUnitTypeId()=>Unit; }
 public class Solid { public double Volume=1; public bool IsElementGeometry=true; }
 public class GeometryElement:IEnumerable<Solid> { public IEnumerator<Solid> GetEnumerator(){yield return new Solid{IsElementGeometry=Document.Scenario!="foreign_geometry"};} IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator(); }
 public class Options { }
 public class Extrusion:Element { public Extrusion(Document d):base(d){Sketch=new Sketch(d);} public Sketch Sketch; public double Height;
  private Parameter End=new Parameter(); private Parameter Start=new Parameter();
  public void SetLoops(CurveArrArray loops){foreach(var l in loops.Items[0].Items){var line=(Line)l;line.NativeReference=new Reference(line);var curve=new ModelCurve(Document,line);Sketch.Add(curve);Document.Items.Add(curve);}}
  public Parameter get_Parameter(BuiltInParameter p){var result=p==BuiltInParameter.EXTRUSION_END_PARAM?End:Start;result.Value=p==BuiltInParameter.EXTRUSION_END_PARAM?Height:0;return result;}
  public BoundingBoxXYZ get_BoundingBox(object o){if(Document.FailGeometry)return null;
   var lines=Sketch.GetAllElements().Select(id=>((ModelCurve)Document.GetElement(id)).GeometryCurve).ToArray();
   return new BoundingBoxXYZ{Min=new XYZ(lines.Min(l=>l.GetEndPoint(0).X),lines.Min(l=>l.GetEndPoint(0).Y),0),Max=new XYZ(lines.Max(l=>l.GetEndPoint(1).X),lines.Max(l=>l.GetEndPoint(1).Y),Height)}; }
  public GeometryElement get_Geometry(Options o)=>new GeometryElement(); }
 public class FamilyType { public string Name; public FamilyType(string name){Name=name;} public double? AsDouble(FamilyParameter p)=>p.Value; }
 public class FamilyTypeSet:IEnumerable<FamilyType> { public List<FamilyType> Items=new List<FamilyType>();
  public IEnumerator<FamilyType> GetEnumerator()=>Items.GetEnumerator(); IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator(); }
 public class FamilyManager { private Document Doc; public FamilyManager(Document d){Doc=d;} public FamilyType CurrentType; public FamilyTypeSet Types=new FamilyTypeSet();
  public List<FamilyParameter> Parameters=new List<FamilyParameter>(); public FamilyParameter[] GetParameters()=>Parameters.Select(p=>new FamilyParameter(Doc,p.Name){
   Id=p.Id,Value=p.Value,IsInstance=p.IsInstance,IsReporting=p.IsReporting,StorageType=p.StorageType,Unit=p.Unit,Formula=p.Formula,AssociatedParameters=p.AssociatedParameters}).ToArray();
  public FamilyParameter AddParameter(string n,object g,object s,bool i){Doc.NewParameters++;var p=new FamilyParameter(Doc,n);Parameters.Add(p);return Copy(p);}
  public FamilyType NewType(string n){Doc.NewTypes++;CurrentType=new FamilyType(n);Types.Items.Add(CurrentType);return CurrentType;}
  public void Set(FamilyParameter p,double v){Parameters.Single(x=>x.Id.Value==p.Id.Value).Value=v;Doc.SetValues++;}
  public void AssociateElementParameterToFamilyParameter(Parameter p,FamilyParameter f){var stored=Parameters.Single(x=>x.Id.Value==f.Id.Value);stored.AssociatedParameters.Size++;p.Definition=stored.Definition;Doc.Associations++;}
  public FamilyParameter GetAssociatedFamilyParameter(Parameter p)=>p.Definition==null?null:Copy(Parameters.Single(x=>x.Definition.Name==p.Definition.Name));
  private FamilyParameter Copy(FamilyParameter p)=>new FamilyParameter(Doc,p.Name){Id=p.Id,Value=p.Value,Formula=p.Formula}; }
 public class Dimension:Element { public Dimension(Document d,int references):base(d){References=references;} public int References;
  public FamilyParameter FamilyLabel; public bool IsLocked; public bool AreSegmentsEqual;
  public void SetLabel(FamilyParameter p){FamilyLabel=p;} }
 public class FilteredElementCollector { private Document Doc; private Type RequestedType; public FilteredElementCollector(Document d){Doc=d;}
  public FilteredElementCollector OfClass(Type t){RequestedType=t;return this;} public IEnumerable<T> Cast<T>()=>Doc.Items.OfType<T>(); }
 public class Transaction:IDisposable { private Document Doc; private TransactionStatus Status;
  public Transaction(Document d,string n){Doc=d;} public TransactionStatus Start(){Doc.Transactions++;return Status=TransactionStatus.Started;}
  public TransactionStatus Commit(){Doc.Commits++;return Status=TransactionStatus.Committed;} public TransactionStatus RollBack(){Doc.Rollbacks++;return Status=TransactionStatus.RolledBack;}
  public TransactionStatus GetStatus()=>Status; public void Dispose(){} }
 public class SaveAsOptions { public bool OverwriteExistingFile; }
 public class Document { public List<Element> Items=new List<Element>(); public FamilyManager FamilyManager; public Autodesk.Revit.Creation.FamilyItemFactory FamilyCreate;
  public static string Scenario; public static bool FailGeometry=>Scenario=="geometry_failure"; public bool InitialTypeWasNull,NewParameterWrappers,SaveLockReleasedOnClose,LockedWhileSaved;
  public int Transactions,Commits,Rollbacks,Saves,Closes,NextId=1,NewTypes,NewParameters,SetValues,Associations,DimensionsAfterRegeneration; public Extrusion Extrusion; public string Title="Cabinet";
  private FileStream SaveLock;
  public Document(){FamilyManager=new FamilyManager(this);InitialTypeWasNull=FamilyManager.CurrentType==null;FamilyCreate=new Autodesk.Revit.Creation.FamilyItemFactory{Document=this};
   Items.Add(new ViewPlan(this)); var level=new Level(this){Elevation=0};Items.Add(level);Items.Add(new ViewPlan(this){GenLevel=level});
   Items.Add(new ReferencePlane(this,new XYZ(0,-2,0),new XYZ(0,2,0),XYZ.BasisZ){Name="Center (Left/Right)"});   Items.Add(new ReferencePlane(this,new XYZ(-2,0,0),new XYZ(2,0,0),XYZ.BasisZ){Name="Center (Front/Back)"});
   if(Scenario.StartsWith("existing_")){FamilyManager.CurrentType=new FamilyType("Standard");FamilyManager.Types.Items.Add(FamilyManager.CurrentType);
    foreach(var n in new[]{"Length","Width","Height"}){var p=new FamilyParameter(this,n);FamilyManager.Parameters.Add(p);
     if(Scenario=="existing_instance")p.IsInstance=true;if(Scenario=="existing_text")p.StorageType=StorageType.String;
     if(Scenario=="existing_formula")p.Formula="1 ft";if(Scenario=="existing_associated")p.AssociatedParameters.Size=1;if(Scenario=="existing_reporting")p.IsReporting=true;}}
   var first=FamilyManager.GetParameters().FirstOrDefault();NewParameterWrappers=FamilyManager.GetParameters().Length==0||!ReferenceEquals(first,FamilyManager.GetParameters().First());}
  public bool IsFamilyDocument=>true; public bool IsValidObject=true; public bool IsWorkshared=false; public bool Regenerated;
  public int Regenerations; public void Regenerate(){Regenerated=true;Regenerations++;}
  public Element GetElement(ElementId id)=>Items.Single(e=>e.Id.Value==id.Value);
  public void SaveAs(string path,SaveAsOptions o){Saves++;File.WriteAllText(path,"seam");SaveLock=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None);LockedWhileSaved=true;
   if(Scenario=="save_failure"){SaveLock.Dispose();SaveLock=null;throw new Exception("save refused");}
   if(Scenario=="active_after_save"&&OwnerApp!=null)OwnerApp.ActiveUIDocument=new Autodesk.Revit.UI.UIDocument{Document=this}; }
  public Autodesk.Revit.UI.UIApplication OwnerApp;
  public void ReleaseFailedCloseLock(){if(Scenario=="close_false"||Scenario=="close_exception"){SaveLock?.Dispose();SaveLock=null;}}
  public bool Close(bool save){Closes++;if(Scenario=="close_exception")throw new Exception("close refused");if(Scenario=="close_false")return false;
   SaveLockReleasedOnClose=SaveLock!=null;SaveLock?.Dispose();SaveLock=null;return true;} }
}
namespace Autodesk.Revit.ApplicationServices { public class Application { public static int Opens; public Autodesk.Revit.DB.Document Last; public Autodesk.Revit.UI.UIApplication Owner;
 public Autodesk.Revit.DB.Document NewFamilyDocument(string path){Opens++;if(Autodesk.Revit.DB.Document.Scenario=="template_failure")throw new Exception("bad template");Last=new Autodesk.Revit.DB.Document();Last.OwnerApp=Owner;Documents.Add(Last);if(Autodesk.Revit.DB.Document.Scenario=="active_document")Owner.ActiveUIDocument=new Autodesk.Revit.UI.UIDocument{Document=Last};return Last;} public System.Collections.Generic.List<Autodesk.Revit.DB.Document> Documents=new System.Collections.Generic.List<Autodesk.Revit.DB.Document>(); } }
namespace Autodesk.Revit.UI { public class UIDocument { public Autodesk.Revit.DB.Document Document; } public class UIApplication { public Autodesk.Revit.ApplicationServices.Application Application=new Autodesk.Revit.ApplicationServices.Application(); public UIDocument ActiveUIDocument; public UIApplication(){Application.Owner=this;} } }
namespace RvtMcp.Plugin { public interface IRevitCommand {} public class CommandResult { public bool Success; public string Error; public object Data;
 public static CommandResult Ok(object data)=>new CommandResult{Success=true,Data=data}; public static CommandResult Fail(string error)=>new CommandResult{Error=error}; } }
namespace RvtMcp.Plugin.Failures { public static class FailureControl {
 public class Scope { public bool PrivateFamilyDiscarded { get; private set; } public void MarkPrivateFamilyDiscarded(){PrivateFamilyDiscarded=true;} }
 public static Scope Active=new Scope();
 public static IDisposable AuthorizeFamilySave(Autodesk.Revit.DB.Document doc,string path) { if(Autodesk.Revit.DB.Document.Scenario=="save_failure")throw new Exception("native save authorization refused");return null; }
 public static void ThrowIfStopped(){} } }
public static class Probe { public static string Run(string scenario){
 Autodesk.Revit.DB.Document.Scenario=scenario; Autodesk.Revit.ApplicationServices.Application.Opens=0;
 RvtMcp.Plugin.Failures.FailureControl.Active=new RvtMcp.Plugin.Failures.FailureControl.Scope();
 var dir=Path.Combine(Path.GetTempPath(),"rvtmcp-family-probe-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);Autodesk.Revit.DB.Document doc=null;
 try { var template=Path.Combine(dir,"template.rft");File.WriteAllText(template,"seam");var output=Path.Combine(dir,"result.rfa");
 var request=new JObject{["family_name"]="Cabinet",["template_path"]=template,["output_path"]=output,["length_mm"]=scenario=="invalid_dimensions"?0:1200,["width_mm"]=600,["height_mm"]=900};
 var app=new Autodesk.Revit.UI.UIApplication();var r=new RvtMcp.Plugin.Handlers.CreateParametricFamilyHandler().Execute(app,request.ToString());doc=app.Application.Last;
 return new JObject{["success"]=r.Success,["error"]=r.Error,["data"]=r.Data==null?null:JObject.FromObject(r.Data),["document_opens"]=Autodesk.Revit.ApplicationServices.Application.Opens,
  ["document_closes"]=doc?.Closes??0,["saves"]=doc?.Saves??0,["output_exists"]=File.Exists(output),["transaction_commits"]=doc?.Commits??0,["transaction_rollbacks"]=doc?.Rollbacks??0,
  ["temporary_files"]=new JArray(Directory.GetFiles(dir,".rvtmcp-*.rfa")),["initial_type_was_null"]=doc?.InitialTypeWasNull??false,["new_parameter_wrappers"]=doc?.NewParameterWrappers??false,
  ["save_lock_released_on_close"]=doc?.SaveLockReleasedOnClose??false,["locked_while_saved"]=doc?.LockedWhileSaved??false,["new_types"]=doc?.NewTypes??0,["new_parameters"]=doc?.NewParameters??0,
  ["set_values"]=doc?.SetValues??0,["labelled_dimensions_and_extrusion_associations"]=doc?.Associations??0,
  ["dimensions_created_after_regeneration"]=(doc?.DimensionsAfterRegeneration??0)==4,
  ["origin_equality_count"]=doc?.Items.OfType<Autodesk.Revit.DB.Dimension>().Count(d=>d.References==3)??0,
  ["private_family_discarded"]=RvtMcp.Plugin.Failures.FailureControl.Active.PrivateFamilyDiscarded,
  ["locked_profile_alignments"]=doc?.Items.OfType<Autodesk.Revit.DB.Dimension>().Count(d=>d.IsLocked)??0}.ToString(); }
 finally { doc?.ReleaseFailedCloseLock();try{Directory.Delete(dir,true);}catch{} } } }
""";
}
