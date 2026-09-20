using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Notlar;
using Notlar.Sync;

static class SyncChecks
{
    private sealed class Host(string root) : ISyncHost
    {
        public Notebook Book = new();
        public AttachmentStore Attachments { get; } = new(Path.Combine(root,"attachments"));
        public Task<(Manifest Manifest,string NotebookId)> ManifestAsync() => Task.FromResult((Manifest.Of(Book,Attachments),Book.Id));
        public Task<List<Note>> NotesAsync(IReadOnlyList<string> ids) => Task.FromResult(Book.Notes.Where(n=>ids.Contains(n.Id)).Select(SyncMerge.Clone).ToList());
        public Task<Attachment?> AttachmentAsync(string id) => Task.FromResult(Book.Notes.SelectMany(n=>n.Attachments).FirstOrDefault(a=>a.Id==id));
        public Task<SyncMerge.Result> ApplyAsync(List<Note> notes,List<PurgeStamp> purges,string name) => Task.FromResult(SyncMerge.Apply(Book,notes,purges,name));
        public Task DeviceSeenAsync(string id,string name) => Task.CompletedTask;
    }
    public static void Run(string root, Action<bool,string> check)
    {
        using var keys=new SyncKeys(new byte[32]);
        var note=new Note { Title="Türkçe 🔐", Text="secret" };
        var sealedNote=keys.Seal(note);
        check(keys.Open(note.Id,note.Revision,sealedNote).Text=="secret","Sync note encryption round trip");
        sealedNote[^1]^=1;
        bool rejected=false;try { keys.Open(note.Id,note.Revision,sealedNote); }catch(CryptographicException){rejected=true;}
        check(rejected,"Sync rejects tampered ciphertext");
        var book=new Notebook { Notes=[note] };
        var edited=SyncMerge.Clone(note);edited.Text="phone edit";edited.Updated=note.Updated.AddSeconds(1);
        var result=SyncMerge.Apply(book,[edited],[],"phone");
        check(result.Conflicts==1 && book.Notes.Count==2 && book.Notes.Any(n=>n.Text=="secret") && book.Notes.Any(n=>n.Text=="phone edit"),"Sync preserves equal-revision conflicting edits");
        SyncMerge.Apply(book,[edited],[],"phone");
        check(book.Notes.Count==2,"Sync conflict replay does not duplicate copies");
        var archived=SyncMerge.Clone(book.Notes.First(n=>n.Id==note.Id));archived.Archived=true;archived.Revision++;archived.Updated=archived.Updated.AddSeconds(5);
        SyncMerge.Apply(book,[archived],[],"phone");
        check(book.Notes.First(n=>n.Id==note.Id).Archived && !SyncMerge.SameContent(edited,archived),"Archiving on one device reaches the other through sync");
        SyncMerge.Apply(book,[],[new(note.Id,99)],"phone");
        SyncMerge.Apply(book,[note],[],"phone");
        check(!book.Notes.Any(n=>n.Id==note.Id),"Purge prevents stale note resurrection");
        var host=new Host(Path.Combine(root,"sync-test"));
        var media=host.Attachments.Import(new MemoryStream(Encoding.UTF8.GetBytes("desktop attachment")),"desktop.png","image/png");
        host.Book.Notes.Add(new Note { Title="Desktop",Text="from PC",Attachments=[media] });
        using var service=new SyncService(Path.Combine(root,"sync-test"),host);
        // A random port pair can be taken by something else on this machine; try a few before giving up.
        for(int attempt=0;attempt<5&&!service.Running;attempt++){service.Settings.Port=Random.Shared.Next(48000,58000);service.Start();}
        check(service.Running,"HTTPS sync server starts");
        check(service.PairCode==null && service.TryPair("000000")==null,"No pairing code exists until the sync window asks for one");
        string burn=service.NewPairCode();
        for(int i=0;i<5;i++)check(service.TryPair(burn=="111111"?"222222":"111111")==null,"Wrong pairing code attempt "+(i+1)+" is refused");
        check(service.PairCode==null && service.TryPair(burn)==null,"Five wrong attempts burn the pairing code");
        var clock=DateTimeOffset.UtcNow;service.Clock=()=>clock;
        string first=service.NewPairCode();
        check(first.Length==6 && first.All(char.IsAsciiDigit) && service.CurrentPairCode()==first && service.PairCodeSecondsLeft==SyncService.PairCodeSeconds,"A fresh six-digit pairing code is shown with a full countdown");
        clock=clock.AddSeconds(SyncService.PairCodeSeconds-1);
        check(service.CurrentPairCode()==first && service.PairCodeSecondsLeft==1,"The code stays the same until its minute is over");
        clock=clock.AddSeconds(1);
        string second=service.CurrentPairCode();
        check(second!=first && service.PairCode==second,"After a minute the window shows a new code");
        clock=clock.AddSeconds(SyncService.PairCodeGraceSeconds/2);
        var graced=service.TryPair(first);
        check(graced!=null && graced.Length==32 && service.PairCode==null,"The previous code still works briefly for someone mid-typing, then every code is spent");
        CryptographicOperations.ZeroMemory(graced!);
        string third=service.NewPairCode();clock=clock.AddSeconds(SyncService.PairCodeSeconds+1);
        check(service.TryPair(third)==null,"A code older than a minute is refused");
        string code=service.NewPairCode();
        string script=Path.GetFullPath("tests/Notlar.Tests/sync-integration.mjs");
        var start=new ProcessStartInfo("node") { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true };
        start.ArgumentList.Add(script);start.ArgumentList.Add(service.Port.ToString());
        start.Environment["NOTEBOOK_TEST_KEY"]=Convert.ToBase64String(service.Settings.Key());
        start.Environment["NOTEBOOK_TEST_CODE"]=code;
        // Only the isolated test client bypasses TLS trust, using a newly generated test CA.
        start.Environment["NODE_TLS_REJECT_UNAUTHORIZED"]="0";
        using var process=Process.Start(start)!;
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(45000)){process.Kill(true);throw new Exception("Sync integration timed out");}
        Console.WriteLine(stdout.GetAwaiter().GetResult());
        if(process.ExitCode!=0)throw new Exception(stderr.GetAwaiter().GetResult());
        check(service.PairCode==null,"A used pairing code cannot be used again");
        check(host.Book.Notes.Any(n=>n.Title=="Phone" && n.Archived),"WebCrypto phone uploads encrypted note to C#, archive flag included");
        var phone=host.Book.Notes.First(n=>n.Title=="Phone");
        using var output=new MemoryStream();host.Attachments.Decrypt(phone.Attachments.First(a=>a.Name=="phone.png"),output);
        check(Encoding.UTF8.GetString(output.ToArray())=="phone attachment","WebCrypto attachment decrypts on desktop");
        var clipMeta=phone.Attachments.First(a=>a.Name.EndsWith(".mov"));
        var deadline=DateTime.UtcNow.AddSeconds(15);while(!host.Attachments.Exists(clipMeta)&&DateTime.UtcNow<deadline)Thread.Sleep(50);
        using var clipOut=new MemoryStream();host.Attachments.Decrypt(clipMeta,clipOut);var clipBytes=clipOut.ToArray();
        bool exact=clipBytes.Length==2621440+123;for(int i=0;exact&&i<clipBytes.Length;i++)exact=clipBytes[i]==(byte)((i*7+3)&255);
        check(exact,"A multi-chunk video from the phone arrives on the desktop byte for byte (no re-encoding anywhere)");
        check(host.Book.Notes.Any(n=>n.Title.Contains("conflict copy")),"Divergent base creates a conflict copy across unequal revisions");
        check(host.Book.Notes.Count(n=>n.Text.StartsWith("typing"))==1 && host.Book.Notes.Single(n=>n.Text.StartsWith("typing")).Text=="typing ab","Fast typing from one phone lands as one note, not as conflict copies");
    }
}
