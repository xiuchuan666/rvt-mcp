using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests;

/// <summary>Native Revit seam: control flow and analytic geometry, not hosted Revit acceptance.</summary>
public sealed class CreateSegmentFamilyHandlerTests
{
    private static readonly Lazy<Func<string, JObject>> Probe = new(() => HandlerProbe.Compile(Seam,
        new[] { "src/shared/Handlers/Families/CreateSegmentFamilyHandler.cs", "src/shared/Handlers/Families/SegmentFamilyInput.cs",
            "src/shared/Handlers/Families/ParametricFamilyInput.cs", "src/shared/Infrastructure/RevitCompat.cs",
            "src/shared/Infrastructure/McpDocument.cs", "src/shared/Handlers/Documents/OpenModelLocalPolicy.cs" },
        symbols: new[] { "REVIT2027", "REVIT2024_OR_GREATER", "REVIT2027_OR_GREATER" }));

    [Fact]
    public void Creates_arc_extrusion_with_four_driving_parameters_and_real_native_counts()
    {
        var r = Probe.Value("success");
        Assert.True(r.Value<bool>("success"), r.Value<string>("error"));
        var d = r["data"]!;
        Assert.Equal("arc_extrusion", d.Value<string>("geometry_kind"));
        Assert.True(d.Value<bool>("native_editable"));
        Assert.Equal(4, d["parameters"]!.Count());
        Assert.All(d["parameters"]!, p => Assert.True(p!.Value<bool>("associated")));
        Assert.Equal("deg", d["parameters"]![3]!.Value<string>("unit"));
        Assert.Equal(14000, d["parameters"]![0]!.Value<double>("value_mm"));
        Assert.Null(d["parameters"]![0]!.Value<double?>("value_deg"));
        Assert.Equal(36, d["parameters"]![3]!.Value<double>("value_deg"));
        Assert.Null(d["parameters"]![3]!.Value<double?>("value_mm"));
        Assert.Contains("OuterRadius formula", d["parameters"]![0]!.Value<string>("driving_path"));
        Assert.Contains("InnerRadius formula", d["parameters"]![1]!.Value<string>("driving_path"));
        Assert.Equal(14000, d["parameters"]![0]!.Value<double>("value"));
        Assert.Equal(600, d["parameters"]![1]!.Value<double>("value"));
        Assert.Equal(2000, d["parameters"]![2]!.Value<double>("value"));
        Assert.Equal(36, d["parameters"]![3]!.Value<double>("value"));
        Assert.Equal(1, d["geometry"]!.Value<int>("solid_count"));
        Assert.Equal(2, d["geometry"]!.Value<int>("reference_arc_count"));
        Assert.Equal(2, d["geometry"]!.Value<int>("radial_reference_count"));
        Assert.Equal(0, d["geometry"]!.Value<int>("origin_dimension_count"));
        Assert.Equal(3, d["geometry"]!.Value<int>("labelled_dimension_count"));
        Assert.Equal(4, d["geometry"]!.Value<int>("profile_alignment_count"));
        Assert.Equal(4, d["geometry"]!.Value<int>("locked_alignment_count"));
        Assert.True(d["geometry"]!.Value<bool>("dimensions_match"));
        Assert.Equal(3, r.Value<int>("transaction_commits"));
        Assert.Equal(4, r.Value<int>("profile_curves"));
        Assert.Equal(2, r.Value<int>("profile_arcs"));
        Assert.Equal(2, r.Value<int>("profile_lines"));
        Assert.Equal(7000, r.Value<double>("outer_radius_mm"), 5);
        Assert.Equal(6400, r.Value<double>("inner_radius_mm"), 6);
        Assert.Equal(1, r.Value<int>("document_closes"));
        Assert.Empty(r["temporary_files"]!);
    }

    [Theory]
    [InlineData("flex_outer_diameter", "OuterDiameter")]
    [InlineData("flex_thickness", "Thickness")]
    [InlineData("flex_segment_angle", "SegmentAngle")]
    [InlineData("flex_ring_width", "RingWidth")]
    public void Parameter_flex_changes_native_stub_geometry_via_labels_or_extrusion_association(string scenario, string changed)
    {
        var r = Probe.Value(scenario);
        Assert.True(r.Value<bool>("success"), r.Value<string>("error"));
        Assert.True(r.Value<bool>("flex_applied"), scenario);
        Assert.Contains(changed, r.Value<string>("flex_changed_parameter"));
        Assert.True(r.Value<bool>("flex_geometry_changed"), "Changing " + changed + " must change analytic stub geometry.");
        Assert.Equal(3, r["data"]!["geometry"]!.Value<int>("labelled_dimension_count"));
        Assert.Equal(4, r["data"]!["geometry"]!.Value<int>("profile_alignment_count"));
        Assert.True(r["data"]!["geometry"]!.Value<bool>("dimensions_match"));
        Assert.Empty(r["temporary_files"]!);
    }

    [Theory]
    [InlineData("invalid_thickness", "thickness_mm")]
    [InlineData("invalid_angle", "segment_angle_deg")]
    [InlineData("template_failure", "bad template")]
    [InlineData("save_failure", "native save refused")]
    [InlineData("existing_formula", "incompatible or already-used")]
    public void Failure_path_does_not_leave_requested_output_or_temporary_file(string scenario, string error)
    {
        var r = Probe.Value(scenario);
        Assert.False(r.Value<bool>("success"));
        Assert.Contains(error, r.Value<string>("error"));
        Assert.False(r.Value<bool>("output_exists"));
        Assert.Empty(r["temporary_files"]!);
        Assert.Equal(scenario == "invalid_thickness" || scenario == "invalid_angle" ? 0 : 1, r.Value<int>("document_opens"));
        if (scenario != "invalid_thickness" && scenario != "invalid_angle" && scenario != "template_failure")
            Assert.Contains("closed without saving", r.Value<string>("error"));
    }

    [Fact]
    public void Existing_output_is_rejected_before_opening_a_family()
    {
        var r = Probe.Value("existing_output");
        Assert.False(r.Value<bool>("success"));
        Assert.Contains("already exists", r.Value<string>("error"));
        Assert.Equal(0, r.Value<int>("document_opens"));
    }

    [Fact]
    public void Curve_endpoint_reference_rejects_arc_center_index_like_native_revit()
    {
        var r = Probe.Value("endpoint_reference_contract");
        Assert.True(r.Value<bool>("start_and_end_accepted"));
        Assert.Equal("index", r.Value<string>("invalid_index_parameter"));
        Assert.Contains("0 or 1", r.Value<string>("invalid_index_error"));
    }

    [Fact]
    public void Closed_document_releases_native_save_lock_before_non_overwriting_move()
    {
        var r = Probe.Value("success");
        Assert.True(r.Value<bool>("success"), r.Value<string>("error"));
        Assert.True(r.Value<bool>("save_lock_released_on_close"));
        Assert.False(r.Value<bool>("temporary_path_exists_after_move"));
    }

    private const string Seam = """
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
namespace Autodesk.Revit.Creation { public class FamilyItemFactory { public Autodesk.Revit.DB.Document Document;
 public Autodesk.Revit.DB.ModelCurve NewModelCurve(Autodesk.Revit.DB.Curve c,Autodesk.Revit.DB.SketchPlane p){var m=new Autodesk.Revit.DB.ModelCurve(Document,c);Document.Items.Add(m);Document.References.Add(m);return m;}
 public Autodesk.Revit.DB.Dimension NewRadialDimension(Autodesk.Revit.DB.View v,Autodesk.Revit.DB.Reference r,Autodesk.Revit.DB.XYZ p){var d=new Autodesk.Revit.DB.Dimension(Document,"radial",r);Document.Items.Add(d);return d;}
 public Autodesk.Revit.DB.Dimension NewAngularDimension(Autodesk.Revit.DB.View v,Autodesk.Revit.DB.Arc a,Autodesk.Revit.DB.Reference r1,Autodesk.Revit.DB.Reference r2){var d=new Autodesk.Revit.DB.Dimension(Document,"angular",r1);Document.Items.Add(d);return d;}
 public Autodesk.Revit.DB.Dimension NewDimension(Autodesk.Revit.DB.View v,Autodesk.Revit.DB.Line l,Autodesk.Revit.DB.ReferenceArray r){var d=new Autodesk.Revit.DB.Dimension(Document,"origin",r.Items[1]);Document.Items.Add(d);return d;}
 public Autodesk.Revit.DB.Dimension NewAlignment(Autodesk.Revit.DB.View v,Autodesk.Revit.DB.Reference a,Autodesk.Revit.DB.Reference b){var d=new Autodesk.Revit.DB.Dimension(Document,"alignment",a){IsLocked=true};Document.Items.Add(d);return d;}
 public Autodesk.Revit.DB.Extrusion NewExtrusion(bool solid,Autodesk.Revit.DB.CurveArrArray loops,Autodesk.Revit.DB.SketchPlane p,double h){var e=new Autodesk.Revit.DB.Extrusion(Document,loops,h);Document.Items.Add(e);Document.Extrusion=e;return e;} } }
namespace Autodesk.Revit.DB {
 public enum TransactionStatus { Started,Committed,RolledBack } public enum ViewType { FloorPlan } public enum BuiltInParameter { EXTRUSION_END_PARAM } public enum StorageType { Double,String }
 public class ElementId { public long Value; public ElementId(long id){Value=id;} }
 public class XYZ { public double X,Y,Z; public XYZ(double x,double y,double z){X=x;Y=y;Z=z;} public static XYZ BasisX=new XYZ(1,0,0);public static XYZ BasisY=new XYZ(0,1,0);public static XYZ BasisZ=new XYZ(0,0,1);
  public double DistanceTo(XYZ p)=>Math.Sqrt((X-p.X)*(X-p.X)+(Y-p.Y)*(Y-p.Y)+(Z-p.Z)*(Z-p.Z)); public static XYZ operator -(XYZ a,XYZ b)=>new XYZ(a.X-b.X,a.Y-b.Y,a.Z-b.Z); }
 public class IntersectionResult { public XYZ XYZPoint; }
 public class Reference { public Curve Curve; public Reference(Curve c){Curve=c;} } public class ReferenceArray { public List<Reference> Items=new List<Reference>();public void Append(Reference r){Items.Add(r);} }
 public abstract class Curve { public Document Document; public XYZ Start,End; public string Kind; public double Radius; public XYZ Center; public double HalfAngle; public Reference Reference=>new Reference(this);
  public XYZ GetEndPoint(int i)=>i==0?Start:End; public Reference GetEndPointReference(int i){if(i!=0&&i!=1)throw new ArgumentOutOfRangeException("index","Value must be either 0 or 1.");return new Reference(this);} public double Length=>Kind=="line"?Start.DistanceTo(End):Radius*2*HalfAngle;
  public IntersectionResult Project(XYZ p){var d=End-Start;var l=Math.Sqrt(d.X*d.X+d.Y*d.Y+d.Z*d.Z);var n=new XYZ(d.X/l,d.Y/l,d.Z/l);var q=new XYZ(Start.X+n.X*((p.X-Start.X)*n.X+(p.Y-Start.Y)*n.Y+(p.Z-Start.Z)*n.Z),Start.Y+n.Y*((p.X-Start.X)*n.X+(p.Y-Start.Y)*n.Y+(p.Z-Start.Z)*n.Z),Start.Z+n.Z*((p.X-Start.X)*n.X+(p.Y-Start.Y)*n.Y+(p.Z-Start.Z)*n.Z));return new IntersectionResult{XYZPoint=q};}
  public XYZ Evaluate(double t,bool normalized){if(Kind=="line")return new XYZ(Start.X+(End.X-Start.X)*t,Start.Y+(End.Y-Start.Y)*t,Start.Z);var a=-HalfAngle+2*HalfAngle*t;return new XYZ(Radius*Math.Cos(a),Radius*Math.Sin(a),Start.Z);} }
 public class Arc:Curve { public static Arc Create(XYZ center,double radius,double start,double end,XYZ x,XYZ y){var h=(end-start)/2;return new Arc{Center=center,Radius=radius,HalfAngle=h,Kind="arc",Start=P(radius,-h,center.Z),End=P(radius,h,center.Z)};}
  public static Arc Create(XYZ p1,XYZ p2,XYZ mid){var r=mid.X;return new Arc{Center=new XYZ(0,0,p1.Z),Radius=r,HalfAngle=Math.Atan2(p2.Y,p2.X),Kind="arc",Start=p1,End=p2};} public static XYZ P(double r,double a,double z)=>new XYZ(r*Math.Cos(a),r*Math.Sin(a),z); }
 public class Line:Curve { public static Line CreateBound(XYZ a,XYZ b)=>new Line{Start=a,End=b,Kind="line"}; }
 public class CurveArray:IEnumerable<Curve> { public List<Curve> Items=new List<Curve>();public void Append(Curve c){Items.Add(c);}public IEnumerator<Curve> GetEnumerator()=>Items.GetEnumerator();IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator(); }
 public class CurveArrArray { public List<CurveArray> Items=new List<CurveArray>();public void Append(CurveArray a){Items.Add(a);} }
 public class SketchPlane { public static SketchPlane Create(Document d,ElementId id)=>new SketchPlane(); }
 public class Element { public ElementId Id;public Document Document;public string Name;public Element(Document d){Document=d;Id=new ElementId(d.NextId++);} }
 public class ModelCurve:Element { public Curve GeometryCurve;public ModelCurve(Document d,Curve c):base(d){GeometryCurve=c;c.Document=d;} }
 public class ReferencePlane:Element { public ReferencePlane(Document d,string n):base(d){Name=n;}public Reference GetReference()=>new Reference(null); }
 public class View:Element { public View(Document d):base(d){} } public class ViewPlan:View { public bool IsTemplate;public ViewType ViewType=ViewType.FloorPlan;public Level GenLevel;public ViewPlan(Document d):base(d){} }
 public class Level:Element { public double Elevation;public Level(Document d):base(d){} }
 public class ForgeTypeId { public string TypeId;public ForgeTypeId(string id){TypeId=id;}public bool NameEquals(ForgeTypeId o)=>TypeId==o?.TypeId; }
 public static class SpecTypeId { public static ForgeTypeId Length=new ForgeTypeId("length"),Angle=new ForgeTypeId("angle"); } public static class GroupTypeId { public static object Geometry=new object(); }
 public class Definition { public string Name;public ForgeTypeId Spec;public ForgeTypeId GetDataType()=>Spec; } public class ParameterSet { public int Size; }
 public class FamilyParameter:Element { public Definition Definition;public double Value;public bool IsInstance,IsReporting;public StorageType StorageType=StorageType.Double;public string Formula;public ParameterSet AssociatedParameters=new ParameterSet();public FamilyParameter(Document d,string n,ForgeTypeId s):base(d){Definition=new Definition{Name=n,Spec=s};} }
 public class Parameter { public bool IsReadOnly;public Definition Definition; }
 public class FamilyType { public string Name;public FamilyType(string n){Name=n;}public double? AsDouble(FamilyParameter p)=>p.Value; }
 public class FamilyTypeSet:IEnumerable<FamilyType> { public List<FamilyType> Items=new List<FamilyType>();public IEnumerator<FamilyType> GetEnumerator()=>Items.GetEnumerator();IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator(); }
 public class FamilyManager { public Document Document;public FamilyType CurrentType;public FamilyTypeSet Types=new FamilyTypeSet();public List<FamilyParameter> Parameters=new List<FamilyParameter>();public FamilyManager(Document d){Document=d;}
  public FamilyParameter[] GetParameters()=>Parameters.ToArray();public FamilyParameter AddParameter(string n,object g,ForgeTypeId s,bool i){var p=new FamilyParameter(Document,n,s);Parameters.Add(p);return p;}
  public FamilyType NewType(string n){CurrentType=new FamilyType(n);Types.Items.Add(CurrentType);return CurrentType;}public void Set(FamilyParameter p,double v){p.Value=v;}
  public void SetFormula(FamilyParameter p,string f){p.Formula=f;}
  public void AssociateElementParameterToFamilyParameter(Parameter p,FamilyParameter f){p.Definition=f.Definition;f.AssociatedParameters.Size++;}
  public FamilyParameter GetAssociatedFamilyParameter(Parameter p)=>p.Definition==null?null:Parameters.SingleOrDefault(x=>x.Definition.Name==p.Definition.Name); }
 public class Dimension:Element { public string Kind;public Reference Ref;public FamilyParameter FamilyLabel;public bool IsLocked;public int NumberOfSegments=>0;public Dimension(Document d,string k,Reference r):base(d){Kind=k;Ref=r;} }
 public class Solid:GeometryObject { public bool IsElementGeometry=true;public double Volume=1; } public class GeometryObject { }
 public class GeometryElement:IEnumerable<GeometryObject> { private Solid _solid; public GeometryElement(Solid s){_solid=s;}public GeometryElement():this(new Solid()){}public IEnumerator<GeometryObject> GetEnumerator(){yield return _solid;}IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator(); }
 public class Options { } public class BoundingBoxXYZ { public XYZ Min,Max; }
 public class Sketch { public List<ElementId> Ids=new List<ElementId>();public IEnumerable<ElementId> GetAllElements()=>Ids; }
 public class Extrusion:Element { public CurveArrArray Loops;public double Height;public Sketch Sketch;private Parameter End=new Parameter();public Extrusion(Document d,CurveArrArray loops,double h):base(d){Loops=loops;Height=h;Sketch=new Sketch();foreach(var c in loops.Items[0].Items){c.Document=d;var m=new ModelCurve(d,c);d.Items.Add(m);Sketch.Ids.Add(m.Id);}}
  public Parameter get_Parameter(BuiltInParameter p)=>End;public GeometryElement get_Geometry(Options o){var curves=Sketch.GetAllElements().Select(id=>((ModelCurve)Document.GetElement(id)).GeometryCurve).ToList();var arcs=curves.OfType<Arc>().ToList();if(arcs.Count==0)return new GeometryElement();var outer=arcs.OrderByDescending(a=>a.Radius).First();var inner=arcs.OrderBy(a=>a.Radius).First();var sweep=2*outer.HalfAngle;var areaFt2=sweep*0.5*(outer.Radius*outer.Radius-inner.Radius*inner.Radius);return new GeometryElement(new Solid{Volume=areaFt2*Height});}
  public BoundingBoxXYZ get_BoundingBox(object o){var curves=Sketch.GetAllElements().Select(id=>((ModelCurve)Document.GetElement(id)).GeometryCurve).ToList();var outer=curves.OfType<Arc>().OrderByDescending(a=>a.Radius).First();var inner=curves.OfType<Arc>().OrderBy(a=>a.Radius).First();var half=outer.HalfAngle;var minX=half>Math.PI/2?-outer.Radius:inner.Radius*Math.Cos(half);var x=outer.Radius-minX;var y=half>Math.PI/2?2*outer.Radius:2*outer.Radius*Math.Sin(half);return new BoundingBoxXYZ{Min=new XYZ(0,0,0),Max=new XYZ(x,y,Height)};} }
 public class FilteredElementCollector { private Document D;public FilteredElementCollector(Document d){D=d;}public FilteredElementCollector OfClass(Type t){return this;}public IEnumerable<T> Cast<T>()=>D.Items.OfType<T>(); }
 public class Transaction:IDisposable { private Document D;private TransactionStatus Status;public Transaction(Document d,string n){D=d;}public TransactionStatus Start(){return Status=TransactionStatus.Started;}public TransactionStatus Commit(){D.Commits++;return Status=TransactionStatus.Committed;}public TransactionStatus RollBack(){D.Rollbacks++;return Status=TransactionStatus.RolledBack;}public TransactionStatus GetStatus()=>Status;public void Dispose(){} }
 public class SaveAsOptions { public bool OverwriteExistingFile; }
 public class Document { public static string Scenario;public List<Element> Items=new List<Element>();public List<ModelCurve> References=new List<ModelCurve>();public FamilyManager FamilyManager;public Autodesk.Revit.Creation.FamilyItemFactory FamilyCreate;public int NextId=1,Commits,Rollbacks,Saves,Closes;public string Title="Segment";public Extrusion Extrusion;public bool IsFamilyDocument=true,IsValidObject=true;public Autodesk.Revit.UI.UIApplication OwnerApp;private FileStream Lock;public bool SaveLockReleasedOnClose;
  public Document(){FamilyManager=new FamilyManager(this);FamilyCreate=new Autodesk.Revit.Creation.FamilyItemFactory{Document=this};var l=new Level(this);Items.Add(l);Items.Add(new ViewPlan(this){GenLevel=l});Items.Add(new ReferencePlane(this,"Center (Left/Right)"));Items.Add(new ReferencePlane(this,"Center (Front/Back)"));if(Scenario=="existing_formula"){FamilyManager.CurrentType=new FamilyType("Standard");FamilyManager.Types.Items.Add(FamilyManager.CurrentType);FamilyManager.AddParameter("OuterDiameter",null,SpecTypeId.Length,false).Formula="1 ft";}}
  public void Regenerate(){var outer=FamilyManager.Parameters.Single(x=>x.Definition.Name=="OuterDiameter").Value/2;FamilyManager.Parameters.Single(x=>x.Definition.Name=="OuterRadius").Value=outer;FamilyManager.Parameters.Single(x=>x.Definition.Name=="InnerRadius").Value=outer-FamilyManager.Parameters.Single(x=>x.Definition.Name=="Thickness").Value;} public Element GetElement(ElementId id)=>Items.Single(e=>e.Id.Value==id.Value);
  public void Flex(){if(Scenario==null||!Scenario.StartsWith("flex_")||FlexApplied)return;FlexApplied=true;var p=Scenario=="flex_outer_diameter"?FamilyManager.Parameters.Single(x=>x.Definition.Name=="OuterDiameter"):Scenario=="flex_thickness"?FamilyManager.Parameters.Single(x=>x.Definition.Name=="Thickness"):Scenario=="flex_ring_width"?FamilyManager.Parameters.Single(x=>x.Definition.Name=="RingWidth"):FamilyManager.Parameters.Single(x=>x.Definition.Name=="SegmentAngle");
   FlexChanged=p.Definition.Name;if(p.Definition.Name=="OuterDiameter")p.Value=16000/304.8;else if(p.Definition.Name=="Thickness")p.Value=700/304.8;else if(p.Definition.Name=="RingWidth"){p.Value=2500/304.8;Extrusion.Height=p.Value;}else p.Value=45*Math.PI/180;
   var outer=FamilyManager.Parameters.Single(x=>x.Definition.Name=="OuterDiameter").Value/2;var thick=FamilyManager.Parameters.Single(x=>x.Definition.Name=="Thickness").Value;var half=FamilyManager.Parameters.Single(x=>x.Definition.Name=="SegmentAngle").Value/2;Regenerate();SetArc(References[0].GeometryCurve,outer,half);SetArc(References[1].GeometryCurve,outer-thick,half);
   var arcs=Extrusion.Sketch.GetAllElements().Select(id=>((ModelCurve)GetElement(id)).GeometryCurve).OfType<Arc>().ToArray();SetArc(arcs[0],outer,half);SetArc(arcs[1],outer-thick,half);
   var radial=References.Select(x=>x.GeometryCurve).OfType<Line>().ToArray();for(var i=0;i<2;i++){radial[i].Start=new XYZ(0,0,0);radial[i].End=Arc.P(outer,i==0?-half:half,0);}
   var lines=Extrusion.Sketch.GetAllElements().Select(id=>((ModelCurve)GetElement(id)).GeometryCurve).OfType<Line>().ToArray();var os=Arc.P(outer,-half,0);var oe=Arc.P(outer,half,0);var ie=Arc.P(outer-thick,half,0);var i0=Arc.P(outer-thick,-half,0);lines[0].Start=oe;lines[0].End=ie;lines[1].Start=i0;lines[1].End=os;}
  public bool FlexApplied;public string FlexChanged;private static void SetArc(Curve c,double r,double h){c.Radius=r;c.HalfAngle=h;c.Start=Arc.P(r,-h,0);c.End=Arc.P(r,h,0);c.Center=new XYZ(0,0,0);}
  public void SaveAs(string path,SaveAsOptions o){if(Scenario=="save_failure")throw new Exception("native save refused");Saves++;File.WriteAllText(path,"seam");Lock=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None);}
  public bool Close(bool save){Closes++;SaveLockReleasedOnClose=Lock!=null;Lock?.Dispose();Lock=null;return true;} }
}
namespace Autodesk.Revit.ApplicationServices { public class Application { public static int Opens;public Autodesk.Revit.DB.Document Last;public Autodesk.Revit.UI.UIApplication Owner;public List<Autodesk.Revit.DB.Document> Documents=new List<Autodesk.Revit.DB.Document>();public Autodesk.Revit.DB.Document NewFamilyDocument(string path){Opens++;if(Autodesk.Revit.DB.Document.Scenario=="template_failure")throw new Exception("bad template");Last=new Autodesk.Revit.DB.Document();Last.OwnerApp=Owner;Documents.Add(Last);return Last;} } }
namespace Autodesk.Revit.UI { public class UIDocument { public Autodesk.Revit.DB.Document Document; }public class UIApplication { public Autodesk.Revit.ApplicationServices.Application Application=new Autodesk.Revit.ApplicationServices.Application();public UIDocument ActiveUIDocument; public UIApplication(){Application.Owner=this;} } }
namespace RvtMcp.Plugin { public interface IRevitCommand {} public class CommandResult { public bool Success;public string Error;public object Data;public static CommandResult Ok(object d)=>new CommandResult{Success=true,Data=d};public static CommandResult Fail(string e)=>new CommandResult{Error=e}; } }
namespace RvtMcp.Plugin.Failures { public static class FailureControl { public class Scope { public void MarkPrivateFamilyDiscarded(){} }public static Scope Active=new Scope();public static IDisposable AuthorizeFamilySave(Autodesk.Revit.DB.Document d,string p){return null;}public static void ThrowIfStopped(){} } }
public static class Probe { public static string Run(string scenario){Autodesk.Revit.DB.Document.Scenario=scenario;Autodesk.Revit.ApplicationServices.Application.Opens=0;var dir=Path.Combine(Path.GetTempPath(),"rvtmcp-segment-probe-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);Autodesk.Revit.DB.Document doc=null;var temp=Path.Combine(dir,"result.rfa");try{
 var template=Path.Combine(dir,"template.rft");File.WriteAllText(template,"seam");if(scenario=="existing_output")File.WriteAllText(temp,"existing");var request=new JObject{["family_name"]="Jintang segment",["template_path"]=template,["output_path"]=temp,["outer_diameter_mm"]=14000,["thickness_mm"]=scenario=="invalid_thickness"?7000:600,["ring_width_mm"]=2000,["segment_angle_deg"]=scenario=="invalid_angle"?360:36};
 var app=new Autodesk.Revit.UI.UIApplication();if(scenario=="endpoint_reference_contract"){var arc=Autodesk.Revit.DB.Arc.Create(new Autodesk.Revit.DB.XYZ(0,0,0),10,-0.1,0.1,Autodesk.Revit.DB.XYZ.BasisX,Autodesk.Revit.DB.XYZ.BasisY);var accepted=arc.GetEndPointReference(0)!=null&&arc.GetEndPointReference(1)!=null;string parameter=null,error=null;try{arc.GetEndPointReference(2);}catch(ArgumentOutOfRangeException ex){parameter=ex.ParamName;error=ex.Message;}return new JObject{["start_and_end_accepted"]=accepted,["invalid_index_parameter"]=parameter,["invalid_index_error"]=error}.ToString();}var r=new RvtMcp.Plugin.Handlers.CreateSegmentFamilyHandler().Execute(app,request.ToString());doc=app.Application.Last;var arcs=doc?.Extrusion?.Sketch?.GetAllElements().Select(id=>((Autodesk.Revit.DB.ModelCurve)doc.GetElement(id)).GeometryCurve).OfType<Autodesk.Revit.DB.Arc>().ToArray();var bbox=doc?.Extrusion?.get_BoundingBox(null);doc?.Flex();
 return new JObject{["success"]=r.Success,["error"]=r.Error,["data"]=r.Data==null?null:JObject.FromObject(r.Data),["document_opens"]=Autodesk.Revit.ApplicationServices.Application.Opens,["document_closes"]=doc?.Closes??0,["transaction_commits"]=doc?.Commits??0,["output_exists"]=File.Exists(temp),["temporary_files"]=new JArray(Directory.GetFiles(dir,".rvtmcp-*.rfa")),["temporary_path_exists_after_move"]=Directory.GetFiles(dir,".rvtmcp-*.rfa").Any(),["save_lock_released_on_close"]=doc?.SaveLockReleasedOnClose??false,["profile_curves"]=doc?.Extrusion?.Sketch?.GetAllElements().Count()??0,["profile_arcs"]=arcs?.Length??0,["profile_lines"]=doc?.Extrusion?.Sketch?.GetAllElements().Select(id=>((Autodesk.Revit.DB.ModelCurve)doc.GetElement(id)).GeometryCurve).OfType<Autodesk.Revit.DB.Line>().Count()??0,["outer_radius_mm"]=arcs?.OrderByDescending(a=>a.Radius).First().Radius*304.8??0,["inner_radius_mm"]=arcs?.OrderBy(a=>a.Radius).First().Radius*304.8??0,["flex_applied"]=doc?.FlexApplied??false,["flex_changed_parameter"]=doc?.FlexChanged,["flex_geometry_changed"]=doc?.Extrusion==null?false:scenario=="flex_outer_diameter"?doc.Extrusion.get_BoundingBox(null).Max.X>bbox.Max.X:scenario=="flex_thickness"?Math.Abs(doc.Extrusion.Sketch.GetAllElements().Select(id=>((Autodesk.Revit.DB.ModelCurve)doc.GetElement(id)).GeometryCurve).OfType<Autodesk.Revit.DB.Arc>().Min(a=>a.Radius)*304.8-6300)<0.01:scenario=="flex_ring_width"?doc.Extrusion.Height*304.8>2400:doc.Extrusion.get_BoundingBox(null).Max.Y>bbox.Max.Y}.ToString();}finally{try{Directory.Delete(dir,true);}catch{}} } }
""";
}
