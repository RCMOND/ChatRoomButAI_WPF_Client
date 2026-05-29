using Microsoft.Extensions.Configuration;
using System.IO;

namespace ChatClientWpf;

public static class AppConfig
{
    public static IConfiguration Configuration { get; }

    static AppConfig()
    {
        Configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();
    }

    public static string ServerBaseUrl
    {
        get
        {
            string url = Configuration["Server:BaseUrl"] ?? "http://127.0.0.1:25565";
            return url.TrimEnd('/');   // 强制去除末尾斜杠
        }
    }

    public static string ChatHubUrl => $"{ServerBaseUrl}/chatHub";
}

  