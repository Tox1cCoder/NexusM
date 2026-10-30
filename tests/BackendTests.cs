using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NexusM;
class MockServer : HttpMessageHandler {
 public byte[] Bytes=new byte[200000];
 public int Mode;
 public long RequestedOffset;
 public string IfRange;
 public bool SawApiKey;
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t) {
  RequestedOffset=r.Headers.Range==null?0:r.Headers.Range.Ranges.GetEnumerator().MoveNext()?First(r):0;
  IfRange=r.Headers.IfRange==null?"":r.Headers.IfRange.ToString();
  SawApiKey=r.Headers.Contains("apikey");
  long offset=Mode==2?0:RequestedOffset;
  int count=Bytes.Length-(int)offset;
  var response=new HttpResponseMessage(offset>0?HttpStatusCode.PartialContent:HttpStatusCode.OK);
  response.Content=new ByteArrayContent(Bytes,(int)offset,count);
  if(Mode==4)response.Content.Headers.ContentType=new MediaTypeHeaderValue("text/html");
  response.Headers.ETag=new EntityTagHeaderValue("\"test-file\"");
  if(Mode==5)response.Headers.ETag=new EntityTagHeaderValue("\"changed-file\"");
  if(offset>0) response.Content.Headers.ContentRange=new ContentRangeHeaderValue(Mode==3?offset+1:offset,Bytes.Length-1,Bytes.Length);
  return Task.FromResult(response);
 }
 static long First(HttpRequestMessage r) { foreach(var range in r.Headers.Range.Ranges)return range.From.Value; return 0; }
}
class BackendTests {
 static int passed;
 static void Check(bool value,string name){if(!value)throw new Exception(name);Console.WriteLine("PASS "+name);passed++;}
 static void Reject(string uri){try{DownloadRequest.ParseNxm(uri,"test");throw new Exception("Accepted "+uri);}catch(ArgumentException){passed++;}}
 static void Main(){Run().GetAwaiter().GetResult();}
 static async Task Run(){
  var req=DownloadRequest.ParseNxm("nxm://BaldursGate3/mods/123/files/456?key=abc%2Bdef&expires=1900000000&user_id=7","test");
  Check(req.Domain=="baldursgate3"&&req.ModId==123&&req.FileId==456&&req.Key=="abc+def"&&req.UserId==7,"NXM parsing and encoded key");
  Reject("https://baldursgate3/mods/123/files/456");Reject("nxm://baldursgate3/mods/0/files/456");Reject("nxm://baldursgate3/collections/123");Reject("nxm://baldursgate3/mods/123/files/456?expires=bad");
  Check(DownloadService.IsNexusDownloadUri(new Uri("https://cf-files.nexusmods.com/file.zip")),"Known Nexus CDN accepted");
  Check(!DownloadService.IsNexusDownloadUri(new Uri("https://nexusmods.com.evil.test/file.zip"))&&!DownloadService.IsNexusDownloadUri(new Uri("http://cf-files.nexusmods.com/file.zip"))&&!DownloadService.IsNexusDownloadUri(new Uri("https://user:pass@cf-files.nexusmods.com/file.zip")),"CDN lookalike, HTTP and credentials rejected");
  string folder=Path.Combine(Path.GetTempPath(),"nexusm-tests-"+Guid.NewGuid());Directory.CreateDirectory(folder);
  try {
   using(var api=new NexusApi("dummy-test-key"))using(var service=new DownloadService(api,null)){
    var handler=new MockServer();for(int i=0;i<handler.Bytes.Length;i++)handler.Bytes[i]=(byte)(i%251);
    var clientField=typeof(DownloadService).GetField("client",BindingFlags.NonPublic|BindingFlags.Instance);
    ((HttpClient)clientField.GetValue(service)).Dispose();clientField.SetValue(service,new HttpClient(handler));
    var transfer=typeof(DownloadService).GetMethod("TransferAsync",BindingFlags.NonPublic|BindingFlags.Instance);
    string partial=Path.Combine(folder,"file.part");var state=new Dictionary<string,object>{{"id","test:1:2"}};
    await (Task)transfer.Invoke(service,new object[]{new Uri("https://cf-files.nexusmods.com/file.zip?key=synthetic-signature"),partial,state,CancellationToken.None});
    Check(new FileInfo(partial).Length==handler.Bytes.Length&&JsonTools.Long(state,"downloadedBytes")==handler.Bytes.Length,"Full transfer exact byte count");
    Check(!handler.SawApiKey&&!File.ReadAllText(partial+".json").Contains("synthetic-signature"),"CDN requests and resume sidecars exclude API and signed-link credentials");
    using(var f=new FileStream(partial,FileMode.Open))f.SetLength(64000);
    await (Task)transfer.Invoke(service,new object[]{new Uri("https://cf-files.nexusmods.com/file.zip"),partial,state,CancellationToken.None});
    byte[] resumed=File.ReadAllBytes(partial);bool equal=resumed.Length==handler.Bytes.Length;for(int i=0;equal&&i<resumed.Length;i++)equal=resumed[i]==handler.Bytes[i];
    Check(handler.RequestedOffset==64000&&handler.IfRange=="\"test-file\""&&equal,"Range resume preserves archive bytes using If-Range");
    using(var f=new FileStream(partial,FileMode.Open))f.SetLength(32000);handler.Mode=2;
    await (Task)transfer.Invoke(service,new object[]{new Uri("https://cf-files.nexusmods.com/file.zip"),partial,state,CancellationToken.None});
    Check(new FileInfo(partial).Length==handler.Bytes.Length,"Server ignoring Range restarts rather than appends");
    using(var f=new FileStream(partial,FileMode.Open))f.SetLength(16000);handler.Mode=3;bool rangeRejected=false;
    try{await (Task)transfer.Invoke(service,new object[]{new Uri("https://cf-files.nexusmods.com/file.zip"),partial,state,CancellationToken.None});}catch(IOException){rangeRejected=true;}
    Check(rangeRejected&&new FileInfo(partial).Length==16000,"Invalid Content-Range rejected before write");
    handler.Mode=4;bool htmlRejected=false;
    try{await (Task)transfer.Invoke(service,new object[]{new Uri("https://cf-files.nexusmods.com/file.zip"),partial,state,CancellationToken.None});}catch(IOException){htmlRejected=true;}
    Check(htmlRejected&&new FileInfo(partial).Length==16000,"HTML challenge page rejected before archive write");
    handler.Mode=5;bool changedRejected=false;
    try{await (Task)transfer.Invoke(service,new object[]{new Uri("https://cf-files.nexusmods.com/file.zip"),partial,state,CancellationToken.None});}catch(IOException){changedRejected=true;}
    Check(changedRejected&&new FileInfo(partial).Length==0&&!File.Exists(partial+".json"),"Changed strong validator in a 206 response resets the unsafe partial before appending");
    handler.Mode=0;
    await (Task)transfer.Invoke(service,new object[]{new Uri("https://cf-files.nexusmods.com/file.zip"),partial,state,CancellationToken.None});
    using(var f=new FileStream(partial,FileMode.Open))f.SetLength(32000);
    File.WriteAllText(partial+".json",JsonTools.Serializer().Serialize(new Dictionary<string,object>{{"id","test:1:2"},{"etag","W/\"test-file\""},{"lastModified",""},{"total",handler.Bytes.Length}}));
    await (Task)transfer.Invoke(service,new object[]{new Uri("https://cf-files.nexusmods.com/file.zip"),partial,state,CancellationToken.None});
    Check(handler.RequestedOffset==0&&Same(File.ReadAllBytes(partial),handler.Bytes),"Weak ETags cannot authorize appending a partial archive");
   }
   using(var api=new NexusApi("synthetic-test-key")){
    typeof(NexusApi).GetField("validated",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(api,new Dictionary<string,object>{{"user_id",7L},{"is_premium",false}});
    var events=new List<object>();
    using(var service=new DownloadService(api,events.Add)){
     var expired=new DownloadRequest{Domain="baldursgate3",ModId=1,FileId=2,Folder=folder,Key="synthetic-download-key",Expires=1,UserId=7};
     bool expiredRejected=false;
     try{await service.DownloadAsync(expired,CancellationToken.None);}catch(IOException ex){expiredRejected=ex.Message.Contains("expired")&&!ex.Message.Contains(expired.Key);}
     Check(expiredRejected&&JsonTools.Text(JsonTools.Dict(events[events.Count-1]),"status")=="failed","Expired authorization rejected without leaking its key");
     expired.Expires=JsonTools.UnixNow()+1000;expired.UserId=8;bool wrongAccount=false;
     try{await service.DownloadAsync(expired,CancellationToken.None);}catch(IOException ex){wrongAccount=ex.Message.Contains("different Nexus account");}
     Check(wrongAccount,"Download authorization for another account rejected");
     expired.UserId=7;expired.Key=null;bool missingKey=false;
     try{await service.DownloadAsync(expired,CancellationToken.None);}catch(IOException ex){missingKey=ex.Message.Contains("Download with Manager");}
     Check(missingKey,"Free download cannot bypass website authorization");
     using(var cancel=new CancellationTokenSource()){
      cancel.Cancel();bool cancelled=false;
      try{await service.DownloadAsync(expired,cancel.Token);}catch(OperationCanceledException){cancelled=true;}
      Check(cancelled&&JsonTools.Text(JsonTools.Dict(events[events.Count-1]),"status")=="cancelled","Cancellation stops a queued download before network access");
     }
    }
   }
   await PipelineTests(folder);
  }finally{
   foreach(string path in Directory.GetFiles(folder))File.Delete(path);
   Directory.Delete(folder,false);
  }
  Console.WriteLine("Passed "+passed+" checks.");
 }

 sealed class PipelineApi : HttpMessageHandler {
  internal int FileRequests,LinkRequests;
  internal Func<HttpRequestMessage,HttpResponseMessage> Override;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {
   string path=request.RequestUri.AbsolutePath;
   if(request.RequestUri.Host!="api.nexusmods.com")throw new Exception("Unexpected fixture API host");
   string json;
   if(path.EndsWith("/download_link.json",StringComparison.Ordinal)) {
    Interlocked.Increment(ref LinkRequests);json="[{\"URI\":\"https://cf-files.nexusmods.com/archive.zip?key=synthetic-secret-signature\"}]";
   }else if(path.Contains("/files/")&&path.EndsWith(".json",StringComparison.Ordinal)) {
    Interlocked.Increment(ref FileRequests);string id=Path.GetFileNameWithoutExtension(path);
    json="{\"file_id\":"+id+",\"file_name\":\"shared-archive.zip\",\"name\":\"Fixture archive\",\"version\":\"1.0\"}";
   }else throw new Exception("Unexpected fixture API path");
   var overridden=Override==null?null:Override(request);
   return Task.FromResult(overridden??new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json)});
  }
 }
 sealed class PipelineServer : HttpMessageHandler {
  internal Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> Respond;
  internal int Requests;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {
   Interlocked.Increment(ref Requests);
   if(request.RequestUri.Host!="cf-files.nexusmods.com"||request.Headers.Contains("apikey"))throw new Exception("Unexpected CDN request or leaked API credential");
   return Respond(request,token);
  }
 }
 sealed class Fixture : IDisposable {
  internal readonly PipelineApi ApiServer=new PipelineApi();
  internal readonly PipelineServer Server=new PipelineServer();
  internal readonly List<object> Events=new List<object>();
  internal readonly List<int> Delays=new List<int>();
  internal readonly NexusApi Api;
  internal readonly DownloadService Service;
  internal readonly string Folder;
  internal Fixture(string folder,DownloadOptions options=null,bool throwOnCompletion=false) {
   Folder=folder;Api=new NexusApi("synthetic-api-key");
   Set(Api,"client",new HttpClient(ApiServer));
   Set(Api,"validated",new Dictionary<string,object>{{"user_id",7L},{"is_premium",true}});
   Service=new DownloadService(Api,value=>{lock(Events)Events.Add(value);if(throwOnCompletion&&JsonTools.Text(JsonTools.Dict(value),"status")=="completed")throw new IOException("Synthetic UI callback failure");},options??new DownloadOptions{MaxDownloadRetries=0});
   Set(Service,"client",new HttpClient(Server){Timeout=Timeout.InfiniteTimeSpan});
   Set(Service,"delay",new Func<int,CancellationToken,Task>((milliseconds,token)=>{lock(Delays)Delays.Add(milliseconds);token.ThrowIfCancellationRequested();return Task.FromResult(true);}));
   Server.Respond=(request,token)=>Task.FromResult(Archive(new byte[]{1,2,3,4,5,6,7,8}));
  }
  internal DownloadRequest Request(long fileId){return new DownloadRequest{Domain="fixturegame",ModId=1,FileId=fileId,Folder=Folder};}
  internal Dictionary<string,object> Last {get{lock(Events)return JsonTools.Dict(Events[Events.Count-1]);}}
  public void Dispose(){Service.Dispose();Api.Dispose();}
 }
 sealed class BlockingStream : Stream {
  readonly byte[] bytes;
  readonly int firstChunk;
  int offset;
  internal readonly TaskCompletionSource<bool> Reading=new TaskCompletionSource<bool>();
  internal readonly TaskCompletionSource<bool> Gate=new TaskCompletionSource<bool>();
  internal bool Disposed;
  internal BlockingStream(byte[] data,int initialChunk=0){bytes=data;firstChunk=initialChunk;}
  public override async Task<int> ReadAsync(byte[] buffer,int start,int count,CancellationToken token) {
   if(firstChunk>0&&offset==0){int size=Math.Min(count,firstChunk);Array.Copy(bytes,0,buffer,start,size);offset+=size;return size;}
   Reading.TrySetResult(true);await Gate.Task.ConfigureAwait(false);
   if(Disposed)throw new ObjectDisposedException("fixture-stream");
   int read=Math.Min(count,bytes.Length-offset);Array.Copy(bytes,offset,buffer,start,read);offset+=read;return read;
  }
  protected override void Dispose(bool disposing){Disposed=true;Gate.TrySetResult(true);base.Dispose(disposing);}
  public override bool CanRead{get{return true;}}public override bool CanWrite{get{return false;}}public override bool CanSeek{get{return false;}}
  public override long Length{get{return bytes.Length;}}public override long Position{get{return offset;}set{throw new NotSupportedException();}}
  public override int Read(byte[] buffer,int offset,int count){throw new NotSupportedException();}
  public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}public override void SetLength(long value){throw new NotSupportedException();}
  public override void Write(byte[] buffer,int offset,int count){throw new NotSupportedException();}public override void Flush(){}
 }
 sealed class TrackedContent : ByteArrayContent {
  internal bool Disposed;
  internal TrackedContent(byte[] bytes):base(bytes){}
  protected override void Dispose(bool disposing){Disposed=true;base.Dispose(disposing);}
 }
 static void Set(object target,string name,object value) {
  var field=target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);
  var previous=field.GetValue(target) as HttpClient;if(previous!=null)previous.Dispose();field.SetValue(target,value);
 }
 static HttpResponseMessage Archive(byte[] bytes) {
  var result=new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};
  result.Headers.ETag=new EntityTagHeaderValue("\"pipeline-version\"");return result;
 }
 static string Marker(string folder,long fileId){return Path.Combine(folder,".nexusm-fixturegame-1-"+fileId+".part.json");}
 static async Task WaitFor(Func<bool> condition) {for(int i=0;i<1000&&!condition();i++)await Task.Delay(1);if(!condition())throw new Exception("Fixture condition timed out");}
 static async Task<IOException> Fail(Task<object> task){try{await task;}catch(IOException error){return error;}throw new Exception("Expected a failed download");}
 static bool Same(byte[] first,byte[] second){return first.SequenceEqual(second);}

 static async Task PipelineTests(string folder) {
  Check(new DownloadOptions().MaxParallelDownloads==2&&new DownloadOptions().MaxDownloadRetries==3,"Download defaults bound concurrency and retry budget");
  foreach(Action<DownloadOptions> mutate in new Action<DownloadOptions>[] {o=>o.MaxParallelDownloads=0,o=>o.MaxParallelDownloads=5,o=>o.MaxDownloadRetries=-1,o=>o.MaxDownloadRetries=11,o=>o.DownloadTimeoutSeconds=14,o=>o.DownloadTimeoutSeconds=601,o=>o.RetryDelaySeconds=0,o=>o.RetryDelaySeconds=61}) {
   var options=new DownloadOptions();mutate(options);bool rejected=false;try{options.ValidatedCopy();}catch(ArgumentOutOfRangeException){rejected=true;}Check(rejected,"Invalid transfer configuration is rejected");
  }
  using(var fixture=new Fixture(folder,new DownloadOptions{MaxParallelDownloads=2,MaxDownloadRetries=0})) {
   var streams=new List<BlockingStream>();
   fixture.Server.Respond=(request,token)=>{var stream=new BlockingStream(new byte[]{11,22,33,44});lock(streams)streams.Add(stream);var response=new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(stream)};response.Content.Headers.ContentLength=4;return Task.FromResult(response);};
   var tasks=Enumerable.Range(100,4).Select(id=>fixture.Service.DownloadAsync(fixture.Request(id),CancellationToken.None)).ToArray();
   await WaitFor(()=>{lock(streams)return streams.Count==2;});
   Check(fixture.Server.Requests==2&&fixture.ApiServer.FileRequests==2,"Configured parallel limit admits two transfers and retains the rest in the queue");
   lock(streams)streams[0].Gate.TrySetResult(true);
   await WaitFor(()=>{lock(streams)return streams.Count==3;});
   Check(tasks.Count(task=>task.IsCompleted)==1,"Finishing one transfer releases exactly one queued slot");
   lock(streams)foreach(var stream in streams)stream.Gate.TrySetResult(true);
   await WaitFor(()=>{lock(streams)return streams.Count==4;});
   lock(streams)streams[3].Gate.TrySetResult(true);
   var rows=(await Task.WhenAll(tasks)).Select(JsonTools.Dict).ToArray();
   Check(rows.Select(row=>JsonTools.Text(row,"path")).Distinct(StringComparer.OrdinalIgnoreCase).Count()==4&&rows.All(row=>Same(File.ReadAllBytes(JsonTools.Text(row,"path")),new byte[]{11,22,33,44})),"Parallel downloads with identical filenames produce separate intact archives");
   Check(rows.All(row=>JsonTools.Text(row,"status")=="completed")&&fixture.Events.Where(value=>JsonTools.Text(JsonTools.Dict(value),"status")=="queued").All(value=>JsonTools.Long(JsonTools.Dict(value),"attempt")==0),"Progress events are immutable snapshots across concurrent state changes");
   foreach(var row in rows)DownloadService.AcknowledgeCompletion(row);
   Check(Enumerable.Range(100,4).All(id=>!File.Exists(Marker(folder,id))),"Completion receipts are removed only after durable completion acknowledgment");
  }
  using(var fixture=new Fixture(folder,new DownloadOptions{MaxDownloadRetries=1})) {
   fixture.Server.Respond=(request,token)=>{if(fixture.Server.Requests==1){var failed=new HttpResponseMessage(HttpStatusCode.ServiceUnavailable){Content=new StringContent("synthetic-secret-signature")};failed.Headers.RetryAfter=new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));return Task.FromResult(failed);}return Task.FromResult(Archive(new byte[]{21,22}));};
   var row=JsonTools.Dict(await fixture.Service.DownloadAsync(fixture.Request(110),CancellationToken.None));
   Check(fixture.Server.Requests==2&&fixture.ApiServer.LinkRequests==2&&fixture.Delays.SequenceEqual(new[]{7000})&&JsonTools.Long(row,"attempt")==2,"Retry refreshes signed CDN links and honors Retry-After before the second attempt");
   string events=JsonTools.Serializer().Serialize(fixture.Events);
   Check(!events.Contains("synthetic-secret-signature")&&!events.Contains("synthetic-api-key")&&fixture.Events.Any(value=>JsonTools.Text(JsonTools.Dict(value),"errorCode")=="server-error"),"Retry events identify safe errors without response bodies, API keys, or signed URLs");
   DownloadService.AcknowledgeCompletion(row);
  }
  using(var fixture=new Fixture(folder,new DownloadOptions{MaxDownloadRetries=2})) {
   fixture.Server.Respond=(request,token)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable){Content=new ByteArrayContent(new byte[0])});
   await Fail(fixture.Service.DownloadAsync(fixture.Request(111),CancellationToken.None));
   Check(fixture.Server.Requests==3&&fixture.Delays.SequenceEqual(new[]{2000,4000})&&JsonTools.Long(fixture.Last,"attempt")==3&&JsonTools.Long(fixture.Last,"retryAt")==0&&JsonTools.Text(fixture.Last,"status")=="failed","Retry budget is finite and exhausted entries remain explicitly failed");
  }
  using(var fixture=new Fixture(folder,new DownloadOptions{MaxDownloadRetries=1})) {
   long started=JsonTools.UnixNow();
   fixture.Server.Respond=(request,token)=>{if(fixture.Server.Requests==1){var limited=new HttpResponseMessage((HttpStatusCode)429){Content=new ByteArrayContent(new byte[0])};limited.Headers.RetryAfter=new RetryConditionHeaderValue(TimeSpan.FromSeconds(700));return Task.FromResult(limited);}return Task.FromResult(Archive(new byte[]{4,2}));};
   var row=JsonTools.Dict(await fixture.Service.DownloadAsync(fixture.Request(121),CancellationToken.None));
   Check(fixture.Delays.SequenceEqual(new[]{700000})&&fixture.Events.Any(value=>JsonTools.Text(JsonTools.Dict(value),"status")=="retrying"&&JsonTools.Long(JsonTools.Dict(value),"retryAt")>=started+700),"A server Retry-After longer than the exponential cap is honored in full");DownloadService.AcknowledgeCompletion(row);
  }
  using(var fixture=new Fixture(folder,new DownloadOptions{MaxDownloadRetries=3})) {
   long started=JsonTools.UnixNow();
   fixture.Server.Respond=(request,token)=>{var limited=new HttpResponseMessage((HttpStatusCode)429){Content=new ByteArrayContent(new byte[0])};limited.Headers.RetryAfter=new RetryConditionHeaderValue(TimeSpan.FromSeconds(7200));return Task.FromResult(limited);};
   await Fail(fixture.Service.DownloadAsync(fixture.Request(122),CancellationToken.None));
   Check(fixture.Server.Requests==1&&fixture.Delays.Count==0&&JsonTools.Text(fixture.Last,"errorCode")=="rate-limited"&&JsonTools.Long(fixture.Last,"retryAt")>=started+7200,"An excessive server cooldown stops automatic retries while retaining its earliest allowed retry time");
  }
  using(var fixture=new Fixture(folder,new DownloadOptions{MaxDownloadRetries=1})) {
   fixture.ApiServer.Override=request=>{if(request.RequestUri.AbsolutePath.EndsWith("/download_link.json",StringComparison.Ordinal)&&fixture.ApiServer.LinkRequests==1){var limited=new HttpResponseMessage((HttpStatusCode)429){Content=new StringContent("synthetic-api-key")};limited.Headers.RetryAfter=new RetryConditionHeaderValue(TimeSpan.FromSeconds(650));return limited;}return null;};
   var row=JsonTools.Dict(await fixture.Service.DownloadAsync(fixture.Request(123),CancellationToken.None));
   Check(fixture.ApiServer.LinkRequests==2&&fixture.Server.Requests==1&&fixture.Delays.SequenceEqual(new[]{650000})&&!JsonTools.Serializer().Serialize(fixture.Events).Contains("synthetic-api-key"),"API Retry-After metadata reaches the configured download retry policy without leaking response data");DownloadService.AcknowledgeCompletion(row);
  }
  using(var fixture=new Fixture(folder,new DownloadOptions{MaxDownloadRetries=0})) {
   fixture.ApiServer.Override=request=>new HttpResponseMessage(HttpStatusCode.ServiceUnavailable){Content=new ByteArrayContent(new byte[0])};
   await Fail(fixture.Service.DownloadAsync(fixture.Request(124),CancellationToken.None));
   Check(fixture.ApiServer.FileRequests==1&&fixture.Server.Requests==0&&fixture.Delays.Count==0&&JsonTools.Long(fixture.Last,"attempt")==1&&JsonTools.Long(fixture.Last,"maxAttempts")==1,"Disabling retries also disables hidden API retries for download setup requests");
  }
  foreach(var code in new[]{HttpStatusCode.Forbidden,HttpStatusCode.NotFound})using(var fixture=new Fixture(folder,new DownloadOptions{MaxDownloadRetries=3})) {
   fixture.Server.Respond=(request,token)=>Task.FromResult(new HttpResponseMessage(code){Content=new StringContent("synthetic-secret-signature")});
   await Fail(fixture.Service.DownloadAsync(fixture.Request(code==HttpStatusCode.Forbidden?112:113),CancellationToken.None));
   Check(fixture.Server.Requests==1&&fixture.Delays.Count==0&&JsonTools.Text(fixture.Last,"errorCode")==(code==HttpStatusCode.Forbidden?"authorization-expired":"file-unavailable"),"Permanent HTTP "+(int)code+" stops automatic retries and retains an actionable failure");
  }
  using(var fixture=new Fixture(folder)) {
   var stream=new BlockingStream(new byte[]{1,2,3,4,5,6},3);Set(fixture.Service,"timeoutMilliseconds",35);
   fixture.Server.Respond=(request,token)=>{var response=new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(stream)};response.Content.Headers.ContentLength=6;response.Headers.ETag=new EntityTagHeaderValue("\"stalled\"");return Task.FromResult(response);};
   await Fail(fixture.Service.DownloadAsync(fixture.Request(114),CancellationToken.None));
   Check(stream.Disposed&&JsonTools.Text(fixture.Last,"errorCode")=="timeout"&&JsonTools.Long(fixture.Last,"downloadedBytes")==3&&new FileInfo(Marker(folder,114).Substring(0,Marker(folder,114).Length-5)).Length==3,"Idle timeout closes a stream that ignores cancellation and retains exact partial byte progress");
   fixture.Server.Respond=(request,token)=>Task.FromResult(Archive(new byte[]{4,5}));
   var next=JsonTools.Dict(await fixture.Service.DownloadAsync(fixture.Request(115),CancellationToken.None));
   Check(JsonTools.Text(next,"status")=="completed","Timed-out transfer releases its concurrency slot for the next entry");DownloadService.AcknowledgeCompletion(next);
  }
  using(var fixture=new Fixture(folder)) {
   Set(fixture.Service,"timeoutMilliseconds",30);var pending=new TaskCompletionSource<HttpResponseMessage>();
   fixture.Server.Respond=(request,token)=>pending.Task;
   await Fail(fixture.Service.DownloadAsync(fixture.Request(116),CancellationToken.None));
   var content=new TrackedContent(new byte[]{1});pending.SetResult(new HttpResponseMessage(HttpStatusCode.OK){Content=content});
   await WaitFor(()=>content.Disposed);
   Check(JsonTools.Text(fixture.Last,"errorCode")=="timeout"&&content.Disposed,"Header timeout bounds an uncooperative handler and disposes its late response");
  }
  using(var fixture=new Fixture(folder,new DownloadOptions{MaxParallelDownloads=1,MaxDownloadRetries=3}))using(var cancellation=new CancellationTokenSource()) {
   var stream=new BlockingStream(new byte[]{6,7,8});fixture.Server.Respond=(request,token)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(stream)});
   var active=fixture.Service.DownloadAsync(fixture.Request(117),cancellation.Token);await stream.Reading.Task;cancellation.Cancel();bool cancelled=false;try{await active;}catch(OperationCanceledException){cancelled=true;}
   Check(cancelled&&stream.Disposed&&fixture.Delays.Count==0&&JsonTools.Text(fixture.Last,"status")=="cancelled","User cancellation closes a stalled stream without consuming the automatic retry budget");
   fixture.Server.Respond=(request,token)=>Task.FromResult(Archive(new byte[]{9}));var next=JsonTools.Dict(await fixture.Service.DownloadAsync(fixture.Request(118),CancellationToken.None));
   Check(JsonTools.Text(next,"status")=="completed","User cancellation releases a transfer slot");DownloadService.AcknowledgeCompletion(next);
  }
  using(var fixture=new Fixture(folder,new DownloadOptions{MaxDownloadRetries=3})) {
   string blocked=Path.Combine(folder,"not-a-folder");File.WriteAllText(blocked,"fixture");var request=fixture.Request(119);request.Folder=blocked;
   await Fail(fixture.Service.DownloadAsync(request,CancellationToken.None));
   Check(JsonTools.Text(fixture.Last,"errorCode")=="disk-error"&&fixture.Server.Requests==0&&fixture.Delays.Count==0,"Disk failures remain visible and do not retry or contact archive servers");
  }
  using(var fixture=new Fixture(folder,new DownloadOptions{MaxParallelDownloads=1,MaxDownloadRetries=0})) {
   var stream=new BlockingStream(new byte[]{1,2});fixture.Server.Respond=(request,token)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(stream)});
   var active=fixture.Service.DownloadAsync(fixture.Request(125),CancellationToken.None);await stream.Reading.Task;
   var waiting=fixture.Service.DownloadAsync(fixture.Request(126),CancellationToken.None);
   fixture.Service.Dispose();bool activeCancelled=false,waitingCancelled=false;
   try{await active;}catch(OperationCanceledException){activeCancelled=true;}try{await waiting;}catch(OperationCanceledException){waitingCancelled=true;}
   Check(activeCancelled&&waitingCancelled&&stream.Disposed&&fixture.Server.Requests==1,"Disposing the service cancels both an active stalled transfer and queued slot waiters");
  }
  using(var fixture=new Fixture(folder,null,true)) {
   var request=fixture.Request(120);var row=JsonTools.Dict(await fixture.Service.DownloadAsync(request,CancellationToken.None));string target=JsonTools.Text(row,"path");
   Check(File.Exists(target)&&File.Exists(Marker(folder,120)),"A completed archive retains its receipt even when the UI notification throws");
   request.Key="expired-sensitive-key";request.Expires=1;
   var recovered=JsonTools.Dict(await fixture.Service.DownloadAsync(request,CancellationToken.None));
   Check(fixture.Server.Requests==1&&JsonTools.Text(recovered,"path")==target&&JsonTools.Text(recovered,"status")=="completed","Crash recovery recognizes the verified completed archive before network or expired authorization checks");
   string partial=Marker(folder,120).Substring(0,Marker(folder,120).Length-5);File.Move(target,partial);
   recovered=JsonTools.Dict(DownloadService.RecoverCompletion(request));
   Check(JsonTools.Text(recovered,"status")=="completed"&&File.Exists(target)&&!File.Exists(partial),"A durable finalization intent can finish an intact partial after a crash before rename");
   File.Move(target,partial);File.WriteAllText(target,"unrelated archive at colliding path");
   recovered=JsonTools.Dict(DownloadService.RecoverCompletion(request));
   Check(JsonTools.Text(recovered,"path")!=target&&File.ReadAllText(target)=="unrelated archive at colliding path"&&File.Exists(JsonTools.Text(recovered,"path")),"Finalization recovery preserves a colliding unrelated file and saves the verified partial under another name");
   string recoveredPath=JsonTools.Text(recovered,"path");File.WriteAllText(recoveredPath,"external replacement");
   Check(DownloadService.RecoverCompletion(request)==null,"A changed final archive cannot be mistaken for the completed receipt");
   DownloadService.AcknowledgeCompletion(recovered);
   Check(File.Exists(Marker(folder,120)),"Acknowledgment cannot remove a receipt whose archive no longer matches");
   var receipt=JsonTools.Dict(JsonTools.Serializer().DeserializeObject(File.ReadAllText(Marker(folder,120))));receipt["path"]=Path.Combine(Path.GetDirectoryName(folder),"outside-receipt.zip");
   File.WriteAllText(Marker(folder,120),JsonTools.Serializer().Serialize(receipt));
   Check(DownloadService.RecoverCompletion(request)==null,"A receipt cannot recover or finalize a target outside the job's pinned folder");
   receipt["path"]="invalid\0receipt";File.WriteAllText(Marker(folder,120),JsonTools.Serializer().Serialize(receipt));
   Check(DownloadService.RecoverCompletion(request)==null,"A malformed receipt path is ignored rather than trapping a pending job");
  }
 }
}
