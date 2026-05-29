using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace ChatClientWpf;

public partial class LoginWindow : Window
{
    private readonly HttpClient _httpClient = new HttpClient();
    public string? Token { get; private set; }
    public string Username { get; private set; } = "";

    public LoginWindow()
    {
        InitializeComponent();
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
        this.Loaded += LoginWindow_Loaded;
        this.Closing += LoginWindow_Closing;
    }

    private async void LoginWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 尝试自动登录
        string tokenPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "token.dat");
        string userPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "user.dat");
        if (File.Exists(tokenPath) && File.Exists(userPath))
        {
            string savedToken = File.ReadAllText(tokenPath).Trim();
            string savedUser = File.ReadAllText(userPath).Trim();
            if (!string.IsNullOrEmpty(savedToken) && !string.IsNullOrEmpty(savedUser))
            {
                // 快速验证 token 是否有效（调用一个轻量 API）
                var request = new HttpRequestMessage(HttpMethod.Get, $"{AppConfig.ServerBaseUrl}/api/admin/status");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", savedToken);
                try
                {
                    var res = await _httpClient.SendAsync(request);
                    if (res.IsSuccessStatusCode)
                    {
                        // 自动登录成功，直接跳转
                        var mainWin = new MainWindow();
                        mainWin.InitializeAndStart(savedToken, savedUser);
                        mainWin.Show();
                        this.Close();
                        return;
                    }
                }
                catch { /* token 无效，删除本地凭据，继续正常登录 */ }
                File.Delete(tokenPath);
                File.Delete(userPath);
            }
        }
    }

    private async void BtnLogin_Click(object sender, RoutedEventArgs e)
    {
        string user = txtLoginUser.Text.Trim();
        string pass = txtLoginPassword.Password;
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass)) return;

        string hash = ComputeSha256Hash(pass);
        try
        {
            var res = await _httpClient.PostAsJsonAsync($"{AppConfig.ServerBaseUrl}/api/auth/login",
                new { Username = user, Password = hash });
            if (res.IsSuccessStatusCode)
            {
                var result = await res.Content.ReadFromJsonAsync<LoginResponse>();
                var mainWin = new MainWindow();
                mainWin.InitializeAndStart(result!.Token, user);
                mainWin.Show();
                this.Close();
            }
            else
            {
                txtError.Text = "用户名或密码错误";
            }
        }
        catch (Exception ex)
        {
            txtError.Text = ex.Message;
        }
    }

    private void BtnRegister_Click(object sender, RoutedEventArgs e)
    {
        var regWin = new RegisterWindow();
        regWin.Owner = this;
        if (regWin.ShowDialog() == true)
        {
            txtLoginUser.Text = regWin.Username;
            txtLoginPassword.Password = regWin.Password;
        }
    }

    private void LoginWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // 如果应用程序没有其他窗口，则允许关闭时退出程序（例如用户直接关闭登录窗口）
        if (Application.Current.Windows.Count == 1 && this.Visibility == Visibility.Visible)
        {
            Application.Current.Shutdown();
        }
    }

    private static string ComputeSha256Hash(string rawData)
    {
        using var sha256 = SHA256.Create();
        byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawData));
        var builder = new StringBuilder();
        foreach (byte b in bytes) builder.Append(b.ToString("x2"));
        return builder.ToString();
    }
}