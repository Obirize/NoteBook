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
        SyncMerge.Apply(book,[],[new(note.Id,99)],"phone");
        SyncMerge.Apply(book,[note],[],"phone");
        check(!book.Notes.Any(n=>n.Id==note.Id),"Purge prevents stale note resurrection");
        var host=new Host(Path.Combine(root,"sync-test"));
        var media=host.Attachments.Import(new MemoryStream(Encoding.UTF8.GetBytes("desktop attachment")),"desktop.png","image/png");
        host.Book.Notes.Add(new Note { Title="Desktop",Text="from PC",Attachments=[media] });
        using var service=new SyncService(Path.Combine(root,"sync-test"),host);
        service.Settings.Port=Random.Shared.Next(48000,58000);
        service.Start();check(service.Running,"HTTPS sync server starts");
        check(service.PairCode==null && service.TryPair("000000")==null,"No pairing code exists until the sync window asks for one");
        string burn=service.NewPairCode();
        for(int i=0;i<5;i++)check(service.TryPair(burn=="111111"?"222222":"111111")==null,"Wrong pairing code attempt "+(i+1)+" is refused");
        check(service.PairCode==null && service.TryPair(burn)==null,"Five wrong attempts burn the pairing code");
        string code=service.NewPairCode();
        check(code.Length==6 && code.All(char.IsAsciiDigit) && service.PairCode==code,"A fresh six-digit pairing code is shown");
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
        check(host.Book.Notes.Any(n=>n.Title=="Phone"),"WebCrypto phone uploads encrypted note to C#");
        var phone=host.Book.Notes.First(n=>n.Title=="Phone");
        using var output=new MemoryStream();host.Attachments.Decrypt(phone.Attachments.Single(),output);
        check(Encoding.UTF8.GetString(output.ToArray())=="phone attachment","WebCrypto attachment decrypts on desktop");
        check(host.Book.Notes.Any(n=>n.Title.Contains("conflict copy")),"Divergent base creates a conflict copy across unequal revisions");
    }
}
