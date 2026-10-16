using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using System.Text.Json;
var art=ExternalPaths.Require(ExternalResourceKind.Artwork);
var core=StaticCore.Load(IgcReader.ParseFile(Path.Combine(art,"PCore001.igc")));
float[] V(Vector3 v)=>[v.X,v.Y,v.Z];
var result = new {
 civilizations = core.Civilizations.Values.Select(c=> new { id=c.CivilizationId,c.Name,
  station=c.InitialStationTypeId, model=core.StationTypes[c.InitialStationTypeId].ModelName,
  pod=c.Lifepod, podModel=core.HullTypes[c.Lifepod].ModelName, podLength=core.HullTypes[c.Lifepod].Length,
  podCapabilities=core.HullTypes[c.Lifepod].HabmCapabilities }).ToArray(),
 stations=core.StationTypes.Values.Select(t=>{
  var m=CvhReader.Parse(File.ReadAllText(Path.Combine(art,t.ModelName+".cvh")));
  var b=HullLandingBay.ReconstructStation(m,t.Radius);
  return new { id=t.StationTypeId,t.Name,t.ModelName,t.Radius,t.SabmCapabilities,modelRadius=m.Radius,b.Scale,b.CapLandSlots,
   rawFrames=m.Frames.Where(f=>f.Name.StartsWith("garage")||f.Name.StartsWith("capgarage")||f.Name.StartsWith("launch")).Select(f=>new{f.Name,position=V(f.Position),forward=V(f.Forward)}).ToArray(),
   launch=b.LaunchSlots.Select(s=>new{position=V(s.Position),direction=V(s.Direction)}).ToArray(),
   land=b.LandSlots.Select(s=>s.Planes.Select(p=>new{position=V(p.Position),direction=V(p.Direction)}).ToArray()).ToArray() };
 }).ToArray()
};
Console.WriteLine(JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
