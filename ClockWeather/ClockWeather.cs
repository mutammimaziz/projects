// ClockWeather: borderless desktop clock with current weather (Open-Meteo, no API key)
// and a live ADS-B flight map of the sky around you (see FlightMap.cs).
// Targets .NET Framework 4.8, which ships with Windows 10/11, so the exe needs no runtime install.
// Written to C# 5 so Windows' built-in compiler can rebuild it: see build.cmd.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ClockWeather
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main()
        {
            try { SetProcessDPIAware(); } catch { }
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            ServicePointManager.DefaultConnectionLimit = 8;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ClockForm(Settings.Load()));
        }
    }

    // clock.ini next to the exe. Created with defaults on first run; new keys are added on save.
    internal sealed class Settings
    {
        public string LocationName = "Singapore";
        public double Latitude = 1.3521;
        public double Longitude = 103.8198;
        public int WeatherRefreshMinutes = 10;
        public bool Use24Hour = true;
        public bool AlwaysOnTop = false;
        public int WindowX = -1;
        public int WindowY = -1;
        public int WindowWidth = 480;          // logical pixels (before DPI scaling)

        // Flight map
        public bool ShowFlightMap = true;
        public string FlightSource = "airplanes.live";   // airplanes.live | adsb.lol | adsb.fi | local
        public string LocalReceiverUrl = "http://localhost:8080/data/aircraft.json";
        public int FlightRadiusNm = 50;
        public int FlightRefreshSeconds = 5;
        public string MapStyle = "dark";                 // dark | light | street
        public int MapZoom = 9;
        public int MapHeight = 400;                      // logical pixels
        public bool ShowLabels = true;
        public bool ShowTrails = true;

        public static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "clock.ini"); }
        }

        public static Settings Load()
        {
            var s = new Settings();
            if (!File.Exists(FilePath)) { s.Save(); return s; }
            var inv = CultureInfo.InvariantCulture;
            foreach (var raw in File.ReadAllLines(FilePath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();
                try
                {
                    switch (k)
                    {
                        case "locationname": s.LocationName = v; break;
                        case "latitude": s.Latitude = double.Parse(v, inv); break;
                        case "longitude": s.Longitude = double.Parse(v, inv); break;
                        case "weatherrefreshminutes": s.WeatherRefreshMinutes = int.Parse(v, inv); break;
                        case "use24hour": s.Use24Hour = bool.Parse(v); break;
                        case "alwaysontop": s.AlwaysOnTop = bool.Parse(v); break;
                        case "windowx": s.WindowX = int.Parse(v, inv); break;
                        case "windowy": s.WindowY = int.Parse(v, inv); break;
                        case "windowwidth": s.WindowWidth = int.Parse(v, inv); break;
                        case "showflightmap": s.ShowFlightMap = bool.Parse(v); break;
                        case "flightsource": s.FlightSource = v.ToLowerInvariant(); break;
                        case "localreceiverurl": s.LocalReceiverUrl = v; break;
                        case "flightradiusnm": s.FlightRadiusNm = int.Parse(v, inv); break;
                        case "flightrefreshseconds": s.FlightRefreshSeconds = int.Parse(v, inv); break;
                        case "mapstyle": s.MapStyle = v.ToLowerInvariant(); break;
                        case "mapzoom": s.MapZoom = int.Parse(v, inv); break;
                        case "mapheight": s.MapHeight = int.Parse(v, inv); break;
                        case "showlabels": s.ShowLabels = bool.Parse(v); break;
                        case "showtrails": s.ShowTrails = bool.Parse(v); break;
                    }
                }
                catch { /* bad value: keep the default for that key */ }
            }
            s.FlightRadiusNm = Math.Max(5, Math.Min(250, s.FlightRadiusNm));
            s.FlightRefreshSeconds = Math.Max(2, s.FlightRefreshSeconds);
            if (s.MapStyle != "light" && s.MapStyle != "street") s.MapStyle = "dark";
            if (s.FlightSource != "local" && Array.IndexOf(AdsbFeed.PublicSources, s.FlightSource) < 0) s.FlightSource = "airplanes.live";
            return s;
        }

        public void Save()
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("# ClockWeather settings. Find coordinates at https://www.latlong.net");
            sb.AppendLine("LocationName=" + LocationName);
            sb.AppendLine("Latitude=" + Latitude.ToString(inv));
            sb.AppendLine("Longitude=" + Longitude.ToString(inv));
            sb.AppendLine("WeatherRefreshMinutes=" + WeatherRefreshMinutes.ToString(inv));
            sb.AppendLine("Use24Hour=" + Use24Hour);
            sb.AppendLine("AlwaysOnTop=" + AlwaysOnTop);
            sb.AppendLine("WindowX=" + WindowX.ToString(inv));
            sb.AppendLine("WindowY=" + WindowY.ToString(inv));
            sb.AppendLine("WindowWidth=" + WindowWidth.ToString(inv));
            sb.AppendLine();
            sb.AppendLine("# Flight map. FlightSource: airplanes.live, adsb.lol, adsb.fi, or local (your own dump1090/readsb receiver)");
            sb.AppendLine("ShowFlightMap=" + ShowFlightMap);
            sb.AppendLine("FlightSource=" + FlightSource);
            sb.AppendLine("LocalReceiverUrl=" + LocalReceiverUrl);
            sb.AppendLine("FlightRadiusNm=" + FlightRadiusNm.ToString(inv));
            sb.AppendLine("FlightRefreshSeconds=" + FlightRefreshSeconds.ToString(inv));
            sb.AppendLine("# MapStyle: dark, light, street");
            sb.AppendLine("MapStyle=" + MapStyle);
            sb.AppendLine("MapZoom=" + MapZoom.ToString(inv));
            sb.AppendLine("MapHeight=" + MapHeight.ToString(inv));
            sb.AppendLine("ShowLabels=" + ShowLabels);
            sb.AppendLine("ShowTrails=" + ShowTrails);
            try { File.WriteAllText(FilePath, sb.ToString()); } catch { }
        }
    }

    internal sealed class ClockForm : Form
    {
        private readonly Settings _settings;
        private readonly Label _time = new Label();
        private readonly Label _date = new Label();
        private readonly Label _weather = new Label();
        private readonly Label _detail = new Label();
        private readonly FlightMapView _map = new FlightMapView();
        private readonly FlightFeed _feed;
        private readonly Timer _clockTimer = new Timer();
        private readonly Timer _weatherTimer = new Timer();
        private int _lastSecond = -1;
        private bool _fetching, _layingOut;
        private Point _dragStart;
        private readonly float _scale;
        private readonly int _headerH, _pad;

        private static readonly Color Bg = Color.FromArgb(18, 20, 24);
        private static readonly Color Fg = Color.FromArgb(235, 238, 242);
        private static readonly Color Muted = Color.FromArgb(140, 148, 160);
        private static readonly Color Accent = Color.FromArgb(255, 184, 77);
        private static readonly Color Border = Color.FromArgb(48, 54, 62);

        public ClockForm(Settings settings)
        {
            _settings = settings;

            Text = "Clock";
            FormBorderStyle = FormBorderStyle.None;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Bg;
            DoubleBuffered = true;
            TopMost = _settings.AlwaysOnTop;
            ShowInTaskbar = true;

            _scale = DeviceDpi() / 96f;
            _pad = S(10);
            int width = S(Math.Max(360, _settings.WindowWidth));
            ClientSize = new Size(width, S(236));

            if (_settings.WindowX >= 0 && _settings.WindowY >= 0 && OnScreen(_settings.WindowX, _settings.WindowY))
            {
                StartPosition = FormStartPosition.Manual;
                Location = new Point(_settings.WindowX, _settings.WindowY);
            }
            else
            {
                StartPosition = FormStartPosition.CenterScreen;
            }

            // Stack labels top to bottom using each font's measured height.
            int y = S(14);
            y = AddLabel(_time, new Font("Segoe UI Light", 54f), Fg, y);
            y = AddLabel(_date, new Font("Segoe UI", 12f), Muted, y);
            y += S(14);
            y = AddLabel(_weather, new Font("Segoe UI Semibold", 15f), Accent, y);
            y += S(4);
            y = AddLabel(_detail, new Font("Segoe UI", 9.5f), Muted, y);
            _headerH = y + S(16);
            _weather.Text = "Loading weather...";

            // Flight map below the weather.
            _feed = new FlightFeed(_settings.FlightRefreshSeconds)
            {
                Lat = _settings.Latitude,
                Lon = _settings.Longitude,
                RadiusNm = _settings.FlightRadiusNm,
                Source = _settings.FlightSource,
                LocalUrl = _settings.LocalReceiverUrl
            };
            _feed.Updated += () => _map.Invalidate();
            _map.Feed = _feed;
            _map.HomeLat = _settings.Latitude;
            _map.HomeLon = _settings.Longitude;
            _map.Zoom = _settings.MapZoom;
            _map.ShowLabels = _settings.ShowLabels;
            _map.ShowTrails = _settings.ShowTrails;
            _map.Tiles.Style = _settings.MapStyle;
            _map.ResetView();
            _map.ViewChanged += delegate
            {
                if (_settings.MapZoom != _map.Zoom) { _settings.MapZoom = _map.Zoom; _settings.Save(); }
            };
            Controls.Add(_map);

            BuildMenu();
            ApplyMapVisibility(false);

            _clockTimer.Interval = 200;
            _clockTimer.Tick += delegate { UpdateClock(); };
            _weatherTimer.Interval = Math.Max(1, _settings.WeatherRefreshMinutes) * 60000;
            _weatherTimer.Tick += async delegate { await RefreshWeatherAsync(); };

            Load += async delegate
            {
                UpdateClock();
                _clockTimer.Start();
                _weatherTimer.Start();
                if (_settings.ShowFlightMap) _feed.Start();
                await RefreshWeatherAsync();
            };
        }

        private int S(double logical) { return (int)Math.Round(logical * _scale); }

        private static int DeviceDpi()
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) return (int)g.DpiX;
        }

        private static bool OnScreen(int x, int y)
        {
            foreach (var s in Screen.AllScreens)
                if (s.WorkingArea.Contains(x + 40, y + 40)) return true;
            return false;
        }

        // Places the label at 'top' and returns the y position just below it.
        private int AddLabel(Label l, Font f, Color c, int top)
        {
            l.Font = f;
            l.ForeColor = c;
            l.BackColor = Color.Transparent;
            l.AutoSize = false;
            l.UseMnemonic = false;
            l.TextAlign = ContentAlignment.MiddleCenter;
            int h;
            using (var g = CreateGraphics()) h = (int)Math.Ceiling(f.GetHeight(g)) + S(3); // headroom so GDI text isn't clipped
            l.SetBounds(0, top, ClientSize.Width, h);
            l.MouseDown += OnDragStart;
            l.MouseMove += OnDragMove;
            l.MouseUp += OnDragEnd;
            Controls.Add(l);
            return top + h;
        }

        // ------------------------------------------------------------ layout

        private void ApplyMapVisibility(bool save)
        {
            bool show = _settings.ShowFlightMap;
            _map.Visible = show;
            int minW = S(360);
            if (show)
            {
                MinimumSize = new Size(minW, _headerH + S(200) + _pad);
                MaximumSize = Size.Empty;
                ClientSize = new Size(ClientSize.Width, _headerH + S(Math.Max(200, _settings.MapHeight)) + _pad);
                if (IsHandleCreated && !_feed.Running) _feed.Start();
            }
            else
            {
                _feed.Stop();
                MinimumSize = new Size(minW, _headerH);
                MaximumSize = new Size(20000, _headerH);
                ClientSize = new Size(ClientSize.Width, _headerH);
            }
            DoLayout();
            if (save) _settings.Save();
        }

        private void DoLayout()
        {
            if (_layingOut) return;
            _layingOut = true;
            try
            {
                foreach (var l in new[] { _time, _date, _weather, _detail }) l.Width = ClientSize.Width;
                if (_settings.ShowFlightMap)
                    _map.SetBounds(_pad, _headerH, ClientSize.Width - 2 * _pad, Math.Max(10, ClientSize.Height - _headerH - _pad));
                var old = Region;
                Region = new Region(RoundedPath(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), S(18)));
                if (old != null) old.Dispose();
                using (var p = RoundedPath(new Rectangle(0, 0, _map.Width, _map.Height), S(8)))
                {
                    var oldMap = _map.Region;
                    _map.Region = new Region(p);
                    if (oldMap != null) oldMap.Dispose();
                }
                Invalidate();
            }
            finally { _layingOut = false; }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_map != null && _headerH > 0) DoLayout();
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            _settings.WindowX = Location.X;
            _settings.WindowY = Location.Y;
            _settings.WindowWidth = (int)Math.Round(ClientSize.Width / _scale);
            if (_settings.ShowFlightMap) _settings.MapHeight = (int)Math.Round((ClientSize.Height - _headerH - _pad) / _scale);
            _settings.Save();
        }

        // Bottom-right corner acts as a resize grip on this borderless window.
        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84, HTBOTTOMRIGHT = 17;
            base.WndProc(ref m);
            if (m.Msg == WM_NCHITTEST)
            {
                long lp = m.LParam.ToInt64();
                var pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                int grip = S(16);
                if (pt.X >= ClientSize.Width - grip && pt.Y >= ClientSize.Height - grip) m.Result = (IntPtr)HTBOTTOMRIGHT;
            }
        }

        // ------------------------------------------------------------ menu

        private void BuildMenu()
        {
            var menu = new ContextMenuStrip();

            var onTop = new ToolStripMenuItem("Always on top") { Checked = TopMost, CheckOnClick = true };
            onTop.CheckedChanged += delegate
            {
                TopMost = onTop.Checked;
                _settings.AlwaysOnTop = onTop.Checked;
                _settings.Save();
            };

            var h24 = new ToolStripMenuItem("24-hour time") { Checked = _settings.Use24Hour, CheckOnClick = true };
            h24.CheckedChanged += delegate
            {
                _settings.Use24Hour = h24.Checked;
                _settings.Save();
                _lastSecond = -1;
                UpdateClock();
            };

            var refresh = new ToolStripMenuItem("Refresh weather");
            refresh.Click += async delegate { await RefreshWeatherAsync(); };

            var edit = new ToolStripMenuItem("Edit location...");
            edit.Click += delegate
            {
                try { Process.Start("notepad.exe", "\"" + Settings.FilePath + "\""); } catch { }
                MessageBox.Show(this, "Save clock.ini, then restart the clock to apply the new location.\nThe flight map centres on the same coordinates.",
                    "ClockWeather", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            // --- flight map items
            var showMap = new ToolStripMenuItem("Show flight map") { Checked = _settings.ShowFlightMap, CheckOnClick = true };
            var mapItems = new List<ToolStripItem>();
            showMap.CheckedChanged += delegate
            {
                _settings.ShowFlightMap = showMap.Checked;
                foreach (var it in mapItems) it.Enabled = showMap.Checked;
                ApplyMapVisibility(true);
            };

            var source = new ToolStripMenuItem("Flight data");
            var sourceItems = new List<ToolStripMenuItem>();
            foreach (var src in AdsbFeed.PublicSources)
            {
                string name = src;
                var it = new ToolStripMenuItem(name) { Checked = _settings.FlightSource == name };
                it.Click += delegate { SetSource(name, sourceItems); };
                sourceItems.Add(it);
            }
            var local = new ToolStripMenuItem("Local receiver...") { Checked = _settings.FlightSource == "local", Tag = "local" };
            local.Click += delegate
            {
                string url = Prompt("Local receiver",
                    "URL of your receiver's aircraft.json (dump1090-fa, readsb or tar1090):", _settings.LocalReceiverUrl);
                if (url == null) return;
                _settings.LocalReceiverUrl = url.Trim();
                _feed.LocalUrl = _settings.LocalReceiverUrl;
                SetSource("local", sourceItems);
            };
            sourceItems.Add(local);
            source.DropDownItems.AddRange(sourceItems.ToArray());
            source.DropDownItems.Add(new ToolStripSeparator());
            var about = new ToolStripMenuItem("About these feeds...");
            about.Click += delegate
            {
                MessageBox.Show(this,
                    "Aircraft positions come from free community ADS-B networks, built by volunteers with home receivers:\n\n" +
                    "  airplanes.live  ·  adsb.lol  ·  adsb.fi\n\n" +
                    "They are for personal, non-commercial use and are rate limited, so the map polls every " + _settings.FlightRefreshSeconds +
                    " s. If the chosen network fails, the next one is tried automatically.\n\n" +
                    "With your own receiver (an RTL-SDR stick running dump1090 or readsb), pick Local receiver.",
                    "Flight data", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            source.DropDownItems.Add(about);

            var style = new ToolStripMenuItem("Map style");
            var styleItems = new List<ToolStripMenuItem>();
            foreach (var pair in new[] { new[] { "dark", "Dark" }, new[] { "light", "Light" }, new[] { "street", "OpenStreetMap" } })
            {
                string key = pair[0];
                var it = new ToolStripMenuItem(pair[1]) { Checked = _settings.MapStyle == key };
                it.Click += delegate
                {
                    _settings.MapStyle = key;
                    _settings.Save();
                    _map.Tiles.Clear();
                    _map.Tiles.Style = key;
                    foreach (var o in styleItems) o.Checked = o == it;
                    _map.Invalidate();
                };
                styleItems.Add(it);
            }
            style.DropDownItems.AddRange(styleItems.ToArray());

            var range = new ToolStripMenuItem("Range");
            var rangeItems = new List<ToolStripMenuItem>();
            foreach (int nm in new[] { 25, 50, 100, 150, 250 })
            {
                int r = nm;
                var it = new ToolStripMenuItem(r + " nm (" + Math.Round(r * 1.852) + " km)") { Checked = _settings.FlightRadiusNm == r };
                it.Click += delegate
                {
                    _settings.FlightRadiusNm = r;
                    _settings.Save();
                    _feed.RadiusNm = r;
                    foreach (var o in rangeItems) o.Checked = o == it;
                    _feed.Poll();
                    _map.Invalidate();
                };
                rangeItems.Add(it);
            }
            range.DropDownItems.AddRange(rangeItems.ToArray());

            var labels = new ToolStripMenuItem("Show labels") { Checked = _settings.ShowLabels, CheckOnClick = true };
            labels.CheckedChanged += delegate
            {
                _settings.ShowLabels = _map.ShowLabels = labels.Checked;
                _settings.Save();
                _map.Invalidate();
            };
            var trails = new ToolStripMenuItem("Show trails") { Checked = _settings.ShowTrails, CheckOnClick = true };
            trails.CheckedChanged += delegate
            {
                _settings.ShowTrails = _map.ShowTrails = trails.Checked;
                _settings.Save();
                _map.Invalidate();
            };
            var recentre = new ToolStripMenuItem("Recentre map");
            recentre.Click += delegate { _map.ResetView(); };
            var refreshFlights = new ToolStripMenuItem("Refresh flights");
            refreshFlights.Click += delegate { _feed.Poll(); };

            mapItems.AddRange(new ToolStripItem[] { source, style, range, labels, trails, recentre, refreshFlights });
            foreach (var it in mapItems) it.Enabled = _settings.ShowFlightMap;

            var exit = new ToolStripMenuItem("Exit");
            exit.Click += delegate { Close(); };

            menu.Items.AddRange(new ToolStripItem[]
            {
                onTop, h24, refresh, edit, new ToolStripSeparator(),
                showMap, source, style, range, labels, trails, recentre, refreshFlights, new ToolStripSeparator(),
                exit
            });
            ContextMenuStrip = menu;
            foreach (Control c in Controls) c.ContextMenuStrip = menu;
        }

        private void SetSource(string name, List<ToolStripMenuItem> items)
        {
            _settings.FlightSource = name;
            _settings.Save();
            _feed.Source = name;
            _feed.Reset();
            _map.SelectAircraft(null);
            foreach (var it in items) it.Checked = (it.Tag as string ?? it.Text) == name;
            _feed.Poll();
            _map.Invalidate();
        }

        private string Prompt(string title, string text, string value)
        {
            using (var f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = f.MaximizeBox = false;
                f.ShowInTaskbar = false;
                f.TopMost = TopMost;
                f.AutoScaleMode = AutoScaleMode.Dpi;
                f.ClientSize = new Size(S(440), S(112));
                var lbl = new Label { Text = text, AutoSize = false, Bounds = new Rectangle(S(12), S(10), S(416), S(34)) };
                var box = new TextBox { Text = value, Bounds = new Rectangle(S(12), S(46), S(416), S(24)) };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Bounds = new Rectangle(S(264), S(78), S(78), S(26)) };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(S(350), S(78), S(78), S(26)) };
                f.Controls.AddRange(new Control[] { lbl, box, ok, cancel });
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                return f.ShowDialog(this) == DialogResult.OK && box.Text.Trim().Length > 0 ? box.Text : null;
            }
        }

        // ------------------------------------------------------------ clock and weather

        private void UpdateClock()
        {
            DateTime now = DateTime.Now;
            if (now.Second == _lastSecond) return;
            _lastSecond = now.Second;
            _time.Text = now.ToString(_settings.Use24Hour ? "HH:mm:ss" : "h:mm:ss tt", CultureInfo.InvariantCulture);
            _date.Text = now.ToString("dddd, d MMMM yyyy", CultureInfo.CurrentCulture);
        }

        private async Task RefreshWeatherAsync()
        {
            if (_fetching) return;
            _fetching = true;
            var inv = CultureInfo.InvariantCulture;
            string url = "https://api.open-meteo.com/v1/forecast"
                + "?latitude=" + _settings.Latitude.ToString(inv)
                + "&longitude=" + _settings.Longitude.ToString(inv)
                + "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m,is_day"
                + "&timezone=auto";
            try
            {
                string json;
                using (var wc = new WebClient())
                {
                    wc.Encoding = Encoding.UTF8;
                    wc.Headers[HttpRequestHeader.UserAgent] = "ClockWeather/1.1";
                    json = await wc.DownloadStringTaskAsync(new Uri(url));
                }

                OpenMeteoResponse data;
                var ser = new DataContractJsonSerializer(typeof(OpenMeteoResponse));
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    data = (OpenMeteoResponse)ser.ReadObject(ms);
                if (data == null || data.Current == null) throw new InvalidDataException("No current weather in response");

                CurrentWeather c = data.Current;
                _weather.Text = string.Format(inv, "{0}   {1:0.#}°C   {2}",
                    _settings.LocationName, c.Temperature, Describe(c.WeatherCode, c.IsDay == 1));
                _detail.Text = string.Format(inv, "Feels {0:0}°C  ·  Humidity {1:0}%  ·  Wind {2:0} km/h  ·  Updated {3:HH:mm}",
                    c.ApparentTemperature, c.Humidity, c.WindSpeed, DateTime.Now);
            }
            catch (Exception ex)
            {
                _detail.Text = "Weather unavailable: " + ex.GetBaseException().Message;
                if (_weather.Text.StartsWith("Loading")) _weather.Text = _settings.LocationName;
            }
            finally
            {
                _fetching = false;
            }
        }

        // WMO weather interpretation codes, as returned by Open-Meteo
        private static string Describe(int code, bool day)
        {
            switch (code)
            {
                case 0: return day ? "Clear sky" : "Clear night";
                case 1: return "Mostly clear";
                case 2: return "Partly cloudy";
                case 3: return "Overcast";
                case 45: case 48: return "Fog";
                case 51: case 53: case 55: return "Drizzle";
                case 56: case 57: return "Freezing drizzle";
                case 61: return "Light rain";
                case 63: return "Rain";
                case 65: return "Heavy rain";
                case 66: case 67: return "Freezing rain";
                case 71: case 73: case 75: case 77: return "Snow";
                case 80: return "Light showers";
                case 81: return "Showers";
                case 82: return "Heavy showers";
                case 85: case 86: return "Snow showers";
                case 95: return "Thunderstorm";
                case 96: case 99: return "Thunderstorm, hail";
                default: return "Code " + code;
            }
        }

        // ------------------------------------------------------------ dragging and painting

        private void OnDragStart(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) _dragStart = e.Location;
        }

        private void OnDragMove(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                Location = new Point(Location.X + e.X - _dragStart.X, Location.Y + e.Y - _dragStart.Y);
        }

        private void OnDragEnd(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _settings.WindowX = Location.X;
            _settings.WindowY = Location.Y;
            _settings.Save();
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); OnDragStart(this, e); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); OnDragMove(this, e); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); OnDragEnd(this, e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Border, 1.5f * _scale))
            using (var path = RoundedPath(new Rectangle(1, 1, ClientSize.Width - 3, ClientSize.Height - 3), S(17)))
                g.DrawPath(pen, path);

            // resize grip: three short diagonal strokes in the corner
            using (var pen = new Pen(Color.FromArgb(110, Muted), 1.2f * _scale))
            {
                int x = ClientSize.Width - S(6), y = ClientSize.Height - S(6);
                for (int i = 1; i <= 3; i++)
                {
                    int d = S(3.2 * i);
                    g.DrawLine(pen, x - d, y, x, y - d);
                }
            }
        }

        private static GraphicsPath RoundedPath(Rectangle r, int radius)
        {
            int d = Math.Max(2, radius * 2);
            var p = new GraphicsPath();
            if (r.Width < d || r.Height < d) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _clockTimer.Dispose();
            _weatherTimer.Dispose();
            _feed.Dispose();
            base.OnFormClosed(e);
        }
    }

    [DataContract]
    internal sealed class OpenMeteoResponse
    {
        [DataMember(Name = "current")] public CurrentWeather Current { get; set; }
    }

    [DataContract]
    internal sealed class CurrentWeather
    {
        [DataMember(Name = "temperature_2m")] public double Temperature { get; set; }
        [DataMember(Name = "apparent_temperature")] public double ApparentTemperature { get; set; }
        [DataMember(Name = "relative_humidity_2m")] public double Humidity { get; set; }
        [DataMember(Name = "weather_code")] public int WeatherCode { get; set; }
        [DataMember(Name = "wind_speed_10m")] public double WindSpeed { get; set; }
        [DataMember(Name = "is_day")] public int IsDay { get; set; }
    }
}
