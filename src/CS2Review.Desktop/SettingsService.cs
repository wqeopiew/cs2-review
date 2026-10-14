using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CS2Review.Desktop;

public sealed class AppSettings
{
    public string Provider { get; set; } = "Qwen";
    public string BaseUrl { get; set; } = "https://dashscope.aliyuncs.com/compatible-mode/v1";
    public string Model { get; set; } = "qwen-plus";
    public int TimeoutSeconds { get; set; } = 60;
    public int InputBudget { get; set; } = 24000;
    public string? ScoringModelId { get; set; }
}

public sealed class SettingsService(string directory, bool credentialAccess = true)
{
    private const string CredentialTarget = "CS2Review/AI/API-Key";
    public AppSettings Load()
    {
        var path = Path.Combine(directory, "settings.json");
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new();
    }
    public void Save(AppSettings settings, string? newKey)
    {
        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("API 地址必须是无内嵌凭据的 HTTPS 地址。");
        if (string.IsNullOrWhiteSpace(settings.Model)) throw new InvalidOperationException("请输入模型名称。");
        if (newKey is not null) { if(!credentialAccess) throw new InvalidOperationException("测试模式不能保存凭据。"); SaveKey(settings.BaseUrl,newKey); }
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static string Target(string baseUrl)
    {
        if(!Uri.TryCreate(baseUrl,UriKind.Absolute,out var uri) || uri.Scheme!="https" || uri.UserInfo.Length>0 || uri.Query.Length>0 || uri.Fragment.Length>0)
            throw new InvalidOperationException("API 地址必须是无内嵌凭据的 HTTPS 地址。");
        var origin=uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        return CredentialTarget+"/"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(origin))).ToLowerInvariant();
    }
    public bool HasKeyFor(string baseUrl)
    {
        if(!credentialAccess) return false;
        try
        {
            if(!CredRead(Target(baseUrl),1,0,out var pointer)) return false;
            try { return Marshal.PtrToStructure<Credential>(pointer).CredentialBlobSize>0; }
            finally { CredFree(pointer); }
        }
        catch(InvalidOperationException){return false;}
    }
    public string? ReadKey(string baseUrl)
    {
        if (!credentialAccess) return null;
        if (!CredRead(Target(baseUrl), 1, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally { CredFree(pointer); }
    }
    public void DeleteKey(string baseUrl) { if(credentialAccess) CredDelete(Target(baseUrl),1,0); }
    private static void SaveKey(string baseUrl,string key)
    {
        var bytes = Encoding.Unicode.GetBytes(key);
        if (bytes.Length > 2560) throw new InvalidOperationException("API 密钥过长。");
        var pointer = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var credential = new Credential { Type = 1, TargetName = Target(baseUrl), CredentialBlobSize = (uint)bytes.Length, CredentialBlob = pointer, Persist = 2, UserName = "CS2Review" };
            if (!CredWrite(ref credential, 0)) throw new InvalidOperationException("Windows 凭据管理器无法保存密钥。");
        }
        finally
        {
            for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(pointer, i, 0);
            Array.Clear(bytes);
            Marshal.FreeCoTaskMem(pointer);
        }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr credential);
}
