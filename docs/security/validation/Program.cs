using FactoryQuoteApi.Controllers;
using FactoryQuoteApi.Data;
using FactoryQuoteApi.DTOs;
using FactoryQuoteApi.Models;
using FactoryQuoteApi.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

// Never starts the application, loads environment configuration, opens SQL, or uses a network transport.
var root = Path.Combine(Path.GetTempPath(), "factory-security-audit-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(root, "quotes"));
var env = new FakeEnvironment(root);
var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
using var db = new ApplicationDbContext(options);
var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
    ["Security:SigningKey"] = "synthetic-audit-only-key-000000000000000000000000",
    ["Telegram:BotToken"] = "123456:synthetic-audit-only",
    ["Telegram:ChatIds"] = "synthetic-fallback"
}).Build();
var security = new SecurityService(db,cfg,NullLogger<SecurityService>.Instance);
var trackedConfig=new ConfigurationBuilder().AddJsonFile(Path.Combine(Directory.GetCurrentDirectory(),"backend","appsettings.json")).Build();
using(var defaultsDb=new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options)){
 var defaultsService=new SecurityService(defaultsDb,trackedConfig,NullLogger<SecurityService>.Instance);
 await defaultsService.EnsureInitializedAsync();
 var source=File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(),"backend","Services","SecurityService.cs"));
 foreach(var role in new[]{"Quote","Admin","Owner"}){
  var match=Regex.Match(source,"default"+role+"Password = \"([^\"]+)\"");
  Check("tracked placeholder configuration seeds working default "+role+" login",match.Success && (await defaultsService.AuthenticateAsync(role,match.Groups[1].Value)).Success);
 }
}
var alice = new User { FullName="Synthetic Alice",Email="alice@example.invalid", PasswordHash=security.HashPassword("synthetic-only-password"),Role="Sales" };
var bob = new User { FullName="Synthetic Bob",Email="bob@example.invalid", PasswordHash=security.HashPassword("synthetic-only-password"),Role="Sales" };
db.Users.AddRange(alice,bob);
foreach(var r in new[]{"quote","admin","owner"}) db.SystemCredentials.Add(new SystemCredential {Role=r,PasswordHash=security.HashPassword("synthetic-role-password"),SessionSalt="synthetic-"+r});
await db.SaveChangesAsync();
var token = security.GenerateUserToken(alice,TimeSpan.FromHours(1));
var adminToken = security.GenerateToken("admin","synthetic-admin",TimeSpan.FromHours(1));
var quoteToken = security.GenerateToken("quote","synthetic-quote",TimeSpan.FromHours(1));
var transport = new FakeTransport();
var factory = new FakeFactory(transport);
var telegram = new TelegramDeliveryService(db,factory,cfg,env,NullLogger<TelegramDeliveryService>.Instance);
var word = new WordQuoteGeneratorService(NullLogger<WordQuoteGeneratorService>.Instance);
var quotes = new QuotesController(db,word,new FakePdf(),new BackgroundTaskQueue(),cfg,env,NullLogger<QuotesController>.Instance,security,new FakeSequence(),telegram,new FakeNotification());
var admin = new AdminController(db,security,telegram);
var auth = new AuthController(security,NullLogger<AuthController>.Instance);
void Check(string name,bool ok) { if(!ok) throw new Exception("CHECK FAILED: "+name); Console.WriteLine("PASS "+name); }
void SetToken(ControllerBase c,string? t) { c.ControllerContext = new ControllerContext {HttpContext=new DefaultHttpContext()}; c.Request.Scheme="https"; c.Request.Host=new HostString("audit.invalid"); if(t!=null)c.Request.Headers.Authorization="Bearer "+t; }
var q = new Quote { QuoteNumber="000001-20-10-2026",UserId=bob.Id,CreatedByUserId=bob.Id,ClientName="Synthetic client",PdfUrl="https://audit.invalid/quotes/synthetic.pdf" };
db.Quotes.Add(q); await db.SaveChangesAsync();
await File.WriteAllTextAsync(Path.Combine(root,"quotes","synthetic.pdf"),"%PDF- synthetic fixture only");
SetToken(quotes,null);
var publicQuote = await quotes.GetQuote(q.Id);
Check("anonymous quote read returns entity",publicQuote.Result is OkObjectResult);
var body=JsonSerializer.Serialize(((OkObjectResult)publicQuote.Result!).Value,new JsonSerializerOptions{ReferenceHandler=ReferenceHandler.IgnoreCycles});
using(var serialized=JsonDocument.Parse(body))Check("quote serialization includes password hash",serialized.RootElement.GetProperty("User").GetProperty("PasswordHash").GetString()==bob.PasswordHash);
string B64(byte[] b)=>Convert.ToBase64String(b).TrimEnd('=').Replace('+','-').Replace('/','_');
var knownKey=trackedConfig["Security:SigningKey"]!;
var payload=B64(Encoding.UTF8.GetBytes($"employee|{bob.Id}:{bob.PasswordHash[^16..]}|{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}"));
using(var mac=new HMACSHA256(Encoding.UTF8.GetBytes(knownKey))){
 var forged=payload+"."+B64(mac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
 var weakService=new SecurityService(db,trackedConfig,NullLogger<SecurityService>.Instance);
 Check("public placeholder key plus disclosed hash permits employee impersonation",(await weakService.ValidateUserTokenAsync(forged)).IsValid);
}
Check("wrong HMAC key rejected by configured service",!(await security.ValidateUserTokenAsync(payload+".invalid-signature")).IsValid);
Check("anonymous file action returns physical file",quotes.DownloadQuoteFile("synthetic.pdf") is PhysicalFileResult);
Check("anonymous generation rejected",(await quotes.GenerateQuote(new QuoteRequestDto())).Result is UnauthorizedObjectResult);
Check("anonymous preview rejected",await quotes.PreviewQuote(new QuoteRequestDto()) is UnauthorizedObjectResult);
Check("anonymous clients allowed",(await new ClientsController(db).GetClients()).Result is OkObjectResult);
SetToken(admin,adminToken);
var created = await admin.CreateEmployee(new CreateEmployeeRequestDto {FullName="Synthetic Issued",Email="issued@example.invalid",Password="synthetic-password",ConfirmPassword="synthetic-password"});
Check("Admin can create employee without Owner",created is ObjectResult {StatusCode:201});
SetToken(admin,token);
Check("Employee cannot create employees",await admin.CreateEmployee(new CreateEmployeeRequestDto()) is UnauthorizedObjectResult);
Check("Employee can spoof audit actor",await admin.RecordAudit(new AdminController.AuditLogRequestDto{UserId=bob.Id,Action="synthetic-event",EntityId="synthetic"}) is OkObjectResult && await db.AuditLogs.AnyAsync(a=>a.UserId==bob.Id && a.Action=="synthetic-event"));
Check("legacy quote token validates",(await security.ValidateUserTokenAsync(quoteToken)).IsValid);
SetToken(auth,token); auth.Logout();
Check("logout does not revoke bearer token",(await security.ValidateUserTokenAsync(token)).IsValid);
alice.IsActive=false; await db.SaveChangesAsync();
Check("disabled employee token rejected",!(await security.ValidateUserTokenAsync(token)).IsValid);
alice.IsActive=true; await db.SaveChangesAsync();
Check("reenabling account revives old unexpired token",(await security.ValidateUserTokenAsync(token)).IsValid);
alice.PasswordHash=security.HashPassword("replacement-password"); await db.SaveChangesAsync();
Check("password reset invalidates old employee token",!(await security.ValidateUserTokenAsync(token)).IsValid);
var f1=word.SanitizeFileName("Synthetic client","Same project","000001");
var f2=word.SanitizeFileName("Synthetic client","Same project","000002");
Check("distinct quote numbers collide on document filename",f1==f2);
var template=Path.Combine(root,"template.docx");
using(var doc=WordprocessingDocument.Create(template,DocumentFormat.OpenXml.WordprocessingDocumentType.Document)){doc.AddMainDocumentPart().Document=new Document(new Body(new Paragraph(new Run(new Text("{{QuoteNumber}}")))));}
var outPath=Path.Combine(root,f1+".docx");
await word.GenerateQuoteDocumentAsync(template,outPath,new QuoteRequestDto{ClientName="Synthetic client"},"FIRST");
await word.GenerateQuoteDocumentAsync(template,outPath,new QuoteRequestDto{ClientName="Synthetic client"},"SECOND");
using(var doc=WordprocessingDocument.Open(outPath,false))Check("document writer overwrites earlier quote",doc.MainDocumentPart!.Document.InnerText.Contains("SECOND") && !doc.MainDocumentPart.Document.InnerText.Contains("FIRST"));
Directory.CreateDirectory(Path.Combine(root,"Templates"));File.Copy(template,Path.Combine(root,"Templates","عرض سعر شركة اتريم.docx"));
SetToken(quotes,security.GenerateUserToken(alice,TimeSpan.FromHours(1)));
var generated=await quotes.GenerateQuote(new QuoteRequestDto {UserId=bob.Id,ClientName="Synthetic authenticated",Items=new(){new ProductItemDto{ProductName="Synthetic item",Quantity=1,UnitPrice=5}}});
Check("generation ignores frontend UserId for quote and audit",generated.Result is OkObjectResult && await db.Quotes.AnyAsync(x=>x.ClientName=="Synthetic authenticated" && x.UserId==alice.Id && x.CreatedByUserId==alice.Id) && await db.AuditLogs.AnyAsync(x=>x.Action=="QuoteGenerated" && x.UserId==alice.Id));
SetToken(quotes,quoteToken);
var sharedGenerated=await quotes.GenerateQuote(new QuoteRequestDto{ClientName="Synthetic shared login",Items=new(){new ProductItemDto{ProductName="Synthetic item",Quantity=1,UnitPrice=5}}});
Check("legacy shared quote token can generate a quote",sharedGenerated.Result is OkObjectResult);
db.TelegramRecipients.Add(new TelegramRecipient{ChatId="synthetic-active",DisplayName="Synthetic active",IsActive=true}); await db.SaveChangesAsync();
var sent = await telegram.SendQuoteDocumentAsync(q.Id);
Check("automatic Telegram sends PDF using server creator caption",sent.Count==1 && sent[0].Success && transport.Bodies.Any(b=>b.Contains("Synthetic Bob") && b.Contains("application/pdf")));
var calls=transport.Sends;
await telegram.RetryQuoteDeliveryAsync(q.Id,"synthetic-active");
Check("sequential retry skips Sent record",transport.Sends==calls);
var inactive=await db.TelegramRecipients.SingleAsync(); inactive.IsActive=false; await db.SaveChangesAsync();
var fallback=await telegram.SendQuoteDocumentAsync(q.Id);
Check("zero active recipients falls back to configuration",fallback.Any(r=>r.ChatId=="synthetic-fallback"));
var arbitrary=await telegram.RetryQuoteDeliveryAsync(q.Id,"synthetic-unregistered");
Check("specific retry accepts unregistered Chat ID",arbitrary.Any(r=>r.ChatId=="synthetic-unregistered" && r.Success));
var q2=new Quote{QuoteNumber="000002-20-10-2026",UserId=bob.Id,ClientName="Synthetic",PdfUrl=q.PdfUrl};db.Quotes.Add(q2);await db.SaveChangesAsync();
db.QuoteTelegramDeliveries.Add(new QuoteTelegramDelivery{QuoteId=q2.Id,QuoteNumber=q2.QuoteNumber,ChatId="synthetic-race",Status="Pending"});await db.SaveChangesAsync();
using(var d1=new ApplicationDbContext(options))using(var d2=new ApplicationDbContext(options)){
 var racingTransport=new FakeTransport{Barrier=true};var racingFactory=new FakeFactory(racingTransport);
 var s1=new TelegramDeliveryService(d1,racingFactory,cfg,env,NullLogger<TelegramDeliveryService>.Instance);
 var s2=new TelegramDeliveryService(d2,racingFactory,cfg,env,NullLogger<TelegramDeliveryService>.Instance);
 await Task.WhenAll(s1.RetryQuoteDeliveryAsync(q2.Id,"synthetic-race"),s2.RetryQuoteDeliveryAsync(q2.Id,"synthetic-race"));
 Check("concurrent pending retry sends twice",racingTransport.Sends==2);
}
await telegram.GetBotInfoAsync();
Check("GET bot-info invokes deleteWebhook",transport.Paths.Any(p=>p.EndsWith("/deleteWebhook")));
var corsSource=File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(),"backend","Program.cs"));
Check("CORS source contains broad suffix predicate",corsSource.Contains("uri.Host.EndsWith(\"vercel.app\""));
Check("broad suffix accepts attacker and suffix-lookalike hosts",new[]{"https://attacker.vercel.app","https://evilvercel.app"}.All(o=>new Uri(o).Host.EndsWith("vercel.app",StringComparison.OrdinalIgnoreCase)));
Console.WriteLine("ALL CHECKS COMPLETE; fixture directory: "+root);

class FakeEnvironment(string root):IWebHostEnvironment {
 public string WebRootPath{get;set;}=root; public IFileProvider WebRootFileProvider{get;set;}=new NullFileProvider();public string ApplicationName{get;set;}="Audit";public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();public string ContentRootPath{get;set;}=root;public string EnvironmentName{get;set;}="Audit";
}
class FakeFactory(FakeTransport handler):IHttpClientFactory { public HttpClient CreateClient(string name)=>new(handler,false); }
class FakeTransport:HttpMessageHandler {
 public int Sends;public bool Barrier;public List<string>Bodies=new();public List<string>Paths=new();private TaskCompletionSource both=new(TaskCreationOptions.RunContinuationsAsynchronously);
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){
  lock(Paths)Paths.Add(r.RequestUri!.AbsolutePath);
  if(r.RequestUri!.AbsolutePath.EndsWith("/sendDocument")){
   var requestBody=await r.Content!.ReadAsStringAsync(ct);lock(Bodies)Bodies.Add(requestBody);
   if(Interlocked.Increment(ref Sends)==2)both.TrySetResult();
   if(Barrier)await both.Task.WaitAsync(TimeSpan.FromSeconds(5),ct);
   return Reply("{\"ok\":true,\"result\":{\"message_id\":123,\"document\":{\"file_id\":\"synthetic-file\"}}}");
  }
  if(r.RequestUri.AbsolutePath.EndsWith("/getMe"))return Reply("{\"ok\":true,\"result\":{\"id\":123,\"username\":\"synthetic\"}}");
  if(r.RequestUri.AbsolutePath.EndsWith("/getWebhookInfo"))return Reply("{\"ok\":true,\"result\":{\"url\":\"https://synthetic.invalid/hook\"}}");
  return Reply("{\"ok\":true,\"result\":[]}");
 }
 static HttpResponseMessage Reply(string body)=>new(HttpStatusCode.OK){Content=new StringContent(body)};
}
class FakePdf:IPdfConverterService {public async Task<string> ConvertDocxToPdfAsync(string a,string b,CancellationToken c=default){await File.WriteAllTextAsync(b,"%PDF- synthetic fixture only",c);return b;}}
class FakeSequence:IQuoteSequenceService {int n=900;public Task EnsureInitializedAsync()=>Task.CompletedTask;public Task<string> GetNextQuoteNumberAsync(DateTime d)=>Task.FromResult($"{++n:D6}-20-10-2026");public Task<string> PeekNextQuoteNumberAsync(DateTime d)=>Task.FromResult($"{n+1:D6}-20-10-2026");}
class FakeNotification:INotificationService {public Task SendEmailNotificationAsync(string a,string b,string c,decimal d,string e,CancellationToken f=default)=>Task.CompletedTask;public Task SendWhatsAppWebhookAsync(WebhookPayloadDto p,CancellationToken c=default)=>Task.CompletedTask;}
