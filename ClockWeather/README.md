# ClockWeather

A small borderless desktop clock for Windows that shows the current weather and a live map of the aircraft flying around you.

- Large clock and date, 12 or 24 hour
- Current weather from [Open-Meteo](https://open-meteo.com) (no API key)
- Live ADS-B flight map from free community networks ([airplanes.live](https://airplanes.live), [adsb.lol](https://adsb.lol), [adsb.fi](https://adsb.fi)), or your own dump1090 / readsb receiver
- Altitude-coloured aircraft, trails, range rings, click a plane for details
- Dark, light or OpenStreetMap map styles, tiles cached on disk
- Single 70 KB exe, no installer, no runtime to install (uses .NET Framework 4.8, built into Windows 10 and 11)

## Download

Get `ClockWeather.exe` from the [latest release](../../releases/latest), put it in a folder of its own and run it.

Windows SmartScreen may warn that the app is from an unknown publisher, because the exe is not code-signed. Click **More info**, then **Run anyway**. If you'd rather not trust a binary, build it yourself (below); it takes a few seconds.

## Using it

- **Drag** anywhere on the clock to move it. **Resize** from the bottom-right corner.
- **Right-click** for the menu: always on top, 24-hour time, map style, range, flight data source, labels, trails.
- **Map**: scroll or double-click to zoom, drag to pan, click an aircraft for its details, ⌂ to recentre.

## Settings

On first run the app creates `clock.ini` next to the exe. Right-click > **Edit location...** opens it. Set your own coordinates (find them at [latlong.net](https://www.latlong.net)), save, and restart the clock.

```ini
LocationName=Singapore
Latitude=1.3521
Longitude=103.8198
Use24Hour=True
FlightSource=airplanes.live   # airplanes.live, adsb.lol, adsb.fi, or local
LocalReceiverUrl=http://localhost:8080/data/aircraft.json
FlightRadiusNm=50
MapStyle=dark                 # dark, light, street
```

Map tiles are cached in `%LOCALAPPDATA%\ClockWeather\tiles`.

## Building from source

No Visual Studio or SDK needed. Windows includes a C# compiler as part of .NET Framework 4.x:

```
build.cmd
```

This produces `ClockWeather.exe` in the same folder. The code is written to C# 5 so that built-in compiler can handle it.

## Data sources and fair use

Flight data comes from volunteer-run networks that are free for personal, non-commercial use and rate limited; the app polls every 5 seconds and falls back to another network if one fails. Weather is from Open-Meteo's free tier. Map tiles are from CARTO and the OpenStreetMap Foundation; please keep the default refresh rates and don't use this app for bulk downloading.

Map data © OpenStreetMap contributors. Basemaps © CARTO.
