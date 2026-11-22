using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using ByteAtlas.Remote;

namespace ByteAtlas.Dialogs;

/// <summary>A source kept in the settings (secrets left out or DPAPI-protected, see RemoteSpec.ForSaving).</summary>
public sealed class SavedRemote
{
    public string Name { get; set; } = "";
    public RemoteSpec Spec { get; set; } = new();
}

/// <summary>Asks where a remote scan reads from: SSH server, S3 bucket, Azure Blob container, WebDAV folder or disk image.</summary>
public sealed class RemoteDialog : DialogWindow
{
    private static readonly (RemoteKind Kind, string Label)[] Kinds =
    [
        (RemoteKind.Ssh, "Linux / Unix server (SSH)"),
        (RemoteKind.S3, "Amazon S3 or S3-compatible bucket"),
        (RemoteKind.AzureBlob, "Azure Blob Storage container"),
        (RemoteKind.WebDav, "WebDAV folder (Nextcloud, SharePoint, …)"),
        (RemoteKind.DiskImage, "Disk image (VHDX, VHD, ISO)"),
    ];

    private static readonly string[] NewSource = ["(new source)"];

    private readonly AppSettings _settings;
    private readonly ComboBox _saved = new() { MinWidth = 260 };
    private readonly ComboBox _kind = new() { ItemsSource = Kinds.Select(k => k.Label).ToList(), SelectedIndex = 0, MinWidth = 260 };
    private readonly Grid _fields = new() { ColumnDefinitions = new ColumnDefinitions("150,*"), RowSpacing = 8, ColumnSpacing = 10 };
    private readonly TextBox _host = new() { PlaceholderText = "server.example.com" };
    private readonly TextBox _port = new() { Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _user = new();
    private readonly TextBox _password = new() { PasswordChar = '•' };
    private readonly TextBox _keyFile = new() { PlaceholderText = "optional: id_ed25519, id_rsa (OpenSSH or PEM)" };
    private readonly TextBox _path = new();
    private readonly CheckBox _oneFs = new() { Content = "Stay on one file system (do not follow other mounts)", IsChecked = true };
    private readonly TextBox _bucket = new();
    private readonly TextBox _region = new() { PlaceholderText = "us-east-1" };
    private readonly TextBox _endpoint = new() { PlaceholderText = "empty for Amazon; e.g. https://s3.wasabisys.com, http://nas:9000" };
    private readonly TextBox _accessKey = new();
    private readonly TextBox _url = new();
    private readonly CheckBox _remember = new() { Content = "Remember the password (encrypted for this Windows user)" };
    private readonly TextBox _saveAs = new() { PlaceholderText = "name to keep this source under (optional)" };
    private readonly Button _forget = new() { Content = "Forget host key" };
    private readonly Button _delete = new() { Content = "Delete" };

    public RemoteSpec? Result { get; private set; }

    private RemoteKind Kind => Kinds[Math.Max(0, _kind.SelectedIndex)].Kind;

    private RemoteDialog(AppSettings settings, RemoteSpec? start) : base("Scan a remote source", 620)
    {
        _settings = settings;
        _remember.IsVisible = OperatingSystem.IsWindows();
        _kind.SelectionChanged += (_, _) => Layout();
        _saved.SelectionChanged += (_, _) =>
        {
            if (_saved.SelectedIndex > 0 && _saved.SelectedIndex - 1 < settings.Remotes.Count)
            {
                var r = settings.Remotes[_saved.SelectedIndex - 1];
                Fill(r.Spec.Restored());
                _saveAs.Text = r.Name;
                _remember.IsChecked = r.Spec.PasswordProtected != null;
            }
            _delete.IsEnabled = _saved.SelectedIndex > 0;
        };
        _delete.Click += (_, _) =>
        {
            if (_saved.SelectedIndex <= 0)
                return;
            settings.Remotes.RemoveAt(_saved.SelectedIndex - 1);
            settings.Save();
            FillSaved();
        };
        _forget.Click += async (_, _) =>
        {
            var key = $"{_host.Text?.Trim()}:{PortValue()}";
            var gone = RemoteSources.ForgetHost(key);
            await MessageBox.ShowAsync(this, gone
                ? $"The host key of {key} is forgotten. The next connection trusts the key the server shows then."
                : $"No host key is remembered for {key}.", MessageKind.Info, Title ?? "");
        };
        FillSaved();
        if (start != null)
        {
            var match = settings.Remotes.FindIndex(r => r.Spec.Kind == start.Kind && r.Spec.Display == start.Display);
            if (match >= 0)
                _saved.SelectedIndex = match + 1;
            else
                Fill(start);
        }
        Layout();

        Avalonia.Automation.AutomationProperties.SetName(_saved, "Saved sources");
        Avalonia.Automation.AutomationProperties.SetName(_kind, "Type");
        Avalonia.Automation.AutomationProperties.SetName(_saveAs, "Save as");
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 8, ColumnSpacing = 10 };
        Put(top, 0, 0, Label("Saved sources:"));
        Put(top, 0, 1, _saved);
        Put(top, 0, 2, _delete);
        Put(top, 1, 0, Label("Type:"));
        Put(top, 1, 1, _kind);

        var saveRow = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), ColumnSpacing = 10 };
        Put(saveRow, 0, 0, Label("Save as:"));
        Put(saveRow, 0, 1, _saveAs);

        SetBody(Column(10, top, Rule(), _fields, Rule(), _remember, saveRow), "Scan");
        MinWidth = 520;
    }

    public static async Task<RemoteSpec?> AskAsync(Window owner, AppSettings settings, RemoteSpec? start = null)
    {
        var d = new RemoteDialog(settings, start);
        return await d.ShowDialog<bool>(owner) ? d.Result : null;
    }

    private void FillSaved()
    {
        _saved.ItemsSource = NewSource.Concat(_settings.Remotes.Select(r => r.Name)).ToList();
        _saved.SelectedIndex = 0;
        _delete.IsEnabled = false;
    }

    private void Fill(RemoteSpec r)
    {
        _kind.SelectedIndex = Array.FindIndex(Kinds, k => k.Kind == r.Kind);
        _host.Text = r.Host;
        _port.Text = r.Port > 0 ? r.Port.ToString(CultureInfo.InvariantCulture) : "";
        _user.Text = r.User;
        _password.Text = r.Password;
        _keyFile.Text = r.KeyFile;
        _path.Text = r.Path;
        _oneFs.IsChecked = r.StayOnFileSystem;
        _bucket.Text = r.Bucket;
        _region.Text = r.Region == "us-east-1" ? "" : r.Region;
        _endpoint.Text = r.Endpoint;
        _accessKey.Text = r.AccessKey;
        _url.Text = r.Url;
    }

    private int PortValue() => int.TryParse(_port.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536 ? p : 22;

    private static void Put(Grid g, int row, int col, Control c)
    {
        while (g.RowDefinitions.Count <= row)
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(c, row);
        Grid.SetColumn(c, col);
        g.Children.Add(c);
    }

    /// <summary>Shows the fields of the chosen type.</summary>
    private void Layout()
    {
        // the inputs are reused: take them out of the wrappers of the previous type first
        Control[] inputs = [_host, _port, _user, _password, _keyFile, _path, _oneFs, _bucket, _region, _endpoint, _accessKey, _url, _forget];
        foreach (var c in inputs)
            if (c.Parent is Panel p)
                p.Children.Remove(c);
        _fields.Children.Clear();
        _fields.RowDefinitions.Clear();
        var row = 0;
        void Field(string label, Control c, string? hint = null)
        {
            var l = Label(label);
            // screen readers (and UI tests) find each input by its label
            var input = c is Grid g ? g.Children.OfType<TextBox>().FirstOrDefault() : c;
            if (input != null && label.Length > 0)
                Avalonia.Automation.AutomationProperties.SetName(input, label.TrimEnd(':'));
            if (hint != null)
            {
                // line up with the input, not with the input + hint block
                l.VerticalAlignment = VerticalAlignment.Top;
                l.Margin = new Thickness(0, 6, 0, 0);
            }
            Put(_fields, row, 0, l);
            Put(_fields, row, 1, hint == null ? c : Column(2, c, Label(hint, muted: true)));
            row++;
        }
        _remember.IsVisible = OperatingSystem.IsWindows() && Kind != RemoteKind.DiskImage;
        switch (Kind)
        {
            case RemoteKind.Ssh:
                Field("Server:", _host);
                Field("Port:", _port);
                Field("User:", _user);
                Field("Password:", _password, "or the passphrase of the key file");
                Field("Key file:", PathRow(_keyFile, async () =>
                {
                    if (await PickFileAsync("Private key") is { } f)
                        _keyFile.Text = f;
                }));
                Field("Folder:", _path, "e.g. /home, /srv/data or ~ (empty = home folder). Needs GNU find on the server.");
                Field("", _oneFs);
                Field("", _forget);
                break;
            case RemoteKind.S3:
                Field("Bucket:", _bucket);
                Field("Prefix (folder):", _path, "empty = the whole bucket");
                Field("Access key:", _accessKey);
                Field("Secret key:", _password);
                Field("Region:", _region);
                Field("Endpoint:", _endpoint, "S3-compatible services (MinIO, Wasabi, Backblaze B2, Cloudflare R2, …)");
                break;
            case RemoteKind.AzureBlob:
                Field("Container URL:", _url, "with a SAS token that may list: https://account.blob.core.windows.net/container?sv=…&sig=…");
                Field("Prefix (folder):", _path, "empty = the whole container");
                break;
            case RemoteKind.WebDav:
                Field("URL:", _url, "e.g. https://cloud.example.com/remote.php/dav/files/me/");
                Field("User:", _user);
                Field("Password:", _password, "an app password where the service has them");
                break;
            default:
                Field("Image file:", PathRow(_url, async () =>
                {
                    if (await PickFileAsync("Disk image", new FilePickerFileType("Disk images") { Patterns = ["*.vhdx", "*.vhd", "*.iso"] }) is { } f)
                        _url.Text = f;
                }), "Mounted read-only while the scan is open (needs administrator rights), dismounted when it is removed.");
                break;
        }
    }

    private RemoteSpec Build()
    {
        static string t(TextBox b) => b.Text?.Trim() ?? "";
        return new RemoteSpec
        {
            Kind = Kind,
            Host = t(_host),
            Port = Kind == RemoteKind.Ssh ? PortValue() : 0,
            User = t(_user),
            Password = _password.Text ?? "",
            KeyFile = t(_keyFile).Trim('"'),
            Path = t(_path),
            StayOnFileSystem = _oneFs.IsChecked == true,
            Bucket = t(_bucket),
            Region = t(_region) is { Length: > 0 } r ? r : "us-east-1",
            Endpoint = t(_endpoint),
            PathStyle = t(_endpoint).Length > 0,
            AccessKey = t(_accessKey),
            Url = t(_url).Trim('"'),
        };
    }

    protected override async Task<bool> ValidateAsync()
    {
        var r = Build();
        var missing = r.Kind switch
        {
            RemoteKind.Ssh when r.Host.Length == 0 => "the server",
            RemoteKind.Ssh when r.User.Length == 0 => "the user name",
            RemoteKind.Ssh when r.KeyFile.Length > 0 && !File.Exists(r.KeyFile) => "an existing key file",
            RemoteKind.S3 when r.Bucket.Length == 0 => "the bucket",
            RemoteKind.S3 when r.AccessKey.Length == 0 || r.Password.Length == 0 => "the access key and secret key",
            RemoteKind.AzureBlob when !Uri.TryCreate(r.Url, UriKind.Absolute, out _) => "the container URL",
            RemoteKind.AzureBlob when !r.Url.Contains("sig=", StringComparison.Ordinal) => "a container URL with a SAS token (…?sv=…&sig=…)",
            RemoteKind.WebDav when !Uri.TryCreate(r.Url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") => "an http:// or https:// URL",
            RemoteKind.DiskImage when !File.Exists(r.Url) => "an existing image file",
            _ => null,
        };
        if (missing != null)
        {
            await MessageBox.ShowAsync(this, $"Please give {missing}.", MessageKind.Warning, Title ?? "");
            return false;
        }
        if (r.Kind == RemoteKind.S3 && r.Endpoint.Length > 0 && !Uri.TryCreate(r.Endpoint.Contains("://", StringComparison.Ordinal) ? r.Endpoint : "https://" + r.Endpoint, UriKind.Absolute, out _))
        {
            await MessageBox.ShowAsync(this, "The endpoint is not a valid address.", MessageKind.Warning, Title ?? "");
            return false;
        }
        if (_saveAs.Text?.Trim() is { Length: > 0 } name)
        {
            var saved = new SavedRemote { Name = name, Spec = r.ForSaving(_remember.IsChecked == true) };
            var i = _settings.Remotes.FindIndex(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
                _settings.Remotes[i] = saved;
            else
                _settings.Remotes.Add(saved);
            _settings.Save();
        }
        Result = r;
        return true;
    }
}
