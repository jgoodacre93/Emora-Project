using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Emora
{
    public partial class EmoraForm : Form
    {
        private int checkedWebsites = 0;
        private const int minColumnWidth = 360;
        private int results = 0;
        private class WebsiteInfo
        {
            public string ErrorType { get; set; }
            public string ErrorMessage { get; set; }
            public string SuccessMessage { get; set; }
            public string Url { get; set; }
            public string ProfileUrl { get; set; }
            public string Data { get; set; }
            public string Method { get; set; }
            public bool Disabled { get; set; }
            public Dictionary<string, string> Headers { get; set; }
        }

        private Dictionary<string, WebsiteInfo> websites;
        private readonly Dictionary<string, Image> websiteIcons = new Dictionary<string, Image>();

        public EmoraForm()
        {
            InitializeComponent();
            LoadEmbeddedResources();
            InitializeEvents();
            nudThreads.Value = Environment.ProcessorCount;
        }

        private void LoadEmbeddedResources()
        {
            var assembly = Assembly.GetExecutingAssembly();

            this.Icon = new Icon(assembly.GetManifestResourceStream("magnifier.ico"));
            using (var stream = assembly.GetManifestResourceStream("websites.json"))
            using (var reader = new StreamReader(stream))
            {
                var serializer = new JavaScriptSerializer();
                websites = serializer.Deserialize<Dictionary<string, WebsiteInfo>>(reader.ReadToEnd());
            }

            foreach (var name in websites.Keys)
            {
                var stream = assembly.GetManifestResourceStream($"{name}.png");
                if (stream != null)
                    websiteIcons[name] = Image.FromStream(stream);
            }
        }

        private void InitializeEvents()
        {
            Resize += (s, e) => UpdateTableLayoutColumns();
            Load += (s, e) => UpdateTableLayoutColumns();
            txtUsername.GotFocus += (s, e) => { if (txtUsername.Text == "Enter username here...") txtUsername.Text = ""; };
            txtUsername.LostFocus += (s, e) => { if (string.IsNullOrWhiteSpace(txtUsername.Text)) txtUsername.Text = "Enter username here..."; };
            txtUsername.TextChanged += FilterResultsByUsername;
            btnSearch.Click += BtnSearchClick;
        }

        private async void BtnSearchClick(object sender, EventArgs e)
        {
            string username = txtUsername.Text.Trim();
            if (string.IsNullOrEmpty(username) || username == "Enter username here...")
            {
                MessageBox.Show("Please enter a username to check.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            SetSearchUIState(true, username);
            Stopwatch stopwatch = Stopwatch.StartNew();

            using (var httpClient = CreateConfiguredHttpClient())
            {
                var tasks = new List<Task>();
                var semaphore = new SemaphoreSlim((int)nudThreads.Value);

                foreach (var website in websites)
                {
                    if (website.Value.Disabled)
                    {
                        checkedWebsites++;
                        continue;
                    }

                    await semaphore.WaitAsync();
                    var task = Task.Run(async () =>
                    {
                        try
                        {
                            await CheckWebsite(username, website.Key, website.Value, httpClient);
                        }
                        finally
                        {
                            checkedWebsites++;
                            Invoke(new Action(() =>
                            {
                                Text = $"Emora | Checking username {username} [{results} accounts found] [{checkedWebsites}/{websites.Count} websites checked]";
                            }));
                            semaphore.Release();
                        }
                    });
                    tasks.Add(task);
                }
                await Task.WhenAll(tasks);

                stopwatch.Stop();
                Text = $"Emora | {results} accounts found for {username}";
                lblStatus.Text = $"{results} accounts found for {username} in {stopwatch.ElapsedMilliseconds / 1000.0} seconds";
                SetSearchUIState(false, username);
            }
        }

        private void SetSearchUIState(bool isSearching, string username)
        {
            btnSearch.Enabled = !isSearching;
            txtUsername.Enabled = !isSearching;

            if (isSearching)
            {
                Text = $"Emora | Checking username {username}";
                lblStatus.Text = $"Checking username {username}";
                checkedWebsites = 0;
                results = 0;
                tlpResults.RowStyles.Clear();
                tlpResults.Controls.Clear();
            }
        }

        private HttpClient CreateConfiguredHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(8)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:147.0) Gecko/20100101 Firefox/147.0");
            return client;
        }

        private async Task CheckWebsite(string username, string websiteName, WebsiteInfo websiteInfo, HttpClient httpClient)
        {
            try
            {
                var url = websiteInfo.Url.Replace("{}", username);
                string method = !string.IsNullOrEmpty(websiteInfo.Method)
                    ? websiteInfo.Method
                    : (!string.IsNullOrEmpty(websiteInfo.Data) ? "POST" : "GET");
                string profileUrl = string.IsNullOrEmpty(websiteInfo.ProfileUrl)
                    ? url
                    : websiteInfo.ProfileUrl.Replace("{}", username);

                var request = CreateHttpRequest(method, url, username, websiteInfo);
                var response = await httpClient.SendAsync(request);

                await ProcessResponse(response, websiteInfo, websiteName, profileUrl);
            }
            catch {}
        }

        private HttpRequestMessage CreateHttpRequest(string method, string url, string username, WebsiteInfo websiteInfo)
        {
            var request = new HttpRequestMessage(new HttpMethod(method), url);

            if (websiteInfo.Headers != null)
            {
                foreach (var header in websiteInfo.Headers)
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            if (!string.IsNullOrEmpty(websiteInfo.Data))
            {
                request.Content = new StringContent(websiteInfo.Data.Replace("{USERNAME}", username), Encoding.UTF8, "application/json");
            }

            return request;
        }

        private async Task ProcessResponse(HttpResponseMessage response, WebsiteInfo websiteInfo, string websiteName, string profileUrl)
        {
            if (websiteInfo.ErrorType == "status_code")
            {
                if (response.IsSuccessStatusCode)
                {
                    AddResult(websiteName, profileUrl);
                }
            }
            else if (websiteInfo.ErrorType == "message")
            {
                string content = await GetResponseContent(response);
                bool isErrorMessageAbsent = string.IsNullOrEmpty(websiteInfo.ErrorMessage) || !content.Contains(websiteInfo.ErrorMessage);
                bool isSuccessMessagePresent = string.IsNullOrEmpty(websiteInfo.SuccessMessage) || content.Contains(websiteInfo.SuccessMessage);

                if (isErrorMessageAbsent && isSuccessMessagePresent)
                {
                    AddResult(websiteName, profileUrl);
                }
            }
        }

        private async Task<string> GetResponseContent(HttpResponseMessage response)
        {
            try
            {
                return await response.Content.ReadAsStringAsync();
            }
            catch (InvalidOperationException)
            {
                var stream = await response.Content.ReadAsStreamAsync();
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return await reader.ReadToEndAsync();
                }
            }
        }

        private void AddResult(string websiteName, string url)
        {
            results++;
            var panel = CreateResultPanel(websiteName, url);

            Invoke(new Action(() =>
            {
                tlpResults.Controls.Add(panel);
                lblStatus.Text = $"{results} accounts found";

                UpdateTableLayoutColumns();
            }));
        }

        private Panel CreateResultPanel(string websiteName, string url)
        {
            var panel = new Panel
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Color.FromArgb(66, 66, 66),
                BorderStyle = BorderStyle.None,
                Cursor = Cursors.Hand,
                Height = 80,
                TabIndex = tlpResults.Controls.Count,
                Tag = websiteName.ToLower()
            };

            PictureBox pictureBox = new PictureBox
            {
                Image = websiteIcons.TryGetValue(websiteName, out var icon) ? icon : null,
                Size = new Size(64, 64),
                SizeMode = PictureBoxSizeMode.Zoom,
                Cursor = Cursors.Hand
            };
            pictureBox.Location = new Point((panel.Width - pictureBox.Width) / 8, (panel.Height - pictureBox.Height) / 2);
            panel.Controls.Add(pictureBox);

            var label = new Label
            {
                Text = websiteName,
                AutoSize = true,
                Font = new Font(Font.FontFamily, 18),
                Cursor = Cursors.Hand
            };
            label.Location = new Point(pictureBox.Right + 10, (panel.Height - label.Height) / 3);
            panel.Controls.Add(label);

            SetupResultPanelEvents(panel, label, pictureBox, url);

            return panel;
        }

        private void SetupResultPanelEvents(Panel panel, Label label, PictureBox pictureBox, string url)
        {
            Uri uri = new Uri(url);
            string domain = uri.GetLeftPart(UriPartial.Authority);

            panel.Click += (sender, e) => Process.Start(url);
            pictureBox.Click += (sender, e) => Process.Start(domain);
            label.Click += (sender, e) => Process.Start(domain);

            ToolTip toolTip = new ToolTip
            {
                AutoPopDelay = 4000,
                InitialDelay = 800,
                ReshowDelay = 500,
                ShowAlways = true
            };
            toolTip.SetToolTip(panel, "View User Profile");
            toolTip.SetToolTip(label, "Visit Website");
            toolTip.SetToolTip(pictureBox, "Visit Website");
        }

        private void UpdateTableLayoutColumns()
        {
            int availableWidth = tlpResults.Width;
            int columnCount = Math.Max(1, (availableWidth - 10) / minColumnWidth);

            if (tlpResults.ColumnCount == columnCount)
                return;

            tlpResults.SuspendLayout();

            tlpResults.ColumnCount = columnCount;
            tlpResults.ColumnStyles.Clear();

            float columnWidth = 100f / columnCount;
            for (int i = 0; i < columnCount; i++)
            {
                tlpResults.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, columnWidth));
            }

            tlpResults.ResumeLayout(true);
        }

        private void FilterResultsByUsername(object sender, EventArgs e)
        {
            string filterText = txtUsername.Text.ToLower();
            tlpResults.SuspendLayout();

            foreach (Control control in tlpResults.Controls)
            {
                if (control is Panel panel && panel.Tag is string websiteName)
                {
                    bool matches = websiteName.Contains(filterText);
                    panel.Visible = matches;
                }
            }

            tlpResults.ResumeLayout(true);
        }
    }
}
