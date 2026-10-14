using System.Numerics;
namespace CS2Review.Core;
public readonly record struct SpatialTriangle(Vector3 A,Vector3 B,Vector3 C);
public sealed class SpatialMesh
{
 private readonly SpatialTriangle[] triangles;
 private readonly Node? root;
 private sealed record Node(Vector3 Min,Vector3 Max,Node? Left,Node? Right,int[]? Indices);
 public int TriangleCount=>triangles.Length;
 public SpatialMesh(IEnumerable<SpatialTriangle> triangles)
 {
  this.triangles=triangles.ToArray();
  if(this.triangles.Length>2_000_000||this.triangles.Any(t=>!SpatialMath.Finite(t.A)||!SpatialMath.Finite(t.B)||!SpatialMath.Finite(t.C)))throw new InvalidDataException("Invalid static mesh");
  root=this.triangles.Length==0?null:Build(Enumerable.Range(0,this.triangles.Length).ToArray());
 }
 private Node Build(int[] ids)
 {
  var min=new Vector3(float.PositiveInfinity);var max=new Vector3(float.NegativeInfinity);
  foreach(var i in ids){var t=triangles[i];min=Vector3.Min(min,Vector3.Min(t.A,Vector3.Min(t.B,t.C)));max=Vector3.Max(max,Vector3.Max(t.A,Vector3.Max(t.B,t.C)));}
  if(ids.Length<=8)return new(min,max,null,null,ids);
  var span=max-min;var axis=span.X>=span.Y&&span.X>=span.Z?0:span.Y>=span.Z?1:2;
  Array.Sort(ids,(a,b)=>Coordinate((triangles[a].A+triangles[a].B+triangles[a].C)/3,axis).CompareTo(Coordinate((triangles[b].A+triangles[b].B+triangles[b].C)/3,axis)));
  var mid=ids.Length/2;return new(min,max,Build(ids[..mid]),Build(ids[mid..]),null);
 }
 private static float Coordinate(Vector3 v,int axis)=>axis==0?v.X:axis==1?v.Y:v.Z;
 public bool TraceStaticSegment(Vector3 start,Vector3 end)
 {
  if(!SpatialMath.Finite(start)||!SpatialMath.Finite(end)||Vector3.DistanceSquared(start,end)<1e-12f)throw new ArgumentException("Finite distinct segment endpoints required");
  return root is not null&&Trace(root,start,end-start);
 }
 private bool Trace(Node n,Vector3 origin,Vector3 direction)
 {
  double low=0,high=1;
  for(var a=0;a<3;a++){var d=Coordinate(direction,a);var o=Coordinate(origin,a);if(Math.Abs(d)<1e-12){if(o<Coordinate(n.Min,a)||o>Coordinate(n.Max,a))return false;continue;}var x=(Coordinate(n.Min,a)-o)/(double)d;var y=(Coordinate(n.Max,a)-o)/(double)d;if(x>y)(x,y)=(y,x);low=Math.Max(low,x);high=Math.Min(high,y);if(low>high)return false;}
  if(n.Indices is not null)return n.Indices.Any(i=>Intersects(triangles[i],origin,direction));
  return Trace(n.Left!,origin,direction)||Trace(n.Right!,origin,direction);
 }
 private static bool Intersects(SpatialTriangle t,Vector3 o,Vector3 d)
 {
  var edge1=t.B-t.A;var edge2=t.C-t.A;var h=Vector3.Cross(d,edge2);var det=(double)Vector3.Dot(edge1,h);if(Math.Abs(det)<1e-9)return false;
  var s=o-t.A;var u=Vector3.Dot(s,h)/det;if(u < -1e-7||u > 1+1e-7)return false;var q=Vector3.Cross(s,edge1);var v=Vector3.Dot(d,q)/det;if(v < -1e-7||u+v > 1+1e-7)return false;var distance=Vector3.Dot(edge2,q)/det;return distance>=0&&distance<=1;
 }
 public static SpatialMesh ReadAwmh(Stream source)
 {
  return new(ReadTriangles(source,out _));
 }
 public static (int Vertices,int Triangles) ValidateAwmh(Stream source)
 {
  var list=ReadTriangles(source,out var vertices);return(vertices,list.Length);
 }
 private static SpatialTriangle[] ReadTriangles(Stream source,out int vertexCount)
 {
  using var r=new BinaryReader(source,System.Text.Encoding.UTF8,true);
  if(!r.ReadBytes(4).AsSpan().SequenceEqual("AWMH"u8)||r.ReadUInt32()!=1)throw new InvalidDataException("Unsupported AWMH");
  var vc=r.ReadUInt32();var tc=r.ReadUInt32();if(vc>6_000_000||tc>2_000_000)throw new InvalidDataException("Mesh exceeds limits");vertexCount=(int)vc;
  if(source.CanSeek&&source.Length-source.Position!=checked((long)vc*12+(long)tc*12))throw new InvalidDataException("Invalid mesh length");
  var vertices=new Vector3[vc];for(var i=0;i<vc;i++){vertices[i]=new(r.ReadSingle(),r.ReadSingle(),r.ReadSingle());if(!SpatialMath.Finite(vertices[i]))throw new InvalidDataException("Nonfinite vertex");}
  var list=new SpatialTriangle[tc];for(var i=0;i<tc;i++){var a=r.ReadUInt32();var b=r.ReadUInt32();var c=r.ReadUInt32();if(a>=vc||b>=vc||c>=vc)throw new InvalidDataException("Invalid vertex index");list[i]=new(vertices[a],vertices[b],vertices[c]);}return list;
 }
}
public static class SpatialMath
{
 internal static bool Finite(Vector3 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z);
 public static bool IsWithinFov(Vector3 eye,Vector3 target,double yaw,double pitch,double fov)
 {
  if(!Finite(eye)||!Finite(target)||!double.IsFinite(yaw)||!double.IsFinite(pitch)||!double.IsFinite(fov)||fov<=0||fov>180||Vector3.DistanceSquared(eye,target)<1e-12f)throw new ArgumentException("Invalid FOV input");
  var y=yaw*Math.PI/180;var p=pitch*Math.PI/180;var forward=new Vector3((float)(Math.Cos(p)*Math.Cos(y)),(float)(Math.Cos(p)*Math.Sin(y)),(float)-Math.Sin(p));
  return Vector3.Dot(forward,Vector3.Normalize(target-eye))>=Math.Cos(fov*Math.PI/360)-1e-7;
 }
}
