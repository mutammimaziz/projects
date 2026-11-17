// ClockWeather: live ADS-B flight map.
//
// Flight data: free community ADS-B networks (airplanes.live, adsb.lol, adsb.fi), no key needed, or your own
// receiver's aircraft.json (dump1090 / readsb / tar1090). All use the same readsb JSON format.
// Map: raster tiles from CARTO (dark / Voyager) or OpenStreetMap, cached on disk, drawn with GDI+.
// .NET Framework 4.8 / C# 5.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ClockWeather
{
    // ------------------------------------------------------------------ aircraft model

    internal sealed class Aircraft
    {
        public string Hex, Flight, Reg, Type, Desc, Squawk, Category;
        public double Lat, Lon;
        public int? Alt, VRate;
        public double? Gs, Track;
        public bool OnGround, Military, Mlat, Emergency;
        public double SeenPos;
        public DateTime PositionTime;       // when the reported position was valid (UTC)
        public DateTime LastUpdate;
        public readonly List<PointF> Trail = new List<PointF>(); // X = lon, Y = lat

        public string Label
        {
            get { return !string.IsNullOrEmpty(Flight) ? Flight : !string.IsNullOrEmpty(Reg) ? Reg : Hex.ToUpperInvariant(); }
        }

        // Position now, projected forward from the last report using speed and track (at most 20 s).
        public void Predict(DateTime now, out double lat, out double lon)
        {
            lat = Lat; lon = Lon;
            if (OnGround || !Gs.HasValue || !Track.HasValue || Gs.Value < 1) return;
            double dt = Math.Min(20, Math.Max(0, (now - PositionTime).TotalSeconds));
            double nm = Gs.Value * dt / 3600.0;
            double tr = Track.Value * Math.PI / 180;
            lat = Lat + nm * Math.Cos(tr) / 60.0;
            double c = Math.Cos(Lat * Math.PI / 180);
            if (Math.Abs(c) > 0.01) lon = Lon + nm * Math.Sin(tr) / (60.0 * c);
        }
    }

    internal static class Geo
    {
        public static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
        {
            double r = 3440.065; // earth radius, nautical miles
            double p1 = lat1 * Math.PI / 180, p2 = lat2 * Math.PI / 180;
            double dp = p2 - p1, dl = (lon2 - lon1) * Math.PI / 180;
            double a = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
            return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        }

        public static double Bearing(double lat1, double lon1, double lat2, double lon2)
        {
            double p1 = lat1 * Math.PI / 180, p2 = lat2 * Math.PI / 180, dl = (lon2 - lon1) * Math.PI / 180;
            double y = Math.Sin(dl) * Math.Cos(p2);
            double x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
            return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
        }

        public static string Compass(double deg)
        {
            string[] p = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            return p[(int)Math.Round(deg / 45.0) % 8];
        }

        // Web Mercator: world pixel coordinates at a zoom level for a given tile size.
        public static double WorldX(double lon, int zoom, double tile)
        {
            return (lon + 180.0) / 360.0 * tile * (1 << zoom);
        }

        public static double WorldY(double lat, int zoom, double tile)
        {
            lat = Math.Max(-85.0511, Math.Min(85.0511, lat));
            double s = Math.Sin(lat * Math.PI / 180);
            return (0.5 - Math.Log((1 + s) / (1 - s)) / (4 * Math.PI)) * tile * (1 << zoom);
        }

        public static double Lon(double worldX, int zoom, double tile)
        {
            return worldX / (tile * (1 << zoom)) * 360.0 - 180.0;
        }

        public static double Lat(double worldY, int zoom, double tile)
        {
            double n = Math.PI - 2 * Math.PI * worldY / (tile * (1 << zoom));
            return 180.0 / Math.PI * Math.Atan(Math.Sinh(n));
        }
    }

    // ------------------------------------------------------------------ data feed

    internal static class AdsbFeed
    {
        public static readonly string[] PublicSources = { "airplanes.live", "adsb.lol", "adsb.fi" };

        public static string Url(string source, double lat, double lon, int nm, string localUrl)
        {
            var inv = CultureInfo.InvariantCulture;
            string la = lat.ToString("0.####", inv), lo = lon.ToString("0.####", inv);
            nm = Math.Max(1, Math.Min(250, nm));
            switch (source)
            {
                case "adsb.lol": return "https://api.adsb.lol/v2/lat/" + la + "/lon/" + lo + "/dist/" + nm;
                case "adsb.fi": return "https://opendata.adsb.fi/api/v2/lat/" + la + "/lon/" + lo + "/dist/" + nm;
                case "local": return localUrl;
                default: return "https://api.airplanes.live/v2/point/" + la + "/" + lo + "/" + nm;
            }
        }

        public static string WebsiteFor(string source, string hex)
        {
            switch (source)
            {
                case "adsb.lol": return "https://adsb.lol/?icao=" + hex;
                case "adsb.fi": return "https://globe.adsb.fi/?icao=" + hex;
                default: return "https://globe.airplanes.live/?icao=" + hex;
            }
        }

        public static string Download(string url, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "ClockWeather/1.1 (personal desktop clock)";
            req.Accept = "application/json";
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return r.ReadToEnd();
        }

        // readsb / ADS-B Exchange v2 JSON: { "ac": [...] } from the networks, { "aircraft": [...] } from a receiver.
        public static List<Aircraft> Parse(string json, DateTime receivedUtc)
        {
            var list = new List<Aircraft>();
            var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
            if (root == null) throw new InvalidDataException("Unexpected response");
            object arr;
            if (!root.TryGetValue("ac", out arr) && !root.TryGetValue("aircraft", out arr))
            {
                object msg;
                throw new InvalidDataException(root.TryGetValue("msg", out msg) ? Convert.ToString(msg) : "No aircraft list in response");
            }
            var items = arr as object[];
            if (items == null) return list;
            foreach (var o in items)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                double? lat = Num(d, "lat"), lon = Num(d, "lon");
                if (!lat.HasValue || !lon.HasValue) continue;     // no position: can't be drawn
                var a = new Aircraft
                {
                    Hex = (Str(d, "hex") ?? "").TrimStart('~').ToLowerInvariant(),
                    Flight = Clean(Str(d, "flight")),
                    Reg = Clean(Str(d, "r")),
                    Type = Clean(Str(d, "t")),
                    Desc = Clean(Str(d, "desc")),
                    Squawk = Clean(Str(d, "squawk")),
                    Category = Clean(Str(d, "category")),
                    Lat = lat.Value,
                    Lon = lon.Value,
                    Gs = Num(d, "gs"),
                    Track = Num(d, "track") ?? Num(d, "true_heading"),
                    SeenPos = Num(d, "seen_pos") ?? Num(d, "seen") ?? 0,
                    LastUpdate = receivedUtc
                };
                if (a.Hex.Length == 0) continue;
                object alt;
                if (d.TryGetValue("alt_baro", out alt) && alt is string && ((string)alt).Equals("ground", StringComparison.OrdinalIgnoreCase)) a.OnGround = true;
                else
                {
                    double? ab = Num(d, "alt_baro") ?? Num(d, "alt_geom");
                    if (ab.HasValue) a.Alt = (int)Math.Round(ab.Value);
                }
                double? vr = Num(d, "baro_rate") ?? Num(d, "geom_rate");
                if (vr.HasValue) a.VRate = (int)Math.Round(vr.Value);
                double? flags = Num(d, "dbFlags");
                a.Military = flags.HasValue && ((int)flags.Value & 1) != 0;
                string src = Str(d, "type") ?? "";
                a.Mlat = src.StartsWith("mlat", StringComparison.OrdinalIgnoreCase);
                string em = Str(d, "emergency");
                a.Emergency = (em != null && em != "none") || a.Squawk == "7500" || a.Squawk == "7600" || a.Squawk == "7700";
                a.PositionTime = receivedUtc.AddSeconds(-Math.Max(0, a.SeenPos));
                list.Add(a);
            }
            return list;
        }

        private static string Clean(string s)
        {
            if (s == null) return null;
            s = s.Trim();
            return s.Length == 0 ? null : s;
        }

        private static string Str(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        }

        private static double? Num(Dictionary<string, object> d, string k)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null || v is string || v is bool) return null;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return null; }
        }
    }

    // Polls the chosen network every few seconds, falling back to the others if it fails.
    internal sealed class FlightFeed : IDisposable
    {
        public event Action Updated;
        public readonly Dictionary<string, Aircraft> Aircraft = new Dictionary<string, Aircraft>();
        public string Source = "airplanes.live", LocalUrl = "", ActiveSource, Error;
        public double Lat, Lon;
        public int RadiusNm = 50;
        public DateTime LastSuccess;
        public Func<string, string> Fetch = url => AdsbFeed.Download(url, 8000); // replaceable for tests

        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        private bool _busy;
        private string _sticky;            // public source that last worked, if the preferred one failed
        private DateTime _stickySince;

        public FlightFeed(int seconds)
        {
            Interval = seconds;
            _timer.Tick += delegate { Poll(); };
        }

        public int Interval
        {
            set { _timer.Interval = Math.Max(2, value) * 1000; }
        }

        public void Start() { _timer.Start(); Poll(); }
        public void Stop() { _timer.Stop(); }
        public bool Running { get { return _timer.Enabled; } }

        public void Reset()
        {
            Aircraft.Clear();
            _sticky = null;
            Error = null;
            ActiveSource = null;
        }

        private List<string> Order()
        {
            if (Source == "local") return new List<string> { "local" };
            var order = new List<string>();
            if (_sticky != null && (DateTime.UtcNow - _stickySince).TotalMinutes < 5) order.Add(_sticky);
            order.Add(Source);
            order.AddRange(AdsbFeed.PublicSources);
            return order.Distinct().ToList();
        }

        public async void Poll()
        {
            if (_busy) return;
            _busy = true;
            var order = Order();
            double lat = Lat, lon = Lon;
            int nm = RadiusNm;
            string local = LocalUrl;
            string used = null, err = null;
            List<Aircraft> result = null;
            try
            {
                result = await Task.Run(() =>
                {
                    foreach (var src in order)
                    {
                        try
                        {
                            string url = AdsbFeed.Url(src, lat, lon, nm, local);
                            if (string.IsNullOrWhiteSpace(url)) { err = "No receiver URL set"; continue; }
                            var list = AdsbFeed.Parse(Fetch(url), DateTime.UtcNow);
                            if (src == "local") list = list.Where(a => Geo.DistanceNm(lat, lon, a.Lat, a.Lon) <= nm).ToList();
                            used = src;
                            return list;
                        }
                        catch (Exception ex) { if (err == null) err = src + ": " + ex.GetBaseException().Message; }
                    }
                    return null;
                });
            }
            catch (Exception ex) { err = ex.Message; }
            finally { _busy = false; }

            if (result != null)
            {
                Merge(result);
                ActiveSource = used;
                Error = null;
                LastSuccess = DateTime.Now;
                if (used != Source) { _sticky = used; _stickySince = DateTime.UtcNow; } else _sticky = null;
            }
            else Error = err ?? "No data";
            var h = Updated;
            if (h != null) h();
        }

        public void Merge(List<Aircraft> fresh)
        {
            var now = DateTime.UtcNow;
            foreach (var f in fresh)
            {
                Aircraft a;
                if (Aircraft.TryGetValue(f.Hex, out a))
                {
                    if (a.Trail.Count == 0 || Math.Abs(a.Trail[a.Trail.Count - 1].X - a.Lon) > 1e-5 || Math.Abs(a.Trail[a.Trail.Count - 1].Y - a.Lat) > 1e-5)
                        a.Trail.Add(new PointF((float)a.Lon, (float)a.Lat));
                    if (a.Trail.Count > 90) a.Trail.RemoveRange(0, a.Trail.Count - 90);
                    f.Trail.AddRange(a.Trail);
                }
                Aircraft[f.Hex] = f;
            }
            foreach (var hex in Aircraft.Where(kv => (now - kv.Value.LastUpdate).TotalSeconds > 45).Select(kv => kv.Key).ToList())
                Aircraft.Remove(hex);
        }

        public void Dispose() { _timer.Dispose(); }
    }

    // ------------------------------------------------------------------ map tiles

    internal sealed class TileCache
    {
        public event Action TileLoaded;
        private readonly Dictionary<string, Bitmap> _mem = new Dictionary<string, Bitmap>();
        private readonly LinkedList<string> _lru = new LinkedList<string>();
        private readonly HashSet<string> _pending = new HashSet<string>();
        private readonly Dictionary<string, DateTime> _failed = new Dictionary<string, DateTime>();
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(4);
        private readonly object _lock = new object();
        private const int MaxTiles = 96;
        public string Style = "dark";
        public bool Retina;
        public Func<string, byte[]> Fetch = DownloadBytes; // replaceable for tests

        public static string Attribution(string style)
        {
            return style == "street" ? "© OpenStreetMap contributors" : "© OpenStreetMap contributors © CARTO";
        }

        private string UrlFor(int z, int x, int y)
        {
            string r = Retina && Style != "street" ? "@2x" : "";
            string s = "abcd"[(x + y) & 3].ToString();
            switch (Style)
            {
                case "light": return "https://" + s + ".basemaps.cartocdn.com/rastertiles/voyager/" + z + "/" + x + "/" + y + r + ".png";
                case "street": return "https://tile.openstreetmap.org/" + z + "/" + x + "/" + y + ".png";
                default: return "https://" + s + ".basemaps.cartocdn.com/dark_all/" + z + "/" + x + "/" + y + r + ".png";
            }
        }

        private string DiskPath(int z, int x, int y)
        {
            string style = Style + (Retina && Style != "street" ? "@2x" : "");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClockWeather", "tiles", style,
                z.ToString(), x.ToString(), y + ".png");
        }

        private string Key(int z, int x, int y) { return Style + (Retina ? "2" : "1") + "/" + z + "/" + x + "/" + y; }

        // Returns the tile if it's ready; otherwise starts loading it and returns null.
        public Bitmap Get(int z, int x, int y)
        {
            int n = 1 << z;
            if (y < 0 || y >= n) return null;
            x = ((x % n) + n) % n;
            string key = Key(z, x, y);
            lock (_lock)
            {
                Bitmap b;
                if (_mem.TryGetValue(key, out b))
                {
                    _lru.Remove(key);
                    _lru.AddFirst(key);
                    return b;
                }
                DateTime failedAt;
                if (_failed.TryGetValue(key, out failedAt) && (DateTime.UtcNow - failedAt).TotalSeconds < 60) return null;
                if (!_pending.Add(key)) return null;
            }
            string url = UrlFor(z, x, y), path = DiskPath(z, x, y);
            Task.Run(() => Load(key, url, path));
            return null;
        }

        // Only looks in memory (used to stretch a parent tile while the real one loads).
        public Bitmap Peek(int z, int x, int y)
        {
            int n = 1 << z;
            if (z < 0 || y < 0 || y >= n) return null;
            x = ((x % n) + n) % n;
            Bitmap b;
            lock (_lock) return _mem.TryGetValue(Key(z, x, y), out b) ? b : null;
        }

        private async Task Load(string key, string url, string path)
        {
            Bitmap bmp = null;
            try
            {
                byte[] data = null;
                try
                {
                    if (File.Exists(path) && (DateTime.Now - File.GetLastWriteTime(path)).TotalDays < 30) data = File.ReadAllBytes(path);
                }
                catch { }
                if (data == null)
                {
                    await _gate.WaitAsync().ConfigureAwait(false);
                    try { data = Fetch(url); }
                    finally { _gate.Release(); }
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        File.WriteAllBytes(path, data);
                    }
                    catch { }
                }
                using (var ms = new MemoryStream(data))
                using (var img = Image.FromStream(ms))
                    bmp = new Bitmap(img);
            }
            catch { }

            lock (_lock)
            {
                _pending.Remove(key);
                if (bmp == null) { _failed[key] = DateTime.UtcNow; return; }
                _mem[key] = bmp;
                _lru.AddFirst(key);
                while (_lru.Count > MaxTiles)
                {
                    string old = _lru.Last.Value;
                    _lru.RemoveLast();
                    Bitmap ob;
                    if (_mem.TryGetValue(old, out ob)) { _mem.Remove(old); ob.Dispose(); }
                }
            }
            var h = TileLoaded;
            if (h != null) h();
        }

        public void Clear()
        {
            lock (_lock)
            {
                foreach (var b in _mem.Values) b.Dispose();
                _mem.Clear();
                _lru.Clear();
                _failed.Clear();
            }
        }

        private static byte[] DownloadBytes(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = 10000;
            req.UserAgent = "ClockWeather/1.1 (personal desktop clock)";
            using (var resp = req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }
    }

    // ------------------------------------------------------------------ map control

    internal sealed class FlightMapView : Control
    {
        public FlightFeed Feed;
        public readonly TileCache Tiles = new TileCache();
        public double HomeLat, HomeLon;
        public bool ShowLabels = true, ShowTrails = true;
        public event Action ViewChanged;

        private double _cLat, _cLon;
        private int _zoom = 9;
        private string _selected;
        private Point _down, _last;
        private bool _dragging, _pressed;
        private readonly System.Windows.Forms.Timer _anim = new System.Windows.Forms.Timer { Interval = 250 };
        private RectangleF _btnIn, _btnOut, _btnHome, _cardLink, _cardClose;
        private float _s = 1;

        private static readonly Color MapBg = Color.FromArgb(14, 16, 20);
        private static readonly Color Panel = Color.FromArgb(225, 22, 25, 31);
        private static readonly Color Fg = Color.FromArgb(235, 238, 242);
        private static readonly Color Muted = Color.FromArgb(150, 158, 170);
        private static readonly Color Accent = Color.FromArgb(255, 184, 77);

        public FlightMapView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = MapBg;
            Tiles.TileLoaded += () => { try { if (IsHandleCreated) BeginInvoke(new Action(Invalidate)); } catch { } };
            _anim.Tick += delegate { if (Visible && Feed != null && Feed.Aircraft.Count > 0) Invalidate(); };
            _anim.Start();
        }

        public int Zoom
        {
            get { return _zoom; }
            set { _zoom = Math.Max(3, Math.Min(15, value)); Invalidate(); }
        }

        public void CenterOn(double lat, double lon)
        {
            _cLat = lat; _cLon = lon;
            Invalidate();
        }

        public void ResetView()
        {
            CenterOn(HomeLat, HomeLon);
        }

        private double Tile { get { return 256.0 * _s; } }

        private PointF ToScreen(double lat, double lon)
        {
            double cx = Geo.WorldX(_cLon, _zoom, Tile), cy = Geo.WorldY(_cLat, _zoom, Tile);
            return new PointF((float)(Geo.WorldX(lon, _zoom, Tile) - cx + Width / 2.0), (float)(Geo.WorldY(lat, _zoom, Tile) - cy + Height / 2.0));
        }

        private void FromScreen(float x, float y, out double lat, out double lon)
        {
            double cx = Geo.WorldX(_cLon, _zoom, Tile), cy = Geo.WorldY(_cLat, _zoom, Tile);
            lon = Geo.Lon(cx + x - Width / 2.0, _zoom, Tile);
            lat = Geo.Lat(cy + y - Height / 2.0, _zoom, Tile);
        }

        // Altitude colours, low = orange through green and blue to high = magenta (like tar1090).
        public static Color AltColor(Aircraft a)
        {
            if (a.OnGround) return Color.FromArgb(150, 150, 150);
            if (!a.Alt.HasValue) return Color.FromArgb(200, 200, 200);
            double alt = a.Alt.Value;
            double[] stops = { 0, 2000, 6000, 10000, 20000, 30000, 40000 };
            double[] hues = { 20, 40, 90, 140, 190, 240, 290 };
            double hue = hues[hues.Length - 1];
            for (int i = 1; i < stops.Length; i++)
                if (alt <= stops[i]) { hue = hues[i - 1] + (hues[i] - hues[i - 1]) * (alt - stops[i - 1]) / (stops[i] - stops[i - 1]); break; }
            return FromHsl(Math.Max(0, hue), 0.85, 0.58);
        }

        private static Color FromHsl(double h, double s, double l)
        {
            double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2, r, g, b;
            if (h < 60) { r = c; g = x; b = 0; } else if (h < 120) { r = x; g = c; b = 0; } else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; } else if (h < 300) { r = x; g = 0; b = c; } else { r = c; g = 0; b = x; }
            return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
        }

        public static string AltText(Aircraft a)
        {
            if (a.OnGround) return "ground";
            if (!a.Alt.HasValue) return "";
            return a.Alt.Value >= 18000 ? "FL" + (a.Alt.Value / 100).ToString("000") : a.Alt.Value.ToString("N0", CultureInfo.InvariantCulture) + " ft";
        }

        // ---------------------------------------------------------------- painting

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            _s = g.DpiX / 96f;
            Tiles.Retina = _s > 1.25f;
            g.Clear(MapBg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            if (Width < 20 || Height < 20) return;
            if (_cLat == 0 && _cLon == 0) { _cLat = HomeLat; _cLon = HomeLon; }

            DrawTiles(g);
            DrawRings(g);
            var now = DateTime.UtcNow;
            var planes = Feed == null ? new List<Aircraft>() : Feed.Aircraft.Values.OrderBy(a => a.OnGround ? -1 : a.Alt ?? 0).ToList();
            if (ShowTrails) foreach (var a in planes) DrawTrail(g, a, now);
            var labelled = new List<RectangleF>();
            using (var labelFont = new Font("Segoe UI", 7.5f))
            using (var labelBold = new Font("Segoe UI Semibold", 7.5f))
            {
                bool labels = ShowLabels && (_zoom >= 9 || planes.Count <= 30);
                foreach (var a in planes) DrawPlane(g, a, now, labels, labelFont, labelBold, labelled);
            }
            DrawHome(g);
            DrawButtons(g);
            DrawFooter(g, planes.Count);
            Aircraft sel;
            if (_selected != null && Feed != null && Feed.Aircraft.TryGetValue(_selected, out sel)) DrawCard(g, sel, now);
            else { _cardLink = RectangleF.Empty; _cardClose = RectangleF.Empty; }
        }

        private void DrawTiles(Graphics g)
        {
            double T = Tile;
            double cx = Geo.WorldX(_cLon, _zoom, T), cy = Geo.WorldY(_cLat, _zoom, T);
            double left = cx - Width / 2.0, top = cy - Height / 2.0;
            int x0 = (int)Math.Floor(left / T), y0 = (int)Math.Floor(top / T);
            int x1 = (int)Math.Floor((left + Width) / T), y1 = (int)Math.Floor((top + Height) / T);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            for (int ty = y0; ty <= y1; ty++)
                for (int tx = x0; tx <= x1; tx++)
                {
                    var dest = new RectangleF((float)(tx * T - left), (float)(ty * T - top), (float)T + 0.5f, (float)T + 0.5f);
                    var bmp = Tiles.Get(_zoom, tx, ty);
                    if (bmp != null) { g.DrawImage(bmp, dest); continue; }
                    // stretch the parent tile while this one loads
                    var parent = Tiles.Peek(_zoom - 1, tx >> 1, ty >> 1);
                    if (parent != null)
                    {
                        float half = parent.Width / 2f;
                        g.DrawImage(parent, dest, new RectangleF((tx & 1) * half, (ty & 1) * half, half, half), GraphicsUnit.Pixel);
                    }
                }
            g.PixelOffsetMode = PixelOffsetMode.Default;
        }

        private void DrawRings(Graphics g)
        {
            if (Feed == null) return;
            var c = ToScreen(HomeLat, HomeLon);
            // pixels per nautical mile at the home latitude
            double mPerPx = 156543.03392 * Math.Cos(HomeLat * Math.PI / 180) / (1 << _zoom) / _s;
            double pxPerNm = 1852.0 / mPerPx;
            int range = Feed.RadiusNm;
            var rings = new List<int>();
            foreach (int r in new[] { 5, 10, 25, 50, 100, 150, 200, 250 }) if (r <= range && r * pxPerNm > 30 * _s) rings.Add(r);
            if (rings.Count > 3) rings = rings.Skip(rings.Count - 3).ToList();
            bool lightMap = Tiles.Style != "dark";
            Color ring = lightMap ? Color.FromArgb(60, 70, 90) : Color.FromArgb(160, 170, 190);
            using (var pen = new Pen(Color.FromArgb(lightMap ? 110 : 70, ring), 1f) { DashStyle = DashStyle.Dash })
            using (var font = new Font("Segoe UI", 7f))
            using (var brush = new SolidBrush(Color.FromArgb(lightMap ? 200 : 150, ring)))
                foreach (int r in rings)
                {
                    float rad = (float)(r * pxPerNm);
                    g.DrawEllipse(pen, c.X - rad, c.Y - rad, rad * 2, rad * 2);
                    g.DrawString(r + " nm", font, brush, c.X + 3 * _s, c.Y - rad + 1 * _s);
                }
        }

        private void DrawHome(Graphics g)
        {
            var c = ToScreen(HomeLat, HomeLon);
            float r = 4.5f * _s;
            using (var b = new SolidBrush(Accent)) g.FillEllipse(b, c.X - r, c.Y - r, r * 2, r * 2);
            using (var p = new Pen(Color.FromArgb(20, 22, 26), 1.5f * _s)) g.DrawEllipse(p, c.X - r, c.Y - r, r * 2, r * 2);
        }

        private void DrawTrail(Graphics g, Aircraft a, DateTime now)
        {
            if (a.Trail.Count < 1) return;
            var pts = a.Trail.Select(p => ToScreen(p.Y, p.X)).ToList();
            double lat, lon;
            a.Predict(now, out lat, out lon);
            pts.Add(ToScreen(lat, lon));
            if (pts.Count < 2) return;
            var col = AltColor(a);
            using (var pen = new Pen(Color.FromArgb(_selected == a.Hex ? 230 : 120, col), (_selected == a.Hex ? 2.2f : 1.4f) * _s) { LineJoin = LineJoin.Round })
                g.DrawLines(pen, pts.ToArray());
        }

        // Airliner outline pointing up, in units of the plane's size.
        private static readonly PointF[] PlaneShape =
        {
            new PointF(0, -1f), new PointF(0.12f, -0.75f), new PointF(0.12f, -0.25f), new PointF(0.95f, 0.18f), new PointF(0.95f, 0.32f),
            new PointF(0.12f, 0.12f), new PointF(0.1f, 0.62f), new PointF(0.4f, 0.85f), new PointF(0.4f, 0.97f), new PointF(0, 0.88f),
            new PointF(-0.4f, 0.97f), new PointF(-0.4f, 0.85f), new PointF(-0.1f, 0.62f), new PointF(-0.12f, 0.12f), new PointF(-0.95f, 0.32f),
            new PointF(-0.95f, 0.18f), new PointF(-0.12f, -0.25f), new PointF(-0.12f, -0.75f)
        };

        private void DrawPlane(Graphics g, Aircraft a, DateTime now, bool labels, Font font, Font bold, List<RectangleF> used)
        {
            double lat, lon;
            a.Predict(now, out lat, out lon);
            var p = ToScreen(lat, lon);
            if (p.X < -30 || p.Y < -30 || p.X > Width + 30 || p.Y > Height + 30) return;
            bool sel = a.Hex == _selected;
            var col = AltColor(a);
            float size = (a.Category == "A5" || a.Category == "A4" ? 11f : a.Category == "A1" || a.Category == "A7" ? 6.5f : 8.5f) * _s;
            if (a.OnGround) size *= 0.8f;
            if (sel) size *= 1.25f;

            if (a.Emergency)
            {
                float pulse = (float)(0.5 + 0.5 * Math.Sin(now.Ticks / 2500000.0));
                using (var pen = new Pen(Color.FromArgb((int)(120 + 120 * pulse), 255, 60, 60), 2f * _s))
                    g.DrawEllipse(pen, p.X - size * 1.8f, p.Y - size * 1.8f, size * 3.6f, size * 3.6f);
            }

            var state = g.Save();
            g.TranslateTransform(p.X, p.Y);
            g.RotateTransform((float)(a.Track ?? 0));
            var shape = PlaneShape.Select(q => new PointF(q.X * size, q.Y * size)).ToArray();
            using (var b = new SolidBrush(col)) g.FillPolygon(b, shape);
            using (var pen = new Pen(sel ? Color.White : Color.FromArgb(200, 10, 12, 16), (sel ? 1.6f : 1f) * _s)) g.DrawPolygon(pen, shape);
            g.Restore(state);

            if (!labels && !sel) return;
            string line1 = a.Label, line2 = (_zoom >= 10 || sel) ? AltText(a) : null;
            var s1 = g.MeasureString(line1, bold);
            var s2 = line2 != null && line2.Length > 0 ? g.MeasureString(line2, font) : SizeF.Empty;
            float w = Math.Max(s1.Width, s2.Width) + 4 * _s, h = s1.Height + s2.Height;
            var rect = new RectangleF(p.X + size + 3 * _s, p.Y - h / 2, w, h);
            if (rect.Right > Width - 34 * _s) rect.X = p.X - size - 3 * _s - w;   // flip to the left near the edge / buttons
            if (!sel && used.Any(r => r.IntersectsWith(rect))) return;     // skip labels that would overlap
            used.Add(rect);
            using (var bg = new SolidBrush(Color.FromArgb(sel ? 230 : 170, 16, 18, 22))) g.FillRectangle(bg, rect);
            using (var fb = new SolidBrush(Fg)) g.DrawString(line1, bold, fb, rect.X + 2 * _s, rect.Y);
            if (s2.Height > 0) using (var mb = new SolidBrush(col)) g.DrawString(line2, font, mb, rect.X + 2 * _s, rect.Y + s1.Height - 1);
        }

        private void DrawButtons(Graphics g)
        {
            float b = 22 * _s, m = 8 * _s;
            _btnIn = new RectangleF(Width - m - b, m, b, b);
            _btnOut = new RectangleF(Width - m - b, m + b + 2 * _s, b, b);
            _btnHome = new RectangleF(Width - m - b, m + 2 * (b + 2 * _s) + 4 * _s, b, b);
            using (var font = new Font("Segoe UI Semibold", 11f))
            using (var small = new Font("Segoe UI Symbol", 9f))
            {
                Button(g, _btnIn, "+", font);
                Button(g, _btnOut, "−", font);
                Button(g, _btnHome, "⌂", small);
            }
        }

        private void Button(Graphics g, RectangleF r, string text, Font font)
        {
            using (var bg = new SolidBrush(Panel)) g.FillRectangle(bg, r);
            using (var pen = new Pen(Color.FromArgb(60, 255, 255, 255))) g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
            using (var fb = new SolidBrush(Fg))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(text, font, fb, r, sf);
        }

        private void DrawFooter(Graphics g, int count)
        {
            using (var font = new Font("Segoe UI", 7.5f))
            {
                string left;
                if (Feed == null) left = "";
                else if (Feed.Error != null && Feed.LastSuccess == DateTime.MinValue) left = "Flight data unavailable: " + Feed.Error;
                else if (Feed.LastSuccess == DateTime.MinValue) left = "Loading flights...";
                else
                {
                    left = "✈ " + count + " aircraft within " + Feed.RadiusNm + " nm  ·  " + (Feed.ActiveSource == "local" ? "your receiver" : Feed.ActiveSource) +
                           "  ·  " + Feed.LastSuccess.ToString("HH:mm:ss");
                    if (Feed.Error != null) left += "  ·  retrying";
                }
                string right = TileCache.Attribution(Tiles.Style);
                var sr = g.MeasureString(right, font);
                float h = sr.Height + 4 * _s;
                using (var bg = new SolidBrush(Color.FromArgb(190, 14, 16, 20))) g.FillRectangle(bg, 0, Height - h, Width, h);
                using (var mb = new SolidBrush(Muted))
                {
                    g.DrawString(right, font, mb, Width - sr.Width - 6 * _s, Height - h + 2 * _s);
                    var leftRect = new RectangleF(6 * _s, Height - h + 2 * _s, Math.Max(10, Width - sr.Width - 18 * _s), sr.Height);
                    using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                    using (var fb = new SolidBrush(Feed != null && Feed.Error != null ? Color.FromArgb(255, 150, 120) : Fg))
                        g.DrawString(left, font, fb, leftRect, sf);
                }
            }
        }

        private void DrawCard(Graphics g, Aircraft a, DateTime now)
        {
            var rows = new List<KeyValuePair<string, string>>();
            string alt = AltText(a);
            if (a.VRate.HasValue && Math.Abs(a.VRate.Value) >= 200 && !a.OnGround)
                alt += (a.VRate.Value > 0 ? "  ↑ " : "  ↓ ") + Math.Abs(a.VRate.Value).ToString("N0", CultureInfo.InvariantCulture) + " fpm";
            rows.Add(new KeyValuePair<string, string>("Altitude", alt.Length > 0 ? alt : "unknown"));
            if (a.Gs.HasValue) rows.Add(new KeyValuePair<string, string>("Speed", a.Gs.Value.ToString("0") + " kt  (" + (a.Gs.Value * 1.852).ToString("0") + " km/h)"));
            if (a.Track.HasValue) rows.Add(new KeyValuePair<string, string>("Heading", a.Track.Value.ToString("0") + "°  " + Geo.Compass(a.Track.Value)));
            double lat, lon;
            a.Predict(now, out lat, out lon);
            double dist = Geo.DistanceNm(HomeLat, HomeLon, lat, lon);
            rows.Add(new KeyValuePair<string, string>("From you", dist.ToString("0.0") + " nm " + Geo.Compass(Geo.Bearing(HomeLat, HomeLon, lat, lon)) +
                "  (" + (dist * 1.852).ToString("0") + " km)"));
            if (a.Squawk != null) rows.Add(new KeyValuePair<string, string>("Squawk", a.Squawk + (a.Squawk == "7700" ? "  EMERGENCY" : a.Squawk == "7600" ? "  RADIO FAILURE" : a.Squawk == "7500" ? "  HIJACK" : "")));
            rows.Add(new KeyValuePair<string, string>("ICAO hex", a.Hex.ToUpperInvariant() + (a.Mlat ? "  (MLAT)" : "") + (a.Military ? "  military" : "")));

            using (var title = new Font("Segoe UI Semibold", 11f))
            using (var sub = new Font("Segoe UI", 8f))
            using (var font = new Font("Segoe UI", 8.5f))
            using (var link = new Font("Segoe UI", 8f, FontStyle.Underline))
            {
                float pad = 10 * _s, w = 236 * _s, x = 8 * _s, y = 8 * _s;

                string head = a.Label + (a.Reg != null && a.Reg != a.Label ? "   " + a.Reg : "");
                string type = string.Join(" · ", new[] { a.Type, a.Desc }.Where(t => !string.IsNullOrEmpty(t)));
                float th = g.MeasureString(head, title).Height, sh = type.Length > 0 ? g.MeasureString(type, sub).Height : 0;
                float rh = g.MeasureString("Ag", font).Height, lh = g.MeasureString("Ag", link).Height;
                float h = pad + th + sh + 4 * _s + rows.Count * rh + 6 * _s + lh + pad;
                // pick the corner that keeps the card clear of the aircraft and its label
                var ap = ToScreen(lat, lon);
                var avoid = new RectangleF(ap.X - 20 * _s, ap.Y - 20 * _s, 110 * _s, 40 * _s);
                float footer = 22 * _s;
                var corners = new[]
                {
                    new PointF(8 * _s, 8 * _s),
                    new PointF(Width - 38 * _s - w, 8 * _s),
                    new PointF(8 * _s, Height - footer - 8 * _s - h),
                    new PointF(Width - 38 * _s - w, Height - footer - 8 * _s - h)
                };
                PointF best = corners[0];
                float bestScore = float.MinValue;
                foreach (var c in corners)
                {
                    var r = new RectangleF(c.X, c.Y, w, h);
                    float dx = Math.Max(0, Math.Max(r.Left - avoid.Right, avoid.Left - r.Right));
                    float dy = Math.Max(0, Math.Max(r.Top - avoid.Bottom, avoid.Top - r.Bottom));
                    float score = r.IntersectsWith(avoid) ? -1000 + dx + dy : 1000 - (c == corners[0] ? 0 : 1);
                    if (score > bestScore) { bestScore = score; best = c; }
                }
                x = Math.Max(4 * _s, best.X); y = Math.Max(4 * _s, best.Y);
                var card = new RectangleF(x, y, w, h);
                using (var bg = new SolidBrush(Panel)) g.FillRectangle(bg, card);
                using (var pen = new Pen(Color.FromArgb(200, AltColor(a)), 1.5f * _s)) g.DrawRectangle(pen, card.X, card.Y, card.Width, card.Height);
                float cy = y + pad;
                using (var fb = new SolidBrush(Fg))
                using (var mb = new SolidBrush(Muted))
                using (var ab = new SolidBrush(Accent))
                {
                    g.DrawString(head, title, fb, x + pad, cy);
                    _cardClose = new RectangleF(x + w - 22 * _s, y + 4 * _s, 18 * _s, 18 * _s);
                    g.DrawString("×", title, mb, _cardClose.X + 2 * _s, _cardClose.Y - 2 * _s);
                    cy += th;
                    if (sh > 0) { g.DrawString(type, sub, mb, x + pad, cy); cy += sh; }
                    cy += 4 * _s;
                    foreach (var r in rows)
                    {
                        g.DrawString(r.Key, font, mb, x + pad, cy);
                        bool warn = r.Key == "Squawk" && a.Emergency;
                        using (var vb = new SolidBrush(warn ? Color.FromArgb(255, 110, 110) : Fg)) g.DrawString(r.Value, font, vb, x + pad + 64 * _s, cy);
                        cy += rh;
                    }
                    cy += 6 * _s;
                    string lt = "Open on " + (Feed.ActiveSource == "adsb.lol" || Feed.ActiveSource == "adsb.fi" ? Feed.ActiveSource : "airplanes.live") + " ↗";
                    var ls = g.MeasureString(lt, link);
                    _cardLink = new RectangleF(x + pad, cy, ls.Width, ls.Height);
                    g.DrawString(lt, link, ab, x + pad, cy);
                }
            }
        }

        // ---------------------------------------------------------------- mouse

        private Aircraft HitTest(Point pt)
        {
            if (Feed == null) return null;
            var now = DateTime.UtcNow;
            Aircraft best = null;
            double bestD = 14 * _s;
            foreach (var a in Feed.Aircraft.Values)
            {
                double lat, lon;
                a.Predict(now, out lat, out lon);
                var p = ToScreen(lat, lon);
                double d = Math.Sqrt((p.X - pt.X) * (p.X - pt.X) + (p.Y - pt.Y) * (p.Y - pt.Y));
                if (d < bestD) { bestD = d; best = a; }
            }
            return best;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            if (_btnIn.Contains(e.Location)) { ZoomAt(1, new Point(Width / 2, Height / 2)); return; }
            if (_btnOut.Contains(e.Location)) { ZoomAt(-1, new Point(Width / 2, Height / 2)); return; }
            if (_btnHome.Contains(e.Location)) { ResetView(); Changed(); return; }
            if (_cardClose.Contains(e.Location)) { _selected = null; Invalidate(); return; }
            if (_cardLink.Contains(e.Location) && _selected != null)
            {
                try { Process.Start(AdsbFeed.WebsiteFor(Feed.ActiveSource, _selected)); } catch { }
                return;
            }
            _down = _last = e.Location;
            _pressed = true;
            _dragging = false;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_pressed && e.Button == MouseButtons.Left)
            {
                if (!_dragging && (Math.Abs(e.X - _down.X) > 3 || Math.Abs(e.Y - _down.Y) > 3)) { _dragging = true; Cursor = Cursors.SizeAll; }
                if (_dragging)
                {
                    double lat, lon;
                    FromScreen(Width / 2f - (e.X - _last.X), Height / 2f - (e.Y - _last.Y), out lat, out lon);
                    _cLat = lat; _cLon = lon;
                    _last = e.Location;
                    Invalidate();
                }
                return;
            }
            bool overUi = _btnIn.Contains(e.Location) || _btnOut.Contains(e.Location) || _btnHome.Contains(e.Location) || _cardLink.Contains(e.Location) || _cardClose.Contains(e.Location);
            Cursor = overUi || HitTest(e.Location) != null ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_pressed) return;
            _pressed = false;
            if (_dragging) { _dragging = false; Cursor = Cursors.Default; Changed(); return; }
            var hit = HitTest(e.Location);
            _selected = hit == null ? null : hit.Hex;
            Invalidate();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && HitTest(e.Location) == null && !_btnIn.Contains(e.Location) && !_btnOut.Contains(e.Location))
                ZoomAt(1, e.Location);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ZoomAt(e.Delta > 0 ? 1 : -1, e.Location);
        }

        // Zooms one level, keeping the map point under the cursor in place.
        private void ZoomAt(int delta, Point at)
        {
            int nz = Math.Max(3, Math.Min(15, _zoom + delta));
            if (nz == _zoom) return;
            double lat, lon;
            FromScreen(at.X, at.Y, out lat, out lon);
            _zoom = nz;
            double wx = Geo.WorldX(lon, _zoom, Tile) - (at.X - Width / 2.0);
            double wy = Geo.WorldY(lat, _zoom, Tile) - (at.Y - Height / 2.0);
            _cLon = Geo.Lon(wx, _zoom, Tile);
            _cLat = Geo.Lat(wy, _zoom, Tile);
            Invalidate();
            Changed();
        }

        private void Changed()
        {
            var h = ViewChanged;
            if (h != null) h();
        }

        public void SelectAircraft(string hex) { _selected = hex; Invalidate(); }
        public string Selected { get { return _selected; } }

        // Let the parent form handle the resize grip in the bottom-right corner.
        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84, HTTRANSPARENT = -1;
            if (m.Msg == WM_NCHITTEST)
            {
                long lp = m.LParam.ToInt64();
                var pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                if (pt.X > Width - 14 * _s && pt.Y > Height - 14 * _s) { m.Result = (IntPtr)HTTRANSPARENT; return; }
            }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _anim.Dispose(); Tiles.Clear(); }
            base.Dispose(disposing);
        }
    }
}
