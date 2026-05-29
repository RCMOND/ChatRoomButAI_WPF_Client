using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.Text.Json;

namespace ChatClientWpf;

public static class AppConfig
{
    private static readonly string ConfigFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

    public static IConfiguration Configuration { get; }

    static AppConfig()
    {
        // 如果配置文件不存在，自动生成默认配置
        if (!File.Exists(ConfigFilePath))
        {
            var defaultConfig = new
            {
                Server = new
                {
                    BaseUrl = "http://127.0.0.1:25565"
                }
            };

            string json = JsonSerializer.Serialize(defaultConfig, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigFilePath, json, System.Text.Encoding.UTF8);
        }

        Configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();
    }

    public static string ServerBaseUrl
    {
        get
        {
            string url = Configuration["Server:BaseUrl"] ?? "http://127.0.0.1:25565";
            return url.TrimEnd('/');
        }
    }

    public static string ChatHubUrl => $"{ServerBaseUrl}/chatHub";
}
