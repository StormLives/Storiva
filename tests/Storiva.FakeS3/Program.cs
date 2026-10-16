using System.Text;
using Storiva.FakeS3;

// Host for the UI smoke test: "Storiva.FakeS3 [--seed] [--sample]".Prints "ENDPOINT <url>" and serves on 127.0.0.1 until
// stdin is closed. With --seed it creates the sample buckets the smoke test expects. Credentials are fixed,
// fictitious test values (FakeS3Server.TestAccessKey / TestSecret).
await using var server = new FakeS3Server();
server.Start();
if (args.Contains("--seed"))
{
    server.AddBucket("storiva-smoke");
    server.AddBucket("storiva-empty");
    server.AddBucket("storiva-hidden");
    server.HiddenBuckets.Add("storiva-hidden");
    server.PutObject("storiva-hidden", "shared.txt", Encoding.UTF8.GetBytes("visible only by name"), "text/plain");
    server.PutObject("storiva-smoke", "readme.txt", Encoding.UTF8.GetBytes("Storiva smoke test object.\nPlain text.\n"), "text/plain");
    server.PutObject("storiva-smoke", "data/config.json", Encoding.UTF8.GetBytes("{\"name\":\"storiva\",\"items\":[1,2,3]}"), "application/json");
    server.PutObject("storiva-smoke", "data/", []);
    server.PutObject("storiva-smoke", "slow/big.bin", new byte[256 * 1024]);
    server.SlowKeys["slow/big.bin"] = TimeSpan.FromSeconds(30);
    for (var i = 1; i <= 5; i++)
    {
        server.PutObject("storiva-smoke", $"many/item-{i:000}.bin", new byte[100 * i]);
    }
}

// --sample: fictitious buckets for the README screenshots (tools\capture-readme-screenshots.ps1).
if (args.Contains("--sample"))
{
    static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);
    server.AddBucket("example-backups");
    server.AddBucket("example-media");
    server.AddBucket("example-website");
    server.PutObject("example-website", "index.html", Text("<!doctype html>\n<html lang=\"en\">\n<head><title>Example</title></head>\n<body><h1>Hello from Storiva</h1></body>\n</html>\n"), "text/html");
    server.PutObject("example-website", "robots.txt", Text("User-agent: *\nAllow: /\n"), "text/plain");
    server.PutObject("example-website", "site.webmanifest", Text("{\n  \"name\": \"Example site\",\n  \"short_name\": \"Example\",\n  \"start_url\": \"/\",\n  \"display\": \"standalone\",\n  \"theme_color\": \"#0f766e\"\n}\n"), "application/json");
    server.PutObject("example-website", "assets/css/site.css", Text("body { font-family: system-ui, sans-serif; margin: 0; }\n"), "text/css");
    server.PutObject("example-website", "assets/js/app.js", Text("console.log('hello');\n"), "application/javascript");
    server.PutObject("example-website", "assets/img/hero.png", new byte[184_320], "image/png");
    server.PutObject("example-website", "assets/img/logo.svg", Text("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"), "image/svg+xml");
    server.PutObject("example-website", "downloads/catalog-2026.pdf", new byte[1_258_291], "application/pdf");
    server.PutObject("example-website", "downloads/price-list.xlsx", new byte[48_640], "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    server.PutObject("example-media", "photos/2026/beach.jpg", new byte[2_621_440], "image/jpeg");
    server.PutObject("example-media", "videos/intro.mp4", new byte[15_728_640], "video/mp4");
    server.PutObject("example-media", "audio/jingle.mp3", new byte[524_288], "audio/mpeg");
    server.PutObject("example-backups", "weekly/2026-10-11.tar.gz", new byte[3_145_728], "application/gzip");
    server.PutObject("example-backups", "weekly/2026-10-04.tar.gz", new byte[3_096_576], "application/gzip");
}

Console.WriteLine($"ENDPOINT {server.Endpoint}");
Console.Out.Flush();
while (Console.In.ReadLine() is not null)
{
}
