using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace ChatClientWpf;

public partial class RegisterWindow : Window
{
    private readonly HttpClient _httpClient = new HttpClient();
    public string Username { get; private set; } = "";
    public string Password { get; private set; } = "";

    public RegisterWindow()
    {
        InitializeComponent();
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    private async void BtnRegister_Click(object sender, RoutedEventArgs e)
    {
        string user = txtUsername.Text.Trim();
        string pass = txtPassword.Password;
        string confirm = txtConfirm.Password;
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            txtError.Text = "用户名和密码不能为空";
            return;
        }
        if (pass != confirm)
        {
            txtError.Text = "两次密码不一致";
            return;
        }

        string hash = ComputeSha256Hash(pass);
        try
        {
            var res = await _httpClient.PostAsJsonAsync($"{AppConfig.ServerBaseUrl}/api/auth/register", 
                new { Username = user, Password = hash });
            if (res.IsSuccessStatusCode)
            {
                Username = user;
                Password = pass;
                DialogResult = true;
            }
            else
            {
                var err = await res.Content.ReadAsStringAsync();
                txtError.Text = err;
            }
        }
        catch (Exception ex) { txtError.Text = ex.Message; }
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