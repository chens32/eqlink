using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using EqLink.Core;

namespace EqLink.Tests;

internal static partial class Program
{
    private static async Task ControlTls(DeviceProfile fixture)
    {
        using var rootKey = RSA.Create(3072);
        var rootRequest = new CertificateRequest("CN=EqLink-isolated-TLS-root",rootKey,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,false,0,true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign,true));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(2));
        using var serverKey = RSA.Create(3072); using var clientKey = RSA.Create(3072);
        X509Certificate2 Issue(string name,RSA key,bool server)
        {
            var request = new CertificateRequest("CN="+name,key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection{new(server?"1.3.6.1.5.5.7.3.1":"1.3.6.1.5.5.7.3.2")},true));
            if(server){var san=new SubjectAlternativeNameBuilder();san.AddDnsName("server");request.CertificateExtensions.Add(san.Build());}
            using var issued=request.Create(root,DateTimeOffset.UtcNow.AddHours(-1),DateTimeOffset.UtcNow.AddDays(1),RandomNumberGenerator.GetBytes(16));
            using var pair=issued.CopyWithPrivateKey(key);
            var pfx=pair.Export(X509ContentType.Pkcs12);
            try{return X509CertificateLoader.LoadPkcs12(pfx,null,X509KeyStorageFlags.DefaultKeySet);}finally{CryptographicOperations.ZeroMemory(pfx);}
        }
        using var serverCert=Issue("server",serverKey,true);using var clientCert=Issue("device",clientKey,false);
        using var listener=new TcpListener(IPAddress.Parse("127.77.89.2"),0);listener.Start();
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server=Task.Run(async ()=>
        {
            using var tcp=await listener.AcceptTcpClientAsync(deadline.Token);
            using var ssl=new SslStream(tcp.GetStream(),false,(_,cert,_,_)=>cert is not null && cert.GetCertHashString()==clientCert.GetCertHashString());
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=serverCert,ClientCertificateRequired=true,CertificateRevocationCheckMode=X509RevocationMode.NoCheck},deadline.Token);
            var header=new List<byte>();var one=new byte[1];
            while(header.Count<8192) {await ssl.ReadExactlyAsync(one,deadline.Token);header.Add(one[0]);if(header.Count>=4 && header.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray()))break;}
            var first=Encoding.ASCII.GetString(header.ToArray()).Split('\r')[0];var path=first.Split(' ')[1];
            var nonce=path.Split("nonce=")[1];
            var body=JsonSerializer.SerializeToUtf8Bytes(new{ok=true,nonce});
            await ssl.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"),deadline.Token);
            await ssl.WriteAsync(body,deadline.Token);
        },deadline.Token);
        var profile=fixture with {VirtualIp="127.77.89.1",ServerIp="127.77.89.2",HealthPort=((IPEndPoint)listener.LocalEndpoint).Port,CaPem=root.ExportCertificatePem(),CertificatePem=clientCert.ExportCertificatePem(),PrivateKeyPem=clientKey.ExportPkcs8PrivateKeyPem()};
        using(var api=new ControlApiClient(profile,connectTimeout:TimeSpan.FromSeconds(10))) await api.HealthAsync(deadline.Token);
        await server;
        Console.WriteLine("Native mutual TLS control client, custom root/name trust and authenticated nonce: passed (isolated loopback)");
    }
}
