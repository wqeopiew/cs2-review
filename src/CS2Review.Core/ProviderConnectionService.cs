using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CS2Review.Core;

public enum ConnectionProbeStatus { Success,AuthenticationFailed,PermissionDenied,AddressOrModelUnavailable,RateLimited,UnsupportedRequest,ServiceFailed,NetworkFailed,TimedOut,InvalidResponse }
public sealed record ConnectionProbeResult(ConnectionProbeStatus Status, bool StructuredOutputSupported, int? HttpStatus, double DurationSeconds, DateTimeOffset CheckedAt);
public sealed class ProviderConnectionService(HttpClient? httpClient=null)
{
    private static readonly HttpClient SharedClient=new(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=Timeout.InfiniteTimeSpan};
    private readonly HttpClient client=httpClient??SharedClient;
    public async Task<ConnectionProbeResult> TestAsync(ReportOptions options,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();ReportService.ValidateOptions(options,true);
        var watch=Stopwatch.StartNew();
        ConnectionProbeResult Result(ConnectionProbeStatus status,int? code=null)=>new(status,status==ConnectionProbeStatus.Success,code,watch.Elapsed.TotalSeconds,DateTimeOffset.UtcNow);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(options.Timeout<TimeSpan.FromSeconds(30)?options.Timeout:TimeSpan.FromSeconds(30));
        var requestBody=new Dictionary<string,object>{["model"]=options.Model,["temperature"]=0,["max_tokens"]=128,["response_format"]=new{type="json_object"},["messages"]=new[]{new{role="user",content="连接测试。仅返回 JSON 对象 {\"ok\":true}。"}}};
        if(options.EnableThinking is bool thinking)requestBody["enable_thinking"]=thinking;
        var body=JsonSerializer.Serialize(requestBody,DataJson.Options);
        using var request=new HttpRequestMessage(HttpMethod.Post,options.Endpoint){Content=new StringContent(body,Encoding.UTF8,"application/json")};
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",options.ApiKey);
        try
        {
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token).ConfigureAwait(false);
            var code=(int)response.StatusCode;
            if(!response.IsSuccessStatusCode) return Result(code switch{401=>ConnectionProbeStatus.AuthenticationFailed,403=>ConnectionProbeStatus.PermissionDenied,404=>ConnectionProbeStatus.AddressOrModelUnavailable,429=>ConnectionProbeStatus.RateLimited,400 or 422=>ConnectionProbeStatus.UnsupportedRequest,_=>ConnectionProbeStatus.ServiceFailed},code);
            await using var stream=await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer=new MemoryStream();var bytes=new byte[2048];int read;
            while((read=await stream.ReadAsync(bytes,timeout.Token).ConfigureAwait(false))>0){if(buffer.Length+read>16384)return Result(ConnectionProbeStatus.InvalidResponse,code);buffer.Write(bytes,0,read);}
            using var envelope=JsonDocument.Parse(buffer.ToArray());
            var content=envelope.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            using var result=JsonDocument.Parse(content??"");
            return Result(result.RootElement.ValueKind==JsonValueKind.Object && result.RootElement.TryGetProperty("ok",out var ok)&&ok.ValueKind==JsonValueKind.True?ConnectionProbeStatus.Success:ConnectionProbeStatus.InvalidResponse,code);
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){return Result(ConnectionProbeStatus.TimedOut);}
        catch(HttpRequestException){return Result(ConnectionProbeStatus.NetworkFailed);}
        catch(Exception ex) when(ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException){return Result(ConnectionProbeStatus.InvalidResponse);}
    }
}
