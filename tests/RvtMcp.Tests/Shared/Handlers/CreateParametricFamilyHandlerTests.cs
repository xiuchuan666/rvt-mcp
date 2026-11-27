using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests;

/// <summary>
/// Compiles the unchanged REVIT2027 handler against a test-only API seam. These tests prove
/// preflight, transaction ownership, close-without-save and DTO control flow, not native Revit
/// constraints. Creation and flexing a real .rfa remain a Revit 2027 hosted E2E requirement.
/// </summary>
public sealed class CreateParametricFamilyHandlerTests
{
    private static readonly Lazy<Func<string, JObject>> Probe = new(() => HandlerProbe.Compile(Seam,
        new[] { "src/shared/Handlers/Families/CreateParametricFamilyHandler.cs",
            "src/shared/Handlers/Families/ParametricFamilyInput.cs", "src/shared/Infrastructure/RevitCompat.cs" },
        symbols: new[] { "REVIT2027" }));

    [Fact]
    public void Creates_type_parameters_associates_geometry_and_returns_readback()
    {
        var result = Probe.Value("success");
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
        Assert.Equal(3, result.Value<int>("labelled_dimensions_and_extrusion_associations"));
        Assert.Equal(4, result.Value<int>("locked_profile_alignments"));
        Assert.Equal(1200, data["geometry"]!["bounding_box_mm"]!.Value<double>("length"), 6);
        Assert.Equal(600, data["geometry"]!["bounding_box_mm"]!.Value<double>("width"), 6);
        Assert.Equal(900, data["geometry"]!["bounding_box_mm"]!.Value<double>("height"), 6);
        Assert.True(data["geometry"]!.Value<bool>("dimensions_match"));
        Assert.Equal(1, data["geometry"]!.Value<int>("solid_count"));
        Assert.Equal(4, data["geometry"]!.Value<int>("reference_plane_count"));
        Assert.Equal(2, data["geometry"]!.Value<int>("labelled_dimension_count"));
        Assert.Equal(4, data["geometry"]!.Value<int>("locked_alignment_count"));
        Assert.Equal(1, result.Value<int>("transaction_commits"));
        Assert.Equal(1, result.Value<int>("document_closes"));
    }

    [Theory]
    [InlineData("geometry_failure")]
    [InlineData("template_failure")]
    public void Failure_never_saves_a_partial_family_and_closes_any_owned_document(string scenario)
    {
        var result = Probe.Value(scenario);
        Assert.False(result.Value<bool>("success"));
        Assert.Contains("closed without saving to output_path", result.Value<string>("error"));
        Assert.Equal(0, result.Value<int>("saves"));
        Assert.False(result.Value<bool>("output_exists"));
        if (scenario == "geometry_failure") Assert.Equal(1, result.Value<int>("transaction_rollbacks"));
        Assert.Equal(scenario == "template_failure" ? 0 : 1, result.Value<int>("document_closes"));
        Assert.Empty(result["temporary_files"]!);
    }

    [Fact]
    public void Save_failure_closes_without_retrying_or_overwriting()
    {
        var result = Probe.Value("save_failure");
        Assert.False(result.Value<bool>("success"));
        Assert.Equal(1, result.Value<int>("saves"));
        Assert.Equal(1, result.Value<int>("document_closes"));
        Assert.False(result.Value<bool>("output_exists"));
        Assert.Empty(result["temporary_files"]!);
    }

    [Fact]
    public void Invalid_parameters_do_not_open_a_revit_document()
    {
        var result = Probe.Value("invalid_dimensions");
        Assert.False(result.Value<bool>("success"));
        Assert.Contains("length_mm", result.Value<string>("error"));
        Assert.Equal(0, result.Value<int>("document_opens"));
    }

    private const string Seam = """
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
namespace Autodesk.Revit.DB {
 public enum TransactionStatus { Started,Committed,RolledBack }
 public enum ViewType { FloorPlan }
 public enum BuiltInParameter { EXTRUSION_END_PARAM }
 public class ElementId { public int IntegerValue; public ElementId(int id){IntegerValue=id;} }
 public class XYZ { public double X,Y,Z; public XYZ(double x,double y,double z){X=x;Y=y;Z=z;}
  public static XYZ BasisZ=new XYZ(0,0,1); public XYZ Add(XYZ o)=>new XYZ(X+o.X,Y+o.Y,Z+o.Z); public XYZ Multiply(double n)=>new XYZ(X*n,Y*n,Z*n); }
 public abstract class Curve { public Reference Reference=new Reference(); public XYZ A,B;
  public XYZ Direction=>new XYZ(B.X-A.X,B.Y-A.Y,B.Z-A.Z); public XYZ GetEndPoint(int i)=>i==0?A:B; }
 public class Line:Curve { public static Line CreateBound(XYZ a,XYZ b)=>new Line{A=a,B=b}; }
 public class CurveArray:IEnumerable<Curve> { public List<Curve> Items=new List<Curve>(); public void Append(Curve c){Items.Add(c);}
  public IEnumerator<Curve> GetEnumerator()=>Items.GetEnumerator(); IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator(); }
 public class CurveArrArray:IEnumerable<CurveArray> { public List<CurveArray> Items=new List<CurveArray>(); public void Append(CurveArray c){Items.Add(c);}
  public IEnumerator<CurveArray> GetEnumerator()=>Items.GetEnumerator(); IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator(); }
 public static class GroupTypeId { public static object Geometry=new object(); }
 public static class SpecTypeId { public static object Length=new object(); }
 public class Reference { }
 public class ReferenceArray { public List<Reference> Items=new List<Reference>(); public void Append(Reference r){Items.Add(r);} }
 public class Element { public ElementId Id=new ElementId(10); public string Name; }
 public class Level:Element { }
 public class ReferencePlane:Element { public Reference GetReference()=>new Reference(); }
 public class ViewPlan { public bool IsTemplate; public ViewType ViewType=ViewType.FloorPlan; }
 public class SketchPlane { public static SketchPlane Create(Document doc,ElementId id)=>new SketchPlane(); }
 public class Sketch { public CurveArrArray Profile=new CurveArrArray(); }
 public class BoundingBoxXYZ { public XYZ Min,Max; }
 public class Parameter { public bool IsReadOnly; }
 public class ParameterSet:IEnumerable<Parameter> { public List<Parameter> Items=new List<Parameter>();
  public IEnumerator<Parameter> GetEnumerator()=>Items.GetEnumerator(); IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator(); }
 public class FamilyParameter:Element { public double Value; public ParameterSet AssociatedParameters=new ParameterSet(); }
 public class Solid { public double Volume=1; }
 public class GeometryElement:IEnumerable<Solid> { public IEnumerator<Solid> GetEnumerator(){yield return new Solid();} IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator(); }
 public class Options { }
 public class Extrusion:Element { public Sketch Sketch=new Sketch(); public double Height; public Parameter End=new Parameter();
  public Parameter get_Parameter(BuiltInParameter p)=>End;
  public BoundingBoxXYZ get_BoundingBox(object o){ if(Document.FailGeometry)return null;
   var lines=Sketch.Profile.Items[0].Items; return new BoundingBoxXYZ{Min=new XYZ(lines.Min(l=>l.A.X),lines.Min(l=>l.A.Y),0),Max=new XYZ(lines.Max(l=>l.A.X),lines.Max(l=>l.A.Y),Height)}; }
  public GeometryElement get_Geometry(Options o)=>new GeometryElement(); }
 public class FamilyType { public double? AsDouble(FamilyParameter p)=>p.Value; }
 public class FamilyManager { public FamilyType CurrentType=new FamilyType(); public static int Associations;
  public FamilyParameter AddParameter(string n,object g,object s,bool i)=>new FamilyParameter{Name=n};
  public FamilyType NewType(string n)=>new FamilyType(); public void Set(FamilyParameter p,double v){p.Value=v;}
  public void AssociateElementParameterToFamilyParameter(Parameter p,FamilyParameter f){f.AssociatedParameters.Items.Add(p);Associations++;} }
 public class Dimension { public FamilyParameter FamilyLabel; public bool IsLocked=true; }
 public class FamilyItemFactory { public Document Doc;
  public Extrusion NewExtrusion(bool solid,CurveArrArray loops,SketchPlane plane,double height){var e=new Extrusion{Height=height,Sketch=new Sketch{Profile=loops}};Doc.Extrusion=e;return e;}
  public ReferencePlane NewReferencePlane(XYZ a,XYZ b,XYZ c,ViewPlan v){var p=new ReferencePlane();Doc.Items.Add(p);return p;}
  public Dimension NewDimension(ViewPlan v,Line l,ReferenceArray r){FamilyManager.Associations++;return new Dimension();}
  public Dimension NewAlignment(ViewPlan v,Reference a,Reference b){Document.LockedAlignments++;return new Dimension();} }
 public class FilteredElementCollector { private Document Doc; public FilteredElementCollector(Document d){Doc=d;}
  public FilteredElementCollector OfClass(Type t)=>this; public IEnumerable<T> Cast<T>()=>Doc.Items.OfType<T>(); }
 public class Transaction:IDisposable { private Document Doc; private TransactionStatus Status;
  public Transaction(Document d,string n){Doc=d;} public TransactionStatus Start(){Doc.Transactions++;return Status=TransactionStatus.Started;}
  public TransactionStatus Commit(){Doc.Commits++;return Status=TransactionStatus.Committed;}
  public TransactionStatus RollBack(){Doc.Rollbacks++;return Status=TransactionStatus.RolledBack;}
  public TransactionStatus GetStatus()=>Status; public void Dispose(){} }
 public class SaveAsOptions { public bool OverwriteExistingFile; }
 public class Document { public List<object> Items=new List<object>(); public FamilyManager FamilyManager=new FamilyManager(); public FamilyItemFactory FamilyCreate;
  public static string Scenario; public static int LockedAlignments; public static bool FailGeometry=>Scenario=="geometry_failure";
  public int Transactions,Commits,Rollbacks,Saves,Closes; public Extrusion Extrusion; public string Title="Cabinet";
  public Document(){Items.Add(new Level());Items.Add(new ViewPlan());FamilyCreate=new FamilyItemFactory{Doc=this};}
  public bool IsFamilyDocument=>true; public void Regenerate(){} public void SaveAs(string path,SaveAsOptions o){Saves++;if(Scenario=="save_failure")File.WriteAllText(path,"partial");if(Scenario=="save_failure")throw new Exception("save refused");File.WriteAllText(path,"seam");}
  public void Close(bool save){Closes++;} }
}
namespace Autodesk.Revit.ApplicationServices { public class Application { public static int Opens; public Autodesk.Revit.DB.Document Last;
  public Autodesk.Revit.DB.Document NewFamilyDocument(string path){Opens++;if(Autodesk.Revit.DB.Document.Scenario=="template_failure")throw new Exception("bad template");return Last=new Autodesk.Revit.DB.Document();} } }
namespace Autodesk.Revit.UI { public class UIApplication { public Autodesk.Revit.ApplicationServices.Application Application=new Autodesk.Revit.ApplicationServices.Application(); } }
namespace RvtMcp.Plugin { public interface IRevitCommand {} public class CommandResult { public bool Success; public string Error; public object Data;
 public static CommandResult Ok(object data)=>new CommandResult{Success=true,Data=data}; public static CommandResult Fail(string error)=>new CommandResult{Error=error}; } }
public static class Probe { public static string Run(string scenario){
 Autodesk.Revit.DB.Document.Scenario=scenario; Autodesk.Revit.DB.Document.LockedAlignments=0; Autodesk.Revit.DB.FamilyManager.Associations=0; Autodesk.Revit.ApplicationServices.Application.Opens=0;
 var dir=Path.Combine(Path.GetTempPath(),"rvtmcp-family-probe"); Directory.CreateDirectory(dir); var template=Path.Combine(dir,"template.rft");File.WriteAllText(template,"seam"); var output=Path.Combine(dir,"result.rfa");if(File.Exists(output))File.Delete(output);
 var request=new JObject{["family_name"]="Cabinet",["template_path"]=template,["output_path"]=output,["length_mm"]=scenario=="invalid_dimensions"?0:1200,["width_mm"]=600,["height_mm"]=900};
 var app=new Autodesk.Revit.UI.UIApplication(); var r=new RvtMcp.Plugin.Handlers.CreateParametricFamilyHandler().Execute(app,request.ToString()); var doc=app.Application.Last;
 return new JObject{["success"]=r.Success,["error"]=r.Error,["data"]=r.Data==null?null:JObject.FromObject(r.Data),["document_opens"]=Autodesk.Revit.ApplicationServices.Application.Opens,
  ["document_closes"]=doc?.Closes??0,["saves"]=doc?.Saves??0,["output_exists"]=File.Exists(output),["transaction_commits"]=doc?.Commits??0,["transaction_rollbacks"]=doc?.Rollbacks??0,["temporary_files"]=new JArray(Directory.GetFiles(dir,".rvtmcp-*.rfa")),
  ["labelled_dimensions_and_extrusion_associations"]=Autodesk.Revit.DB.FamilyManager.Associations,["locked_profile_alignments"]=Autodesk.Revit.DB.Document.LockedAlignments}.ToString(); } }
""";
}
