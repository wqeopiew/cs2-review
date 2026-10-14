using System.Numerics;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
namespace CS2Review.Core;
public sealed class MapOverview
{
 public double PosX{get;set;} public double PosY{get;set;} public double Scale{get;set;} public int ImageSize{get;set;}
 public int Rotate{get;set;}
 public Vector2 Project(double x,double y)
 {
  if(!double.IsFinite(x)||!double.IsFinite(y)||!double.IsFinite(PosX)||!double.IsFinite(PosY)||!double.IsFinite(Scale)||Scale<=0||ImageSize<=0)throw new ArgumentException("Invalid overview coordinates");
  // The overview scale is Hammer units per pixel of the 1024-pixel source radar.
  return new((float)((x-PosX)/Scale*ImageSize/1024),(float)((PosY-y)/Scale*ImageSize/1024));
 }
}
public sealed class MapResource
{
 public int SchemaVersion{get;set;}=1;
 public string MapName{get;set;}="";
 public string? BuildId{get;set;} public string? ClientVersion{get;set;} public string? SourceRevision{get;set;}
 public string SourceMapVpkSha256{get;set;}="";public string OverviewSha256{get;set;}="";public string SourceTextureSha256{get;set;}="";
 public string ImageSha256{get;set;}="";public string PhysSha256{get;set;}="";public string SourcePhysicsResourceSha256{get;set;}="";public string GeometrySha256{get;set;}="";
 public string ResourceDirectory{get;set;}="";public MapOverview Overview{get;set;}=new();
 public int VertexCount{get;set;} public int TriangleCount{get;set;}
 public string ToolVersion{get;set;}="20.0";public string ToolSha256{get;set;}="";
 public string GeometryPolicy{get;set;}="all-recorded-collision-no-decimation-v1";
 public string MatchStatus=>"unknown";
 public List<string> Warnings{get;set;}=[];
 [JsonIgnore] public string ImagePath{get;internal set;}="";
 [JsonIgnore] public string GeometryPath{get;internal set;}="";
 [JsonIgnore] public string ManifestPath{get;internal set;}="";
}
public sealed class MapResourceService(string toolPath)
{
 private const string PinnedToolSha256="a4bb4f9945db985efb2cad90a90d06867de5ba7e0447a2a4a69a07dace781028";
 private static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
 private static readonly HashSet<string> AllowedMaps=["de_mirage","de_dust2"];
 private static void MapName(string mapName){if(!AllowedMaps.Contains(mapName))throw new ArgumentException("Unsupported map name",nameof(mapName));}
 private static string Safe(string root,params string[] children)
 {
  root=Path.GetFullPath(root);var current=root;EnsureNotLink(current);
  foreach(var c in children){if(c.Length==0||c is "." or ".."||c.IndexOfAny([Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar,':'])>=0)throw new InvalidDataException("Unsafe resource path");current=Path.Combine(current,c);EnsureNotLink(current);}return current;
 }
 private static void EnsureNotLink(string path)
 {
  // Check every existing ancestor, including a reparse point supplied as the root.
  for(var p=Path.GetFullPath(path);p is not null;p=Path.GetDirectoryName(p))if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked map resource paths are unsupported");
 }
 private static bool HashValid(string? value)=>value is {Length:64}&&value.All(Uri.IsHexDigit);
 private static string HashFile(string path){using var f=File.OpenRead(path);return Convert.ToHexStringLower(SHA256.HashData(f));}
 private static async Task<string> HashFileAsync(string path,CancellationToken ct){await using var f=File.OpenRead(path);return Convert.ToHexStringLower(await SHA256.HashDataAsync(f,ct));}
 public MapResource? Find(string mapName,string outputDir)
 {
  MapName(mapName);
  try{
   var manifest=Safe(outputDir,mapName,"manifest.json");if(!File.Exists(manifest))return null;if(new FileInfo(manifest).Length>64*1024)throw new InvalidDataException("Manifest too large");
   var r=JsonSerializer.Deserialize<MapResource>(File.ReadAllText(manifest),Json)??throw new InvalidDataException("Invalid manifest");
   if(r.SchemaVersion!=1||r.MapName!=mapName||!Regex.IsMatch(r.ResourceDirectory,"^assets-[a-f0-9]{24}$"))throw new InvalidDataException("Invalid resource identity");
   var folder=Safe(outputDir,mapName,r.ResourceDirectory);r.ImagePath=Safe(folder,"radar.png");r.GeometryPath=Safe(folder,"geometry.mesh");r.ManifestPath=manifest;
   foreach(var item in new[]{(r.ImagePath,r.ImageSha256),(r.GeometryPath,r.GeometrySha256),(Safe(folder,"overview.txt"),r.OverviewSha256),(Safe(folder,"world_physics.vmdl_c"),r.SourcePhysicsResourceSha256),(Safe(folder,"source_texture.vtex_c"),r.SourceTextureSha256),(Safe(folder,"physics.bin"),r.PhysSha256)})
    if(!HashValid(item.Item2)||!File.Exists(item.Item1)||new FileInfo(item.Item1).Length>256*1024*1024||HashFile(item.Item1)!=item.Item2)throw new InvalidDataException("Map resource hash mismatch");
   var size=PngSize(r.ImagePath);var sourceOverview=ReadOverview(Safe(folder,"overview.txt"),size);
   if(r.Overview is null||r.Overview.ImageSize!=sourceOverview.ImageSize||r.Overview.PosX!=sourceOverview.PosX||r.Overview.PosY!=sourceOverview.PosY||r.Overview.Scale!=sourceOverview.Scale||r.Overview.Rotate!=sourceOverview.Rotate)throw new InvalidDataException("Overview metadata differs from hashed source");
   using var geometry=File.OpenRead(r.GeometryPath);var counts=SpatialMesh.ValidateAwmh(geometry);if(counts.Vertices!=r.VertexCount||counts.Triangles!=r.TriangleCount||counts.Triangles<=0)throw new InvalidDataException("Geometry metadata mismatch");
   return r;
  }catch(Exception e)when(e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException){return null;}
 }
 public async Task<MapResource> ImportFromGameAsync(string mapName,string installDir,string outputDir,CancellationToken ct)
 {
  MapName(mapName);ct.ThrowIfCancellationRequested();
  var game=Path.GetFullPath(Path.Combine(installDir,"game","csgo"));var output=Path.GetFullPath(outputDir);EnsureNotLink(output);
  if(output.Equals(Path.GetFullPath(installDir),StringComparison.OrdinalIgnoreCase)||output.StartsWith(Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Output must be outside game installation");
  var pak=Path.Combine(game,"pak01_dir.vpk");var map=Path.Combine(game,"maps",mapName+".vpk");
  if(!File.Exists(pak)||!File.Exists(map)||!File.Exists(Path.Combine(game,"steam.inf")))throw new DirectoryNotFoundException("CS2 installation or map missing");
  if(!File.Exists(toolPath))throw new FileNotFoundException("Source2Viewer CLI missing",toolPath);
  var toolSha256=await HashFileAsync(toolPath,ct);if(toolSha256!=PinnedToolSha256)throw new InvalidDataException("Unsupported Source2Viewer CLI; expected official Windows x64 20.0");
  var mapRoot=Safe(output,mapName);Directory.CreateDirectory(mapRoot);var stage=Safe(mapRoot,"staging-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
  string? createdDestination=null;var published=false;
  try{
   await Run(stage,ct,"-i",pak,"-f",$"panorama/images/overheadmaps/{mapName}_radar_psd.vtex_c","-o",Path.Combine(stage,"radar.png"),"-d");
   await Run(stage,ct,"-i",pak,"-f",$"panorama/images/overheadmaps/{mapName}_radar_psd.vtex_c","-o",Path.Combine(stage,"source_texture.vtex_c"));
   await Run(stage,ct,"-i",pak,"-f",$"resource/overviews/{mapName}.txt","-o",Path.Combine(stage,"overview.txt"));
   await Run(stage,ct,"-i",map,"-f",$"maps/{mapName}/world_physics.vmdl_c","-o",Path.Combine(stage,"world_physics.vmdl_c"));
   await Run(stage,ct,"-i",map,"-f",$"maps/{mapName}/world_physics.vmdl_c","-o",Path.Combine(stage,"geometry.glb"),"-d","--gltf_export_format","glb","--gltf_export_materials");
   var physical=Path.Combine(stage,"world_physics.vmdl_c");ExtractPhysics(physical,Path.Combine(stage,"physics.bin"));
   var (vertices,triangles)=SpatialSource2Glb.ConvertToAwmh(Path.Combine(stage,"geometry_physics.glb"),Path.Combine(stage,"geometry.mesh"),ct);
   var inf=File.ReadAllLines(Path.Combine(game,"steam.inf")).Select(x=>x.Split('=',2)).Where(x=>x.Length==2).ToDictionary(x=>x[0],x=>x[1]);
   var r=new MapResource{MapName=mapName,ClientVersion=inf.GetValueOrDefault("ClientVersion"),SourceRevision=inf.GetValueOrDefault("SourceRevision"),BuildId=ReadBuildId(installDir),Overview=ReadOverview(Path.Combine(stage,"overview.txt"),PngSize(Path.Combine(stage,"radar.png"))),VertexCount=vertices,TriangleCount=triangles};
   r.SourceMapVpkSha256=await HashFileAsync(map,ct);r.ImageSha256=await HashFileAsync(Path.Combine(stage,"radar.png"),ct);r.GeometrySha256=await HashFileAsync(Path.Combine(stage,"geometry.mesh"),ct);r.PhysSha256=await HashFileAsync(Path.Combine(stage,"physics.bin"),ct);r.SourcePhysicsResourceSha256=await HashFileAsync(physical,ct);r.OverviewSha256=await HashFileAsync(Path.Combine(stage,"overview.txt"),ct);r.SourceTextureSha256=await HashFileAsync(Path.Combine(stage,"source_texture.vtex_c"),ct);r.ToolSha256=toolSha256;
   r.Warnings=["地图与录像版本尚未确认，不能据此认定真实视线。","静态几何包含记录的全部碰撞材质，未建模烟雾、动态门、透明材质和玩家视点。"];
   var identity=string.Join("|",r.SourceMapVpkSha256,r.ImageSha256,r.GeometrySha256,r.PhysSha256,r.SourcePhysicsResourceSha256,r.OverviewSha256,r.SourceTextureSha256,r.ToolSha256);
   r.ResourceDirectory="assets-"+Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)))[..24];ct.ThrowIfCancellationRequested();var destination=Safe(mapRoot,r.ResourceDirectory);
   if(!Directory.Exists(destination)){Directory.Move(stage,destination);createdDestination=destination;}
   else foreach(var file in new[]{"radar.png","geometry.mesh","overview.txt","world_physics.vmdl_c","source_texture.vtex_c","physics.bin"})if(HashFile(Safe(destination,file))!=HashFile(Path.Combine(stage,file)))throw new InvalidDataException("Existing resource directory is damaged");
   var manifest=Safe(mapRoot,"manifest.json");var temporary=Safe(mapRoot,"manifest-"+Guid.NewGuid().ToString("N")+".tmp");
   try{await File.WriteAllTextAsync(temporary,JsonSerializer.Serialize(r,Json),ct);ct.ThrowIfCancellationRequested();File.Move(temporary,manifest,true);published=true;}finally{if(File.Exists(temporary))File.Delete(temporary);}
   return Find(mapName,output)??throw new InvalidDataException("Imported map failed integrity check");
  }finally{if(Directory.Exists(stage)){EnsureNotLink(stage);Directory.Delete(stage,true);}if(!published&&createdDestination is not null&&Directory.Exists(createdDestination)){EnsureNotLink(createdDestination);Directory.Delete(createdDestination,true);}}
 }
 private async Task Run(string stage,CancellationToken ct,params string[] args)
 {
  var start=new ProcessStartInfo(Path.GetFullPath(toolPath)){WorkingDirectory=stage,UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};foreach(var arg in args)start.ArgumentList.Add(arg);
  using var process=Process.Start(start)??throw new IOException("Could not start map exporter");var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromMinutes(2));
  try{await process.WaitForExitAsync(timeout.Token);}catch(OperationCanceledException){if(!process.HasExited)process.Kill(true);await process.WaitForExitAsync();if(!ct.IsCancellationRequested)throw new TimeoutException("Map exporter timed out");throw;}
  var errors=await stderr;await stdout;if(process.ExitCode!=0)throw new IOException($"Map exporter failed ({process.ExitCode}): {errors[..Math.Min(errors.Length,2000)]}");
 }
 private static int PngSize(string path)
 {
  using var f=File.OpenRead(path);Span<byte> header=stackalloc byte[24];f.ReadExactly(header);if(!header[..8].SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))throw new InvalidDataException("Invalid PNG");var width=BinaryPrimitives.ReadInt32BigEndian(header[16..]);var height=BinaryPrimitives.ReadInt32BigEndian(header[20..]);if(width!=height||width<=0||width>8192)throw new InvalidDataException("Unsupported radar dimensions");return width;
 }
 private static MapOverview ReadOverview(string path,int size)
 {
  var text=File.ReadAllText(path);double Value(string key){var matches=Regex.Matches(text,"\""+Regex.Escape(key)+"\"\\s*\"([^\"]+)\"");if(matches.Count!=1||!double.TryParse(matches[0].Groups[1].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out var value)||!double.IsFinite(value))throw new InvalidDataException("Missing/invalid overview "+key);return value;}
  var rotate=Value("rotate");if(rotate is not (0 or 1))throw new InvalidDataException("Invalid overview rotate flag");
  var o=new MapOverview{PosX=Value("pos_x"),PosY=Value("pos_y"),Scale=Value("scale"),ImageSize=size,Rotate=(int)rotate};if(o.Scale<=0)throw new InvalidDataException("Invalid overview scale");return o;
 }
 private static string? ReadBuildId(string installDir)
 {
  var common=Directory.GetParent(Path.GetFullPath(installDir));var steamapps=common?.Parent;if(steamapps is null)return null;var acf=Path.Combine(steamapps.FullName,"appmanifest_730.acf");if(!File.Exists(acf))return null;return Regex.Match(File.ReadAllText(acf),"\"buildid\"\\s*\"([0-9]+)\"").Groups[1].Value is {Length:>0} value?value:null;
 }
 private static void ExtractPhysics(string path,string output)
 {
  var data=File.ReadAllBytes(path);if(data.Length<16||data.Length>256*1024*1024)throw new InvalidDataException("Invalid source physics resource");var table=checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8))+8);var count=BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));if(count>64)throw new InvalidDataException("Invalid resource blocks");byte[]? payload=null;
  for(var i=0;i<count;i++){var at=checked(table+(int)i*12);if(at<0||at+12>data.Length)throw new InvalidDataException("Invalid resource block table");if(!data.AsSpan(at,4).SequenceEqual("PHYS"u8))continue;if(payload is not null)throw new InvalidDataException("Duplicate PHYS");var offset=checked(at+4+(int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at+4)));var length=checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at+8)));if(offset<0||length<=0||offset>data.Length-length)throw new InvalidDataException("Invalid PHYS range");payload=data.AsSpan(offset,length).ToArray();}if(payload is null)throw new InvalidDataException("PHYS missing");File.WriteAllBytes(output,payload);
 }
}
