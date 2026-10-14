using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
namespace CS2Review.Core;
public static class SpatialSource2Glb
{
 // Only the pinned Source2Viewer physics export is accepted. Its POSITION data remains
 // Hammer/Z-up; the node matrix converts that data for glTF display, not for demo coordinates.
 public static (int Vertices,int Triangles) ConvertToAwmh(string input,string output,CancellationToken ct)
 {
  ct.ThrowIfCancellationRequested();if(new FileInfo(input).Length>256*1024*1024)throw new InvalidDataException("GLB exceeds limit");var data=File.ReadAllBytes(input);
  uint U32(int at){if(at<0||at>data.Length-4)throw new InvalidDataException("Truncated GLB");return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));}
  if(data.Length<20||!data.AsSpan(0,4).SequenceEqual("glTF"u8)||U32(4)!=2||U32(8)!=data.Length)throw new InvalidDataException("Invalid GLB header");
  JsonDocument? document=null;int binaryStart=-1,binaryLength=0;
  try{
   for(var at=12;at<data.Length;){var length=checked((int)U32(at));var kind=U32(at+4);at=checked(at+8);if(length<0||length%4!=0||at>data.Length-length)throw new InvalidDataException("Invalid GLB chunk");
    if(kind==0x4e4f534a){if(document is not null)throw new InvalidDataException("Duplicate GLB JSON");document=JsonDocument.Parse(data.AsMemory(at,length));}
    else if(kind==0x004e4942){if(binaryStart!=-1)throw new InvalidDataException("Duplicate GLB BIN");binaryStart=at;binaryLength=length;}
    else throw new InvalidDataException("Unsupported GLB chunk");at+=length;
   }
   if(document is null||binaryStart<0)throw new InvalidDataException("GLB chunks missing");var root=document.RootElement;
   var buffers=root.GetProperty("buffers");if(buffers.GetArrayLength()!=1||buffers[0].TryGetProperty("uri",out _)||buffers[0].GetProperty("byteLength").GetInt32()>binaryLength)throw new InvalidDataException("External/invalid GLB buffer");
   var meshes=root.GetProperty("meshes");var nodes=root.GetProperty("nodes");var seen=new HashSet<int>();
   double[] expected=[0,0,.0254,0,.0254,0,0,0,0,.0254,0,0,0,0,0,1];
   foreach(var node in nodes.EnumerateArray()){
    if(node.TryGetProperty("children",out _)||node.TryGetProperty("skin",out _)||node.TryGetProperty("translation",out _)||node.TryGetProperty("rotation",out _)||node.TryGetProperty("scale",out _))throw new InvalidDataException("Unsupported physics node transform");
    if(!node.TryGetProperty("mesh",out var meshIndex))throw new InvalidDataException("Physics node without mesh");var index=meshIndex.GetInt32();if(index<0||index>=meshes.GetArrayLength()||!seen.Add(index))throw new InvalidDataException("Invalid/reused physics mesh");
    var matrix=node.GetProperty("matrix");if(matrix.GetArrayLength()!=16)throw new InvalidDataException("Physics display transform missing");for(var i=0;i<16;i++)if(!double.IsFinite(matrix[i].GetDouble())||Math.Abs(matrix[i].GetDouble()-expected[i])>1e-6)throw new InvalidDataException("Unknown physics coordinate frame");
   }
   if(seen.Count!=meshes.GetArrayLength())throw new InvalidDataException("Unreferenced physics mesh");
   var vertices=new List<Vector3>();var indices=new List<uint>();var lookup=new Dictionary<Vector3,uint>();
   var accessors=root.GetProperty("accessors");var views=root.GetProperty("bufferViews");
   (int Base,int Count,int Stride,int Component) Accessor(int index,string type){
    if(index<0||index>=accessors.GetArrayLength())throw new InvalidDataException("Invalid accessor");var a=accessors[index];if(a.TryGetProperty("sparse",out _)||a.TryGetProperty("normalized",out var normalized)&&normalized.GetBoolean()||a.GetProperty("type").GetString()!=type)throw new InvalidDataException("Unsupported accessor");
    var vi=a.GetProperty("bufferView").GetInt32();if(vi<0||vi>=views.GetArrayLength())throw new InvalidDataException("Invalid buffer view");var v=views[vi];if(v.GetProperty("buffer").GetInt32()!=0)throw new InvalidDataException("Invalid buffer reference");
    var count=a.GetProperty("count").GetInt32();if(count<0||count>6_000_000)throw new InvalidDataException("Accessor exceeds limit");var component=a.GetProperty("componentType").GetInt32();var width=type=="VEC3"&&component==5126?12:type=="SCALAR"&&component==5125?4:type=="SCALAR"&&component==5123?2:throw new InvalidDataException("Unsupported component type");
    var vo=v.TryGetProperty("byteOffset",out var vOffset)?vOffset.GetInt32():0;var ao=a.TryGetProperty("byteOffset",out var aOffset)?aOffset.GetInt32():0;var length=v.GetProperty("byteLength").GetInt32();var stride=v.TryGetProperty("byteStride",out var s)?s.GetInt32():width;
    if(vo<0||ao<0||length<0||stride<width||vo>binaryLength-length||ao>length||count>0&&(long)(count-1)*stride+width>length-ao)throw new InvalidDataException("Accessor outside buffer");return (checked(binaryStart+vo+ao),count,stride,component);
   }
   foreach(var index in seen.Order())foreach(var primitive in meshes[index].GetProperty("primitives").EnumerateArray()){
    ct.ThrowIfCancellationRequested();if(primitive.TryGetProperty("mode",out var mode)&&mode.GetInt32()!=4||primitive.TryGetProperty("targets",out _))throw new InvalidDataException("Unsupported primitive");
    var position=Accessor(primitive.GetProperty("attributes").GetProperty("POSITION").GetInt32(),"VEC3");var points=new Vector3[position.Count];
    for(var j=0;j<position.Count;j++){var at=position.Base+j*position.Stride;points[j]=new(BitConverter.ToSingle(data,at),BitConverter.ToSingle(data,at+4),BitConverter.ToSingle(data,at+8));if(!SpatialMath.Finite(points[j]))throw new InvalidDataException("Nonfinite GLB vertex");}
    var indexed=primitive.TryGetProperty("indices",out var idx);var source=indexed?Accessor(idx.GetInt32(),"SCALAR"):(0,position.Count,0,0);if(source.Item2%3!=0||indices.Count+(long)source.Item2>6_000_000)throw new InvalidDataException("Invalid triangle count");
    for(var j=0;j<source.Item2;j++){
     if((j&4095)==0)ct.ThrowIfCancellationRequested();uint v=!indexed?(uint)j:source.Item4==5125?U32(source.Item1+j*source.Item3):BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(source.Item1+j*source.Item3));
     if(v>=points.Length)throw new InvalidDataException("GLB index outside positions");var point=points[v];if(!lookup.TryGetValue(point,out var welded)){welded=(uint)vertices.Count;lookup.Add(point,welded);vertices.Add(point);}indices.Add(welded);
    }
   }
   if(indices.Count==0)throw new InvalidDataException("Physics export is empty");ct.ThrowIfCancellationRequested();
   using var writer=new BinaryWriter(File.Create(output));writer.Write("AWMH"u8);writer.Write(1);writer.Write(vertices.Count);writer.Write(indices.Count/3);foreach(var v in vertices){writer.Write(v.X);writer.Write(v.Y);writer.Write(v.Z);}foreach(var i in indices)writer.Write(i);return(vertices.Count,indices.Count/3);
  }catch(Exception e)when(e is KeyNotFoundException or InvalidOperationException or OverflowException or ArgumentOutOfRangeException or JsonException){throw new InvalidDataException("Malformed Source2 physics GLB",e);}
  finally{document?.Dispose();}
 }
}
