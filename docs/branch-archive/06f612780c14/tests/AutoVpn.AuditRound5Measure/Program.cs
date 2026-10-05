using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AutoVpn.Application;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

var output=Path.GetFullPath(args.Length>0?args[0]:"audit-measure-results");Directory.CreateDirectory(output);
var reports=new List<object>();
foreach(var count in new[]{1000,5000,10000})
{
    var directory=Directory.CreateTempSubdirectory("autovpn-r5-measure-");
    try
    {
        var protector=new CountingProtector();
        using(var catalogue=SqliteCatalogue.Open(Path.Combine(directory.FullName,"synthetic.sqlite"),protector))
        {
            var now=DateTimeOffset.UtcNow;
            var nodes=Enumerable.Range(0,count).Select(i=>
            {
                var semantics=new NodeSemantics { Protocol=ProtocolKind.Vless,Host="synthetic-"+i.ToString(CultureInfo.InvariantCulture)+".example",Port=443,
                    Security="tls",Encryption="none",UserId="11111111-1111-4111-8111-111111111111",Transport="tcp" };
                return new SnapshotNode { ArtifactId="synthetic",FamilyId="black-vless",Digest=CanonicalIdentity.Digest(semantics),Semantics=semantics,Label="synthetic",AdvertisedCountry="DE" };
            }).ToArray();
            catalogue.ApplySnapshot(new SnapshotCommit { ArtifactId="synthetic",FamilyId="black-vless",ContentHash="synthetic",Complete=true,NowUtc=now,Nodes=nodes });
            var node=catalogue.Nodes[^1];
            for(var pass=0;pass<3;pass++)
            {
                GC.Collect();GC.WaitForPendingFinalizers();var memory=GC.GetTotalAllocatedBytes(true);var protectedBefore=protector.Calls;var clock=Stopwatch.StartNew();
                catalogue.ApplyAssessment(node.NodeId,new AssessmentSnapshot { Digest=node.Digest,NetworkEpoch=catalogue.NetworkEpoch,Health=HealthState.Healthy,LastSuccessUtc=now.AddSeconds(pass),MedianLatencyMs=10+pass });
                clock.Stop();reports.Add(new { nodes=count,pass,milliseconds=clock.Elapsed.TotalMilliseconds,allocatedBytes=GC.GetTotalAllocatedBytes(true)-memory,protectorCalls=protector.Calls-protectedBefore });
            }
        }
        SqliteConnection.ClearAllPools();
    }
    finally { directory.Delete(true); }
}
var result=new { sourceCommit="49e5bd54e41731b9b96b83789def31e86c701ee6",os=Environment.OSVersion.ToString(),protect="test-only counting passthrough, no DPAPI",measurements=reports,
    scope="Three single-assessment writes at each size. No network, no TUN, no production credentials; not a soak or full UI/probe throughput benchmark." };
File.WriteAllText(Path.Combine(output,"measure.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions { WriteIndented=true }));
Console.WriteLine(JsonSerializer.Serialize(result));
internal sealed class CountingProtector:ISecretProtector
{
    public int Calls { get;private set; }public string ProtectorId=>"test-round5-counting";
    public byte[] Protect(ReadOnlySpan<byte> data){Calls++;return data.ToArray();}
    public byte[] Unprotect(ReadOnlySpan<byte> data)=>data.ToArray();
}
