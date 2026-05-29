using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Win32;

namespace ChatClientWpf;

public partial class MainWindow : Window
{
    private HubConnection? _connection;
    private string? _token;
    private string _username = "";
    private readonly HttpClient _httpClient = new HttpClient();
    private System.Windows.Threading.DispatcherTimer? _announcementTimer;
    private System.Windows.Threading.DispatcherTimer? _onlineUsersTimer;
    private ObservableCollection<MessageItem> _messages = new();

    // 音乐
    private MediaPlayer? _musicPlayer;
    private List<(string url, string title)> _playlist = new();
    private bool _isPlaying;
    private string _cycleMode = "list";

    // 头像
    private string _currentAvatar = "";
    private static readonly BitmapImage DefaultAvatar = new BitmapImage(new Uri("https://via.placeholder.com/40/cccccc/ffffff?text=?"));

    private static readonly string TokenFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "token.dat");
    private static readonly string UserFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "user.dat");

    private bool _isLoggingOut = false;
    private bool _isClosed = false;

    public MainWindow()
    {
        InitializeComponent();
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
        lbMessages.ItemsSource = _messages;
    }

    public void InitializeAndStart(string token, string username)
    {
        _token = token;
        _username = username;
        SaveCredentials();
        txtMyUsername.Text = username;
        _ = StartConnectionAsync();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) { }

    private void SaveCredentials()
    {
        File.WriteAllText(TokenFile, _token ?? "");
        File.WriteAllText(UserFile, _username);
    }

    private void ClearCredentials()
    {
        if (File.Exists(TokenFile)) File.Delete(TokenFile);
        if (File.Exists(UserFile)) File.Delete(UserFile);
    }

    // 将相对路径转换为完整URL
    private string GetFullUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        if (url.StartsWith("http://") || url.StartsWith("https://")) return url;
        return AppConfig.ServerBaseUrl + url;
    }

    private async Task StartConnectionAsync()
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(AppConfig.ChatHubUrl, o => o.AccessTokenProvider = () => Task.FromResult(_token!))
            .WithAutomaticReconnect()
            .Build();

        // 接收消息
        _connection.On<string, string, string, string, string>("ReceiveMessage", (user, text, type, avatar, fileName) =>
        {
            Dispatcher.Invoke(() =>
            {
                string displayText = type switch
                {
                    "image" => "[图片]",
                    "file" => $"[文件] {fileName ?? ""}",
                    _ => text
                };
                _messages.Add(new MessageItem
                {
                    User = user,
                    Text = displayText,
                    Avatar = ParseAvatar(GetFullUrl(avatar))
                });
            });
        });

        _connection.On<string, string>("UserJoined", (user, avatar) =>
            Dispatcher.Invoke(() => _messages.Add(new MessageItem { User = "系统", Text = $"{user} 加入了聊天室", Avatar = ParseAvatar(GetFullUrl(avatar)) })));

        _connection.On<string>("UserLeft", user =>
            Dispatcher.Invoke(() => _messages.Add(new MessageItem { User = "系统", Text = $"{user} 离开了聊天室", Avatar = DefaultAvatar })));

        _connection.On<string>("Kickout", msg => Dispatcher.Invoke(() => { MessageBox.Show(msg, "被踢下线"); Logout(); }));
        _connection.On<string>("Muted", msg => Dispatcher.Invoke(() => MessageBox.Show(msg, "禁言通知")));

        _connection.On<string>("Announcement", text =>
            Dispatcher.Invoke(() => { txtAnnouncement.Text = text; brdAnnouncement.Visibility = Visibility.Visible; }));

        // 音乐状态更新
        _connection.On<MusicState>("MusicStateUpdated", state => Dispatcher.Invoke(() =>
        {
            if (state != null && state.CurrentUrl != null)
                state.CurrentUrl = GetFullUrl(state.CurrentUrl);
            UpdateMusicState(state);
        }));

        _connection.On<List<PlaylistItem>>("PlaylistUpdated", list => Dispatcher.Invoke(() =>
            _playlist = list.Select(p => (GetFullUrl(p.Url), p.Title)).ToList()));

        // 头像变更
        _connection.On<string, string>("AvatarChanged", (user, newAvatar) =>
        {
            Dispatcher.Invoke(() =>
            {
                var bmp = ParseAvatar(GetFullUrl(newAvatar));
                foreach (var msg in _messages)
                    if (msg.User == user) msg.Avatar = bmp;
                if (user == _username)
                {
                    _currentAvatar = GetFullUrl(newAvatar);
                    UpdateMyAvatarDisplay(bmp);
                }
            });
        });

        _connection.Closed += async (error) =>
        {
            if (_isLoggingOut) return;
            await Task.Delay(1000);
            if (_connection != null && _connection.State != HubConnectionState.Disconnected)
                await _connection.StartAsync();
        };

        try
        {
            await _connection.StartAsync();
            await _connection.InvokeAsync("Join", _currentAvatar);

            // 历史消息
            var history = await _connection.InvokeAsync<List<MessageHistoryItem>>("GetRecentMessages");
            foreach (var m in history)
            {
                string display = m.Text;
                if (m.Type == "image") display = "[图片]";
                else if (m.Type == "file") display = $"[文件] {m.FileName ?? ""}";
                _messages.Add(new MessageItem
                {
                    User = m.User,
                    Text = display,
                    Avatar = ParseAvatar(GetFullUrl(m.Avatar))
                });
            }

            // 初始化自己的头像
            var myLastMsg = _messages.LastOrDefault(m => m.User == _username);
            if (myLastMsg?.Avatar != null)
            {
                _currentAvatar = myLastMsg.Avatar is BitmapImage bmp ? bmp.UriSource?.ToString() ?? "" : "";
                UpdateMyAvatarDisplay(myLastMsg.Avatar);
            }
            else
            {
                UpdateMyAvatarDisplay(DefaultAvatar);
            }

            // 音乐初始化（带重试）
            bool musicInitialized = false;
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    if (_connection.State == HubConnectionState.Connected)
                    {
                        var musicState = await _connection.InvokeAsync<MusicState>("GetMusicState");
                        if (musicState != null && musicState.CurrentUrl != null)
                            musicState.CurrentUrl = GetFullUrl(musicState.CurrentUrl);
                        UpdateMusicState(musicState);
                        musicInitialized = true;
                        break;
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"音乐重试{i + 1}: {ex.Message}"); if (i < 2) await Task.Delay(1000); }
            }
            if (!musicInitialized) { UpdateMusicState(null); Dispatcher.Invoke(() => MessageBox.Show("音乐服务暂不可用")); }

            // 播放列表
            try
            {
                var serverPlaylist = await _connection.InvokeAsync<List<PlaylistItem>>("GetPlaylist");
                _playlist = serverPlaylist.Select(p => (GetFullUrl(p.Url), p.Title)).ToList();
            }
            catch { }

            // 公告
            var announcement = await _connection.InvokeAsync<string>("GetAdminAnnouncement");
            if (!string.IsNullOrEmpty(announcement))
            {
                txtAdminAnnouncement.Text = announcement;
                brdAdminAnnouncement.Visibility = Visibility.Visible;
            }

            _announcementTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _announcementTimer.Tick += async (s, ev) =>
            {
                try { if (_connection?.State == HubConnectionState.Connected) { var ann = await _connection.InvokeAsync<string>("GetAdminAnnouncement"); if (!string.IsNullOrEmpty(ann)) Dispatcher.Invoke(() => { txtAdminAnnouncement.Text = ann; brdAdminAnnouncement.Visibility = Visibility.Visible; }); } } catch { }
            };
            _announcementTimer.Start();

            _onlineUsersTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _onlineUsersTimer.Tick += async (s, ev) => { try { await LoadOnlineUsers(); } catch { } };
            _onlineUsersTimer.Start();
            await LoadOnlineUsers();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"连接失败: {ex.Message}");
            Logout();
        }
    }

    private async Task LoadOnlineUsers()
    {
        try { var users = await _httpClient.GetFromJsonAsync<List<string>>($"{AppConfig.ServerBaseUrl}/api/admin/onlineUsers"); if (users != null) lbOnlineUsers.ItemsSource = users; } catch { }
    }

    private static BitmapImage ParseAvatar(string url)
    {
        if (string.IsNullOrEmpty(url)) return DefaultAvatar;
        try { return new BitmapImage(new Uri(url)); } catch { return DefaultAvatar; }
    }

    private void UpdateMyAvatarDisplay(ImageSource source) => imgMyAvatar.Source = source;

    private void UpdateMusicState(MusicState state)
    {
        if (state == null || string.IsNullOrEmpty(state.CurrentUrl))
        {
            txtCurrentSong.Text = "未播放";
            btnPlayPause.IsEnabled = false;
            btnCycleMode.IsEnabled = false;
            return;
        }

        txtCurrentSong.Text = state.CurrentTitle ?? "未知";
        btnPlayPause.IsEnabled = true;
        btnCycleMode.IsEnabled = true;
        _cycleMode = state.CycleMode;
        btnCycleMode.Content = _cycleMode == "single" ? "🔁 单曲" : "🔁 列表";

        try
        {
            // 如果当前播放器没有加载，或者URL不同，则加载新音频
            if (_musicPlayer == null || _musicPlayer.Source?.AbsoluteUri != state.CurrentUrl)
                PlayUrl(state.CurrentUrl);

            // 根据状态播放或暂停
            if (state.IsPlaying && !_isPlaying)
                _musicPlayer?.Play();
            else if (!state.IsPlaying && _isPlaying)
                _musicPlayer?.Pause();

            _isPlaying = state.IsPlaying;
            btnPlayPause.Content = _isPlaying ? "⏸️ 暂停" : "▶️ 播放";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"音乐播放出错: {ex.Message}");
        }
    }

    private void PlayUrl(string url)
    {
        StopMusic();
        try
        {
            _musicPlayer = new MediaPlayer();
            _musicPlayer.Open(new Uri(url));
            _musicPlayer.MediaEnded += (s, e) => _ = NextTrack();
            _musicPlayer.Play();
            _isPlaying = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法播放: {ex.Message}");
            StopMusic();
        }
    }

    private void StopMusic()
    {
        _musicPlayer?.Stop();
        _musicPlayer?.Close();
        _musicPlayer = null;
        _isPlaying = false;
    }

    private async Task NextTrack()
    {
        if (_connection?.State == HubConnectionState.Connected)
            await _connection.InvokeAsync("NextMusic");
    }

    // 暂停/播放按钮（已修复，确保调用后端正确方法）
    private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_connection?.State != HubConnectionState.Connected)
            {
                MessageBox.Show("未连接到服务器");
                return;
            }

            if (_isPlaying)
                _connection.InvokeAsync("PauseMusic");      // 暂停
            else
                _connection.InvokeAsync("ResumeMusic");      // 播放
        }
        catch (Exception ex)
        {
            MessageBox.Show($"操作失败: {ex.Message}");
        }
    }

    private void BtnNextTrack_Click(object sender, RoutedEventArgs e) => _ = NextTrack();

    private void BtnCycleMode_Click(object sender, RoutedEventArgs e)
    {
        try { _connection?.InvokeAsync("SetCycleMode", _cycleMode == "single" ? "list" : "single"); } catch { }
    }

    private void BtnStopMusic_Click(object sender, RoutedEventArgs e)
    {
        try { _connection?.InvokeAsync("StopMusic"); } catch { }
    }

    private void BtnShowPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var win = new Window { Title = "播放列表", Width = 300, Height = 400, Owner = this };
        var listBox = new ListBox();
        listBox.ItemsSource = _playlist.Select(p => $"{p.title}");
        listBox.MouseDoubleClick += (s, ev) =>
        {
            if (listBox.SelectedIndex >= 0 && listBox.SelectedIndex < _playlist.Count)
                _connection?.InvokeAsync("PlayMusic", _playlist[listBox.SelectedIndex].url, _playlist[listBox.SelectedIndex].title);
        };
        win.Content = listBox;
        win.Show();
    }

    // 聊天发送
    private void BtnSend_Click(object sender, RoutedEventArgs e) => SendText();
    private void TxtMessage_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) SendText(); }
    private async void SendText()
    {
        string text = txtMessage.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        try { await _connection!.InvokeAsync("SendMessage", text); } catch (Exception ex) { MessageBox.Show($"发送失败: {ex.Message}"); }
        txtMessage.Clear();
    }

    private async void BtnUploadImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "图片|*.jpg;*.jpeg;*.png;*.gif;*.webp" };
        if (dlg.ShowDialog() == true)
        {
            var url = await UploadFileStreamAsync(dlg.FileName);
            if (url != null) await _connection!.InvokeAsync("SendImage", url);
        }
    }

    private async void BtnUploadFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog();
        if (dlg.ShowDialog() == true)
        {
            string filePath = dlg.FileName;
            var (url, fileName) = await UploadFileWithProgressAsync(filePath);
            if (url != null)
            {
                await _connection!.InvokeAsync("SendFile", url, fileName);
                if (IsAudioFile(filePath))
                {
                    string fullUrl = GetFullUrl(url);
                    await _connection.InvokeAsync("AddToPlaylist", fullUrl, fileName);
                }
            }
        }
    }

    private bool IsAudioFile(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext is ".mp3" or ".wav" or ".ogg" or ".aac" or ".flac" or ".m4a" or ".wma" or ".opus";
    }

    private async Task<string?> UploadFileStreamAsync(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var content = new MultipartFormDataContent();
            var fileContent = new StreamContent(stream);
            fileContent.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data")
            { Name = "\"file\"", FileName = $"\"{Path.GetFileName(filePath)}\"" };
            content.Add(fileContent);
            var response = await _httpClient.PostAsync($"{AppConfig.ServerBaseUrl}/api/upload", content);
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<UploadResult>())?.Url;
            MessageBox.Show($"上传失败 (HTTP {(int)response.StatusCode})"); return null;
        }
        catch (Exception ex) { MessageBox.Show($"上传异常: {ex.Message}"); return null; }
    }

    private async Task<(string? url, string? fileName)> UploadFileWithProgressAsync(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var progress = new Progress<long>(total => Dispatcher.Invoke(() => { pbUpload.Visibility = Visibility.Visible; if (stream.Length > 0) pbUpload.Value = (double)total / stream.Length * 100; }));
            using var content = new MultipartFormDataContent();
            var fileContent = new ProgressStreamContent(stream, progress);
            fileContent.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data")
            { Name = "\"file\"", FileName = $"\"{Path.GetFileName(filePath)}\"" };
            content.Add(fileContent);
            var response = await _httpClient.PostAsync($"{AppConfig.ServerBaseUrl}/api/upload", content);
            Dispatcher.Invoke(() => pbUpload.Visibility = Visibility.Collapsed);
            if (response.IsSuccessStatusCode) return ((await response.Content.ReadFromJsonAsync<UploadResult>())?.Url, Path.GetFileName(filePath));
            MessageBox.Show($"上传失败 (HTTP {(int)response.StatusCode})"); return (null, null);
        }
        catch (Exception ex) { Dispatcher.Invoke(() => pbUpload.Visibility = Visibility.Collapsed); MessageBox.Show($"上传异常: {ex.Message}"); return (null, null); }
    }

    // 头像更换
    private void BtnAvatar_Click(object sender, MouseButtonEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "图片文件|*.jpg;*.jpeg;*.png;*.gif;*.webp" };
        if (dlg.ShowDialog() == true) _ = UploadAvatarAsync(dlg.FileName);
    }

    private async Task UploadAvatarAsync(string filePath)
    {
        var url = await UploadFileStreamAsync(filePath);
        if (url != null)
        {
            string fullUrl = GetFullUrl(url);
            _currentAvatar = fullUrl;
            UpdateMyAvatarDisplay(ParseAvatar(fullUrl));
            if (_connection?.State == HubConnectionState.Connected) await _connection.InvokeAsync("UpdateAvatar", url);
        }
    }

    private void BtnClearMessages_Click(object sender, RoutedEventArgs e)
    {
        if (_messages.Count == 0) return;
        if (MessageBox.Show("确认清除？", "提示", MessageBoxButton.YesNo) == MessageBoxResult.Yes) _messages.Clear();
    }

    private void BtnEmoji_Click(object sender, RoutedEventArgs e) => txtMessage.Text += "😊";

    private bool _suppressOnlineSelection = false;
    private void LbOnlineUsers_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOnlineSelection) return;
        if (e.AddedItems.Count > 0 && lbOnlineUsers.SelectedItem is string user)
        {
            txtMessage.Text += $"@{user} ";
            _suppressOnlineSelection = true;
            lbOnlineUsers.SelectedIndex = -1;
            _suppressOnlineSelection = false;
        }
    }

    private void BtnLogout_Click(object sender, RoutedEventArgs e) => Logout();

    private async void Logout()
    {
        if (_isClosed) return;
        _isClosed = true;
        _isLoggingOut = true;
        if (_connection != null)
        {
            try { if (_connection.State == HubConnectionState.Connected) await _connection.StopAsync(); await _connection.DisposeAsync(); } catch { }
            _connection = null;
        }
        _announcementTimer?.Stop(); _announcementTimer = null;
        _onlineUsersTimer?.Stop(); _onlineUsersTimer = null;
        ClearCredentials();
        Dispatcher.Invoke(() =>
        {
            _messages.Clear();
            lbOnlineUsers.ItemsSource = null;
            txtMyUsername.Text = "未登录";
            UpdateMyAvatarDisplay(DefaultAvatar);
            brdMusicBar.Visibility = Visibility.Visible;
            StopMusic();
            this.Close();
        });
        Application.Current.Dispatcher.BeginInvoke(new Action(() => { new LoginWindow().Show(); }));
    }

    private async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isClosed) { e.Cancel = true; Logout(); return; }
        if (_connection != null) { try { if (_connection.State == HubConnectionState.Connected) await _connection.StopAsync(); await _connection.DisposeAsync(); } catch { } }
        _announcementTimer?.Stop();
        _onlineUsersTimer?.Stop();
    }

    private void HideAdminAnnouncement_Click(object sender, RoutedEventArgs e) => brdAdminAnnouncement.Visibility = Visibility.Collapsed;
    private void HideAnnouncement_Click(object sender, RoutedEventArgs e) => brdAnnouncement.Visibility = Visibility.Collapsed;
}

// 辅助类
public class MessageItem { public string User { get; set; } = ""; public string Text { get; set; } = ""; public ImageSource? Avatar { get; set; } }
public class MessageHistoryItem { public string User { get; set; } = ""; public string Text { get; set; } = ""; public string Type { get; set; } = "text"; public string Avatar { get; set; } = ""; public string? FileName { get; set; } }
public class MusicState { public string? CurrentUrl { get; set; } public string? CurrentTitle { get; set; } public double CurrentTime { get; set; } public bool IsPlaying { get; set; } public string CycleMode { get; set; } = "list"; }
public class PlaylistItem { public string Url { get; set; } = ""; public string Title { get; set; } = ""; }
public class UploadResult { public string Url { get; set; } = ""; }
public class LoginResponse { public string Token { get; set; } = ""; }
public class ProgressStreamContent : HttpContent
{
    private readonly Stream _stream;
    private readonly IProgress<long> _progress;
    public ProgressStreamContent(Stream stream, IProgress<long> progress) { _stream = stream; _progress = progress; }
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        var buffer = new byte[8192]; long total = 0; int read;
        while ((read = await _stream.ReadAsync(buffer, 0, buffer.Length)) > 0) { await stream.WriteAsync(buffer, 0, read); total += read; _progress.Report(total); }
    }
    protected override bool TryComputeLength(out long length) { if (_stream.CanSeek) { length = _stream.Length; return true; } length = -1; return false; }
}