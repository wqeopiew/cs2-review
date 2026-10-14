using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
namespace CS2Review.Core;
public sealed class MapResourceProvenance
{
 public string MapName{get;}
 public string SourceDemoSha256{get;}
 public string GeometrySha256{get;}
 public string ResourceDirectory{get;}
 public string Status=>"user-asserted";
 public bool AllowsVerifiedVisibility=>false;
 [JsonConstructor]
 public MapResourceProvenance(string mapName,string sourceDemoSha256,string geometrySha256,string resourceDirectory)
 {
  if(mapName is not ("de_mirage" or "de_dust2")||!Hash(sourceDemoSha256)||!Hash(geometrySha256)||!Regex.IsMatch(resourceDirectory,"^assets-[a-f0-9]{24}$"))throw new ArgumentException("Invalid map provenance identity");
  MapName=mapName;SourceDemoSha256=sourceDemoSha256.ToLowerInvariant();GeometrySha256=geometrySha256.ToLowerInvariant();ResourceDirectory=resourceDirectory;
 }
 private static bool Hash(string s)=>s is {Length:64}&&s.All(Uri.IsHexDigit);
 // Call only following an explicit user assertion. Serialization provides a provenance
 // sidecar; neither the parser nor a same-name/build guess can create verified evidence.
 public static MapResourceProvenance UserAsserted(MapResource resource,string sourceDemoSha256)=>new(resource.MapName,sourceDemoSha256,resource.GeometrySha256,resource.ResourceDirectory);
 public bool IsBoundTo(MapResource resource,string sourceDemoSha256)=>MapName==resource.MapName&&string.Equals(SourceDemoSha256,sourceDemoSha256,StringComparison.OrdinalIgnoreCase)&&string.Equals(GeometrySha256,resource.GeometrySha256,StringComparison.OrdinalIgnoreCase)&&ResourceDirectory==resource.ResourceDirectory;
}
