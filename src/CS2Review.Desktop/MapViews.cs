using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using CS2Review.Core;
using Microsoft.Win32;

namespace CS2Review.Desktop;

public sealed partial class MainWindow
{
    private MapResource? _mapResource;
    private BitmapImage? _mapBitmap;
    private int _mapRequestVersion;
    private string MapDirectory=>System.IO.Path.Combine(_directory,"maps");
    private MapResourceService MapService=>new(System.IO.Path.Combine(AppContext.BaseDirectory,"map-tools","Source2Viewer-CLI.exe"));
    private static bool SupportedMap(string name)=>name is "de_mirage" or "de_dust2";
    private async Task RestoreMapAsync()
    {
        var match=_match;if(match is null)return;var request=++_mapRequestVersion;_mapResource=null;_mapBitmap=null;
        if(!SupportedMap(match.MapName))return;
        try
        {
            var resource=await Task.Run(()=>MapService.Find(match.MapName,MapDirectory));
            var bitmap=resource is null?null:ReadMapBitmap(resource);
            if(!_disposed&&_mapRequestVersion==request&&_match?.Id==match.Id){_mapResource=resource;_mapBitmap=bitmap;if(_section=="回合复盘"||_section=="AI 设置")RenderPage();}
        }
        catch { if(_mapRequestVersion==request&&_match?.Id==match.Id){_mapResource=null;_mapBitmap=null;} }
    }
    private async Task ImportMapAsync()
    {
        if(_match is null||_operation is not null)return;
        var match=_match;if(!SupportedMap(match.MapName)){_status.Text="当前支持读取 Mirage 与 Dust2，其他地图继续显示坐标轨迹。";return;}
        var request=++_mapRequestVersion;
        var installation=FindGameInstallation();
        if(installation is null)
        {
            var folder=new OpenFolderDialog{Title="选择 CS2 安装目录（Counter-Strike Global Offensive）"};
            if(folder.ShowDialog(this)!=true)return;installation=folder.FolderName;
        }
        using var cancellation=new CancellationTokenSource();_operation=cancellation;SetBusy(true);
        try
        {
            _status.Text="正在读取本机地图图像与碰撞资源，游戏文件保持原样…";
            var resource=await MapService.ImportFromGameAsync(match.MapName,installation,MapDirectory,cancellation.Token);
            var bitmap=ReadMapBitmap(resource);
            if(!_disposed&&_mapRequestVersion==request&&_match?.Id==match.Id){_mapResource=resource;_mapBitmap=bitmap;}
            _status.Text="本机地图已缓存。Demo 地图版本尚未匹配，地图用于位置参考。";
        }
        catch(OperationCanceledException){_status.Text="地图读取已取消。";}
        catch(Exception){_status.Text="地图读取失败，请检查 CS2 安装目录和随软件提供的地图工具。";}
        finally{CompleteOperation();}
    }
    private static string? FindGameInstallation()=>MapInstallLocator.FindInstalls().FirstOrDefault();
    private void RenderMapTrace(StackPanel panel,Canvas canvas,int round)
    {
        panel.Children.Add(Text(_mapResource is null?"轨迹与朝向 · 坐标示意":"轨迹与朝向 · 本机地图参考",18,Ink,FontWeights.SemiBold));
        panel.Children.Add(Text(_mapResource is null?"尚未读取本机地图。坐标示意按本回合范围缩放，不能判断视线或安全路线。":"图像来自本机 CS2；Demo 地图版本未知，可能存在地图差异。显示位置参考，暂不判断真实视线、掩体或预瞄。",12,Brush("#E3BF72")));
        if(_match is not null&&SupportedMap(_match.MapName))
        {
            var button=Button(_mapResource is null?"读取本机地图":"刷新本机地图",async(_,_)=>await ImportMapAsync(),false);button.IsEnabled=_operation is null;button.HorizontalAlignment=HorizontalAlignment.Left;panel.Children.Add(button);
        }
        if(_mapResource is not null)panel.Children.Add(Text($"地图 {_mapResource.MapName} · 本机 Build {_mapResource.BuildId??"未知"} · 碰撞资源 {_mapResource.TriangleCount:N0} 个三角面 · 与 Demo 匹配状态：未知",11,Muted));
        canvas.Height=_mapResource is null?310:500;canvas.SizeChanged+=(_,_)=>DrawTrace(canvas,round);panel.Children.Add(canvas);
        var samples=_match?.Positions.Count(p=>p.Round==round&&p.PlayerId==_analysis?.PlayerId)??0;
        panel.Children.Add(Text(samples>0?$"当前玩家 {samples} 个采样点 · 2Hz 采样 · {(_mapResource is null?"XY 按本回合范围缩放":"按本机 overview 坐标变换显示 XY")} · 朝向取自记录 yaw；上下层位置需结合 Z 值复核。":"此回合没有当前玩家的轨迹采样。",11,Muted));
    }
    private bool DrawMapTrace(Canvas canvas,List<PositionSample> points)
    {
        var resource=_mapResource;if(resource is null||_mapBitmap is null||_match?.MapName!=resource.MapName)return false;
        var size=Math.Min(canvas.ActualWidth,canvas.ActualHeight);var left=(canvas.ActualWidth-size)/2;var top=(canvas.ActualHeight-size)/2;
        var image=new Image{Source=_mapBitmap,Width=size,Height=size,Stretch=Stretch.Fill,Opacity=.85};Canvas.SetLeft(image,left);Canvas.SetTop(image,top);canvas.Children.Add(image);
        Point Project(PositionSample p){var pixel=resource.Overview.Project(p.X,p.Y);return new(left+pixel.X/resource.Overview.ImageSize*size,top+pixel.Y/resource.Overview.ImageSize*size);}
        if(points.Count==0)return true;
        // Break a trace on gaps/death rather than suggesting an unobserved route.
        var segment=new PointCollection();
        void Finish(){if(segment.Count>1)canvas.Children.Add(new Polyline{Stroke=Accent,StrokeThickness=2,Points=segment});segment=new();}
        PositionSample? previous=null;
        foreach(var sample in points)
        {
            if(previous is not null&&(sample.Time-previous.Time>1.1||sample.Tick<previous.Tick||sample.Health<=0||previous.Health<=0))Finish();
            if(sample.Health>0)segment.Add(Project(sample));previous=sample;
        }
        Finish();
        foreach(var sample in points.Where((_,i)=>i%Math.Max(1,points.Count/30)==0))
        {
            var p=Project(sample);var angle=sample.Yaw*Math.PI/180;
            canvas.Children.Add(new Line{X1=p.X,Y1=p.Y,X2=p.X+12*Math.Cos(angle),Y2=p.Y-12*Math.Sin(angle),Stroke=Brush("#F0FAF8"),StrokeThickness=2,ToolTip=$"{sample.Time:0.00}s · Tick {sample.Tick}\nX {sample.X:0.0} Y {sample.Y:0.0} Z {sample.Z:0.0}\n朝向 {sample.Yaw:0.0}° · HP {sample.Health}"});
        }
        var start=Project(points[0]);var marker=new Ellipse{Width=9,Height=9,Fill=Accent,ToolTip="记录起点"};Canvas.SetLeft(marker,start.X-4.5);Canvas.SetTop(marker,start.Y-4.5);canvas.Children.Add(marker);
        return true;
    }
    private static BitmapImage ReadMapBitmap(MapResource resource)
    {
        var bytes=File.ReadAllBytes(resource.ImagePath);
        if(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))!=resource.ImageSha256)throw new InvalidDataException("地图图像校验失败。");
        using var stream=new MemoryStream(bytes);
        var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();return bitmap;
    }
    internal async Task RestoreSmokeMapAsync(string directory)
    {
        if(_match is null||!SupportedMap(_match.MapName))throw new InvalidOperationException("Map smoke requires supported match.");
        ++_mapRequestVersion;
        var resource=await Task.Run(()=>MapService.Find(_match.MapName,directory))??throw new InvalidDataException("Map smoke resource missing or invalid.");
        _mapResource=resource;_mapBitmap=ReadMapBitmap(resource);RenderPage();
    }
}
