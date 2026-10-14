using System.Text.RegularExpressions;
using Microsoft.Win32;
namespace CS2Review.Core;
public static class MapInstallLocator
{
 // Only Steam's declared library roots are inspected; there is no drive scan.
 public static IReadOnlyList<string> FindInstalls(IEnumerable<string>? steamRoots=null)
 {
  var roots=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  if(steamRoots is not null)foreach(var root in steamRoots)roots.Add(root);
  else{
   if(OperatingSystem.IsWindows())foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32}){
    try{using var key=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,view).OpenSubKey("SOFTWARE\\Valve\\Steam");if(key?.GetValue("InstallPath") is string path)roots.Add(path);}catch(Exception e)when(e is UnauthorizedAccessException or System.Security.SecurityException or IOException){}
   }
   var standard=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);if(standard.Length>0)roots.Add(Path.Combine(standard,"Steam"));
  }
  var libraries=new HashSet<string>(roots,StringComparer.OrdinalIgnoreCase);
  foreach(var root in roots)foreach(var relative in new[]{Path.Combine("steamapps","libraryfolders.vdf"),Path.Combine("config","libraryfolders.vdf")}){
   try{var file=Path.Combine(root,relative);if(!File.Exists(file)||new FileInfo(file).Length>1024*1024)continue;foreach(Match entry in Regex.Matches(File.ReadAllText(file),"\"path\"\\s*\"((?:\\\\.|[^\"\\\\])*)\""))if(libraries.Count<32)libraries.Add(entry.Groups[1].Value.Replace("\\\\","\\"));}catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException){}
  }
  var found=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  foreach(var library in libraries){try{
   var manifest=Path.Combine(library,"steamapps","appmanifest_730.acf");if(!File.Exists(manifest)||new FileInfo(manifest).Length>1024*1024)continue;var name=Regex.Match(File.ReadAllText(manifest),"\"installdir\"\\s*\"([^\"]+)\"").Groups[1].Value;
   if(name.Length==0||name is "." or ".."||name.IndexOfAny(['/', '\\', ':'])>=0)continue;
   var install=Path.GetFullPath(Path.Combine(library,"steamapps","common",name));if(File.Exists(Path.Combine(install,"game","csgo","steam.inf")))found.Add(install);
  }catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException){}}
  return found.Order(StringComparer.OrdinalIgnoreCase).ToArray();
 }
}
