using System;
using System.Collections.Generic;
using System.IO;
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
  }finally{
   string partial=Path.Combine(folder,"file.part");
   if(File.Exists(partial))File.Delete(partial);
   if(File.Exists(partial+".json"))File.Delete(partial+".json");
   Directory.Delete(folder,false);
  }
  Console.WriteLine("Passed "+passed+" checks.");
 }
}
