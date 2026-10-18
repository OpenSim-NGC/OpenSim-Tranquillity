/*
 * Copyright (c) InWorldz Halcyon Developers
 * Copyright (c) Contributors, http://opensimulator.org/
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

// Adapted 2026 by Legion Builds from Halcyon's OpenSim/Region/CoreModules/World/Wind/Plugins/ZephyrWind.cs
// (Zephyr wind module, by Balpien Hammerer). What changed:
//   - The cell grid follows the region size (16 m cells, so a 1024 m x 1024 m region has 64 x 64 cells) instead of a
//     fixed 16 x 16 grid; the 16 x 16 array that viewers accept is sampled from it.
//   - The sun position comes from the region environment (the day fraction), because the region settings no longer
//     carry one; without an environment module the wall clock stands in, as Halcyon did for fixed-sun regions.
//   - Terrain is read from the scene heightmap and its per-cell statistics are kept until the terrain changes.
//   - The quantising fudge added to the sun position is gone: the day fraction is continuous.
//   - A cell whose wind speed is zero keeps a zero ground wind (the original divided by the speed).
//   - Ported to this tree's plugin interface (ILogger, Initialise, WindUpdate returns bool).
//   - Each update builds the new cell arrays (and, when the terrain changed, the new terrain statistics) and
//     publishes them as one immutable snapshot. Samplers read the current snapshot without taking a lock, so they
//     never wait for an update. Updates, WindSet and WindConfig still take turns with each other.
// The wind states set with WindSet are held in memory only; they are lost when the region restarts.

using System.Reflection;

using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules.World.Wind.Plugins;

public class ZephyrWind : IWindModelPlugin
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
    private Scene _scene;

    /// <summary>Width and depth of a wind cell, in metres.</summary>
    public const int CellSize = 16;

    /// <summary>The viewer's wind layer is always 16 x 16 cells, stretched over the region.</summary>
    public const int ViewerCells = 16;

    // Taken by the writers (WindUpdate, WindSet, WindConfig, Dispose) so that they take turns. Samplers never take it.
    private readonly object m_lock = new object();

    /// <summary>
    /// Everything a sampler reads, as one immutable set: the three bands of currents and the terrain statistics.
    /// A snapshot is never changed after it is published; a writer makes a new one, which may share the arrays that
    /// did not change (the terrain statistics are shared by every update until the terrain changes).
    /// </summary>
    private sealed class Snapshot
    {
        public readonly int CellsX;
        public readonly int CellsY;
        public readonly Vector2[] WaterCurrent;
        public readonly Vector2[] WindsGround;
        public readonly Vector2[] WindsAloft;
        public readonly float[] Skews;
        public readonly float[] Ranges;
        public readonly float[] MaxHeights;

        public Snapshot(int cellsX, int cellsY, Vector2[] water, Vector2[] ground, Vector2[] aloft,
            float[] skews, float[] ranges, float[] maxHeights)
        {
            CellsX = cellsX;
            CellsY = cellsY;
            WaterCurrent = water;
            WindsGround = ground;
            WindsAloft = aloft;
            Skews = skews;
            Ranges = ranges;
            MaxHeights = maxHeights;
        }
    }

    // To improve realism, Zephyr creates three bands of currents: sea, surface, and aloft.
    // sea 'wind' is an undulating current that simulates the back and forth surge.
    // surface wind is subject to terrain turblence.
    // wind aloft is laminar with gentle shifts.
    // Cells are stored row by row (y * CellsX + x), 16 m on a side, south-west corner origin.
    // Samplers read the current snapshot once (null until WindConfig, and again after Dispose); writers replace it
    // while holding m_lock.
    private volatile Snapshot m_snapshot;

    // Per cell, what the cell is allowed to do (fixed speed, terrain turbulence). Only read and changed under m_lock.
    private WindConstants[] m_options;

    private float m_avgWindStrength     = 5.0f; // Average magnitude of the wind vector
    private float m_avgWindDirection    = 0.0f; // Average direction of the wind in degrees
    private float m_varWindStrength     = 5.0f; // Max Strength  Variance
    private float m_varWindDirection    = 30.0f;// Max Direction Variance
    private float m_rateChangeAloft     = 1.0f; // Rate of change of the winds aloft
    private float m_rateChangeFlutter   = 64.0f;

    private float m_avgWaterStrength    = 1.0f;
    private float m_avgWaterDirection   = 10.0f;
    private float m_varWaterStrength    = 2.0f;
    private float m_varWaterDirection   = 180.0f;
    private float m_rateChangeSurge     = 128.0f;

    // The terrain statistics are only recomputed after the terrain changed or was replaced.
    private volatile bool m_terrainDirty = true;
    private ITerrainChannel m_terrainSeen;
    private bool m_terrainHooked;

    // Whether the warning about a day fraction that is not a number has been written since it last was a number.
    private bool m_dayFractionWarned;

    /// <summary>Number of wind cells along the region's width (one per 16 m).</summary>
    public int CellsX => m_snapshot?.CellsX ?? 0;

    /// <summary>Number of wind cells along the region's depth (one per 16 m).</summary>
    public int CellsY => m_snapshot?.CellsY ?? 0;

    /// <summary>
    /// Supplies the fraction of the region's day (0 to 1) that the prevailing wind follows. When null, the region
    /// environment's day fraction is used. Present so the wind can be driven by a fixed time.
    /// </summary>
    public Func<float> DayFractionSource { get; set; }

    #region IPlugin Members

    public string Version
    {
        get { return "1.0.0.0"; }
    }

    public string Name
    {
        get { return "ZephyrWind"; }
    }

    public void Initialise()
    {

    }

    #endregion

    #region IDisposable Members

    public void Dispose()
    {
        if (m_terrainHooked && _scene != null)
            _scene.EventManager.OnTerrainTainted -= OnTerrainTainted;
        m_terrainHooked = false;

        lock (m_lock)
        {
            m_snapshot = null;
            m_options = null;
        }
    }

    #endregion

    #region IWindModelPlugin Members

    public void WindConfig(Scene scene, Nini.Config.IConfig windConfig)
    {
        _scene = scene;

        // One cell per 16 m of region, so larger regions get more cells.
        int cellsX = Math.Max(1, ((int)scene.RegionInfo.RegionSizeX + CellSize - 1) / CellSize);
        int cellsY = Math.Max(1, ((int)scene.RegionInfo.RegionSizeY + CellSize - 1) / CellSize);

        lock (m_lock)
        {
            int count = cellsX * cellsY;
            m_options = new WindConstants[count];

            // Default to ground turbulence
            for (int i = 0; i < count; i++)
                m_options[i] = WindConstants.WindSpeedTurbulence;

            m_terrainDirty = true;

            m_snapshot = new Snapshot(cellsX, cellsY,
                new Vector2[count], new Vector2[count], new Vector2[count],
                new float[count], new float[count], new float[count]);
        }

        if (!m_terrainHooked)
        {
            scene.EventManager.OnTerrainTainted += OnTerrainTainted;
            m_terrainHooked = true;
        }

        if (windConfig != null)
        {
            // As in the original, the strength key of the other wind plugins does not apply here: avg_strength does.
            m_avgWindStrength = WindValues.ReadFinite(windConfig, Name, "avg_strength", 5.0F, m_log);

            m_avgWindDirection = WindValues.ReadFinite(windConfig, Name, "avg_direction", 0.0F, m_log);
            m_varWindStrength  = WindValues.ReadFinite(windConfig, Name, "var_strength", 5.0F, m_log);
            m_varWindDirection = WindValues.ReadFinite(windConfig, Name, "var_direction", 30.0F, m_log);
            m_rateChangeAloft   = WindValues.ReadFinite(windConfig, Name, "rate_change_aloft", 1.0f, m_log);
            m_rateChangeFlutter = WindValues.ReadFinite(windConfig, Name, "rate_change_flutter", 64.0f, m_log);

            LogSettings();
        }
    }

    private void OnTerrainTainted()
    {
        m_terrainDirty = true;
    }

    /// <summary>Hours into the region's day (0 to 24) that the prevailing wind follows.</summary>
    private float SunHour()
    {
        float dayFraction;
        Func<float> source = DayFractionSource;
        IEnvironmentModule env;
        if (source != null)
            dayFraction = source();
        else if ((env = _scene?.RequestModuleInterface<IEnvironmentModule>()) != null)
            dayFraction = env.GetRegionDayFractionTime();
        else
            dayFraction = (float)((DateTime.UtcNow.TimeOfDay.TotalSeconds / 600.0) % 24.0) / 24.0f;

        // A day length of 0 in the environment gives a day fraction that is not a number, and that would turn every
        // wind cell into NaN. Use the start of the day instead, and say so once until the fraction is a number again.
        if (!float.IsFinite(dayFraction))
        {
            if (!m_dayFractionWarned)
            {
                m_dayFractionWarned = true;
                m_log.LogWarning("[{0}] The region's day fraction is {1}, not a finite number (a day length of 0?); using 0.", Name, dayFraction);
            }
            dayFraction = 0f;
        }
        else
        {
            m_dayFractionWarned = false;
        }

        return Utils.Clamp(dayFraction, 0f, 1f) * 24.0f;
    }

    public bool WindUpdate(uint frame)
    {
        if (_scene == null)
            return false;

        double sunpos = SunHour();
        float h2o = (float)_scene.RegionInfo.RegionSettings.WaterHeight;

        lock (m_lock)
        {
            Snapshot current = m_snapshot;
            if (current == null)
                return false;

            // Based on the Prevailing wind algorithm
            // Inspired by Kanker Greenacre
            // Modified by Balpien Hammerer to account for terrain turbulence, winds aloft and wind setters

            // Wind Direction
            double ThetaWD = (sunpos / 24.0 * (2.0 * Math.PI) * m_rateChangeFlutter) % (Math.PI * 2.0);
            double offset = Math.Sin(ThetaWD) * Math.Sin(ThetaWD*2) * Math.Sin(ThetaWD*9) * Math.Cos(ThetaWD*4);
            double windDir = m_avgWindDirection * (Math.PI/180.0f) + (m_varWindDirection * (Math.PI/180.0f) * offset);

            // Wind Speed
            double ThetaWS = (sunpos / 24.0 * (2.0 * Math.PI) * m_rateChangeAloft) % (Math.PI * 2.0);
            offset = Math.Sin(ThetaWS) * Math.Sin(ThetaWS*4) + (Math.Sin(ThetaWS*13) / 3.0);
            double windSpeed = (m_avgWindStrength + (m_varWindStrength * offset));

            if (windSpeed < 0)
            {
                windSpeed = -windSpeed;
                windDir += Math.PI;
            }

            // Water Direction
            double ThetaHD = (sunpos / 24.0 * (2.0 * Math.PI) * m_rateChangeFlutter) % (Math.PI * 2.0);
            double woffset = Math.Sin(ThetaHD) * Math.Sin(ThetaHD*2) * Math.Sin(ThetaHD*9) * Math.Cos(ThetaHD*4);
            double waterDir = m_avgWaterDirection * (Math.PI/180.0f) + (m_varWaterDirection * (Math.PI/180.0f) * woffset);

            // Water Speed
            double ThetaHS = (sunpos / 24.0 * (2.0 * Math.PI) * m_rateChangeSurge) % (Math.PI * 2.0);
            woffset = Math.Sin(ThetaHS) * Math.Sin(ThetaHS*3) * Math.Sin(ThetaHS*9) * Math.Cos(ThetaHS*4) * Math.Cos(ThetaHS*13);
            double waterSpeed = (m_avgWaterStrength + (m_varWaterStrength * woffset));

            if (waterSpeed < 0)
            {
                waterSpeed = -waterSpeed;
                waterDir += Math.PI;
            }

            ITerrainChannel terrain = _scene.Heightmap;
            if (terrain != m_terrainSeen)
            {
                m_terrainSeen = terrain;
                m_terrainDirty = true;
            }

            // The terrain statistics of the snapshot in use stay until the terrain changed; then the update makes new
            // ones and publishes them with the new cells.
            int cellsX = current.CellsX;
            int cellsY = current.CellsY;
            float[] skews = current.Skews;
            float[] ranges = current.Ranges;
            float[] maxheights = current.MaxHeights;

            bool computeTerrain = m_terrainDirty;
            if (computeTerrain)
            {
                // A missing heightmap is flat ground, and is looked at again next time.
                m_terrainDirty = terrain == null;
                skews = new float[cellsX * cellsY];
                ranges = new float[cellsX * cellsY];
                maxheights = new float[cellsX * cellsY];
                ComputeTerrainStatistics(terrain, h2o, cellsX, cellsY, skews, ranges, maxheights);
            }

            // Winds aloft and water currents carry the fixed values of cells set with WindSet from one update to the
            // next, so the new arrays start as copies of the published ones; the ground wind is made new every time.
            Vector2[] windsAloft = (Vector2[])current.WindsAloft.Clone();
            Vector2[] waterCurrent = (Vector2[])current.WaterCurrent.Clone();
            Vector2[] windsGround = new Vector2[cellsX * cellsY];

            // Set the predominate wind in each cell, but examine the terrain heights
            // to adjust the winds. Elevation and the pattern of heights in each 16m
            // cell infuence the ground winds.
            for (int y = 0; y < cellsY; y++)
            {
                for (int x = 0; x < cellsX; x++)
                {
                    int cell = y * cellsX + x;

                    // Look for the range of heights in the cell and the overall skewness. Use this
                    // to determine ground induced deflection. It is a rough approximation of
                    // ground wind turbulance. The max height is used later to determine the boundary layer.
                    float cellrange = Math.Max(ranges[cell] / 5.0f, 1.0f);
                    float cellskew = skews[cell];

                    // Begin with winds aloft,starting with the fixed wind value set by wind setters.
                    Vector2 wind = windsAloft[cell];

                    // Update the cell with default zephyr winds aloft (no turbulence) if the wind speed is not fixed.
                    if ((m_options[cell] & WindConstants.WindSpeedFixed) == 0)
                    {
                        // Compute the winds aloft (no turbulence)
                        wind.X = (float)Math.Cos(windDir);
                        wind.Y = (float)Math.Sin(windDir);
                        wind.X *= (float)windSpeed;
                        wind.Y *= (float)windSpeed;
                        windsAloft[cell] = wind;
                    }

                    // Compute ground winds (apply terrain turbulence) from the winds aloft cell.
                    if ((m_options[cell] & WindConstants.WindSpeedTurbulence) != 0)
                    {
                        double speed = Math.Sqrt(wind.X * wind.X + wind.Y * wind.Y);
                        if (speed > 0.0)
                        {
                            wind = wind / (float)speed;
                            double dir = Math.Atan2(wind.Y, wind.X);

                            wind.X = (float)Math.Cos((Math.PI * cellskew * 0.333 * cellrange) + dir);
                            wind.Y = (float)Math.Sin((Math.PI * cellskew * 0.333 * cellrange) + dir);
                            wind.X *= (float)speed;
                            wind.Y *= (float)speed;
                        }
                    }

                    windsGround[cell] = wind;

                    // Update the cell with default zephyr water currents if the speed is not fixed.
                    if ((m_options[cell] & WindConstants.WindSpeedFixed) == 0)
                    {
                        wind.X = (float)Math.Cos(waterDir);
                        wind.Y = (float)Math.Sin(waterDir);
                        wind.X *= (float)waterSpeed;
                        wind.Y *= (float)waterSpeed;
                        waterCurrent[cell] = wind;
                    }
                }
            }

            m_snapshot = new Snapshot(cellsX, cellsY, waterCurrent, windsGround, windsAloft, skews, ranges, maxheights);
        }

        return true;
    }

    /// <summary>
    /// Gather, for every cell, the range of heights, their skewness and the highest point. For the purpose of these
    /// computations, it is the above water height that matters: any ground below water is treated as zero.
    /// </summary>
    private static void ComputeTerrainStatistics(ITerrainChannel terrain, float h2o, int cellsX, int cellsY,
        float[] cellSkews, float[] cellRanges, float[] cellMaxHeights)
    {
        RunningStat rsCell = new RunningStat();
        int width = terrain?.Width ?? 0;
        int height = terrain?.Height ?? 0;

        for (int y = 0; y < cellsY; y++)
        {
            for (int x = 0; x < cellsX; x++)
            {
                rsCell.Clear();

                for (int iy = 0; iy < CellSize; iy++)
                {
                    int ty = y * CellSize + iy;
                    if (ty >= height)
                        break;

                    for (int ix = 0; ix < CellSize; ix++)
                    {
                        int tx = x * CellSize + ix;
                        if (tx >= width)
                            break;

                        float gnd = Math.Max(terrain[tx, ty] - h2o, 0);
                        rsCell.Push(gnd);
                    }
                }

                int cell = y * cellsX + x;
                if (rsCell.NumDataValues() == 0)
                {
                    cellRanges[cell] = 0;
                    cellSkews[cell] = 0;
                    cellMaxHeights[cell] = 0;
                    continue;
                }

                cellRanges[cell] = (float)(rsCell.Max() - rsCell.Min());
                cellSkews[cell] = (float)rsCell.Skewness();
                cellMaxHeights[cell] = (float)rsCell.Max();
            }
        }
    }

    /// <summary>
    /// Determine the wind speed at the specified local region coordinate.
    /// Wind speed returned is influenced by the nearest neighbors.
    /// </summary>
    /// <param name="fX">region local coordinate</param>
    /// <param name="fY">region local coordinate</param>
    /// <param name="fZ">decides between the water current, the surface wind and the wind aloft</param>
    /// <returns></returns>
    public Vector3 WindSpeed(float fX, float fY, float fZ)
    {
        if (_scene == null || fX < 0 || fY < 0)
            return Vector3.Zero;

        float h2o = (float)_scene.RegionInfo.RegionSettings.WaterHeight;

        // One read of the current snapshot, and no lock: an update in progress is not waited for.
        Snapshot snapshot = m_snapshot;
        if (snapshot == null)
            return Vector3.Zero;

        int x0 = (int)fX / CellSize;
        int y0 = (int)fY / CellSize;
        if (x0 >= snapshot.CellsX || y0 >= snapshot.CellsY)
            return Vector3.Zero;

        //Set the wind maxtrix based on the z-position
        float maxheight = snapshot.MaxHeights[y0 * snapshot.CellsX + x0];
        float range = Math.Max(snapshot.Ranges[y0 * snapshot.CellsX + x0], 15.0f);
        // The cell statistics are heights above the water, so the boundary in region height adds the water height.
        float boundary = h2o + maxheight + range;
        Vector2[] windmatrix;

        if (fZ < h2o)
            windmatrix = snapshot.WaterCurrent;
        else if (fZ <= boundary)
            windmatrix = snapshot.WindsGround;
        else
            windmatrix = snapshot.WindsAloft;

        Vector2 wind = Bilinear(snapshot, windmatrix, fX, fY);
        return new Vector3(wind.X, wind.Y, 0f);
    }

    /// <summary>
    /// Perform a bilinear interpolation of wind speeds between the four cells around the position (the caller has
    /// checked that the position is inside the region).
    /// f(x,y) = f(0,0) * (1-x)(1-y) + f(1,0) * x(1-y) + f(0,1) * (1-x)y + f(1,1) * xy.
    /// </summary>
    private static Vector2 Bilinear(Snapshot snapshot, Vector2[] windmatrix, float fX, float fY)
    {
        int cellsX = snapshot.CellsX;
        int cellsY = snapshot.CellsY;
        int x0 = Math.Min((int)fX / CellSize, cellsX - 1);
        int y0 = Math.Min((int)fY / CellSize, cellsY - 1);
        int x1 = Math.Min(x0 + 1, cellsX - 1);
        int y1 = Math.Min(y0 + 1, cellsY - 1);

        float dx = (fX - x0 * CellSize) / (float)CellSize;
        float dy = (fY - y0 * CellSize) / (float)CellSize;

        Vector2 wind = Vector2.Zero;
        wind.X  = windmatrix[y0 * cellsX + x0].X * (1.0f-dx) * (1.0f-dy);
        wind.X += windmatrix[y0 * cellsX + x1].X * dx * (1.0f-dy);
        wind.X += windmatrix[y1 * cellsX + x0].X * dy * (1.0f-dx);
        wind.X += windmatrix[y1 * cellsX + x1].X * dx *dy;

        wind.Y  = windmatrix[y0 * cellsX + x0].Y * (1.0f-dx) * (1.0f-dy);
        wind.Y += windmatrix[y0 * cellsX + x1].Y * dx * (1.0f-dy);
        wind.Y += windmatrix[y1 * cellsX + x0].Y * dy * (1.0f-dx);
        wind.Y += windmatrix[y1 * cellsX + x1].Y * dx *dy;

        return wind;
    }

    /// <summary>
    /// Set the wind speed characteristics for a cell. A cell is
    /// presently 16x16m SW corner origined.
    /// </summary>
    /// <param name="type">WindConstant - fixed or default</param>
    /// <param name="loc">Region local coordinate</param>
    /// <param name="speed">Wind Speed</param>
    public void WindSet(int type, Vector3 loc, Vector3 speed)
    {
        if (_scene == null || loc.X < 0 || loc.Y < 0)
            return;

        float h2o = (float)_scene.RegionInfo.RegionSettings.WaterHeight;
        WindConstants wtype = (WindConstants)type;

        // Takes its turn with the updates: a WindSet made while an update runs is applied after it, on top of what
        // the update published, so it is never overwritten by that update's older copy of the cell.
        lock (m_lock)
        {
            Snapshot current = m_snapshot;
            if (current == null)
                return;

            int cellsX = current.CellsX;
            int x = (int)loc.X / CellSize;
            int y = (int)loc.Y / CellSize;
            if (x >= cellsX || y >= current.CellsY)
                return;

            // Handle default case, which really means not fixed + terrain turbulence
            if (wtype == WindConstants.WindSpeedDefault)
            {
                wtype = WindConstants.WindSpeedTurbulence;
            }

            //  Save the overrides
            m_options[y * cellsX + x] = wtype;

            // If fixed wind set, copy the set windspeed into the winds aloft cell.
            // If the z location is underwater, then set the water current speed.
            // The published arrays are never changed: the one that changes is copied, the others are shared.
            if ((wtype & WindConstants.WindSpeedFixed) != 0)
            {
                Vector2 fixedWind = new Vector2(speed.X, speed.Y);
                if (loc.Z >= h2o)
                {
                    Vector2[] aloft = (Vector2[])current.WindsAloft.Clone();
                    aloft[y * cellsX + x] = fixedWind;
                    m_snapshot = new Snapshot(cellsX, current.CellsY, current.WaterCurrent, current.WindsGround, aloft,
                        current.Skews, current.Ranges, current.MaxHeights);
                }
                else
                {
                    Vector2[] water = (Vector2[])current.WaterCurrent.Clone();
                    water[y * cellsX + x] = fixedWind;
                    m_snapshot = new Snapshot(cellsX, current.CellsY, water, current.WindsGround, current.WindsAloft,
                        current.Skews, current.Ranges, current.MaxHeights);
                }
            }
        }
    }

    /// <summary>
    /// Return the wind speed matrix. This must comply with the viewer's expectations: a 16 x 16 array, which the
    /// viewer stretches over the whole region. Each entry is the surface wind at the south-west corner of its
    /// share of the region (1/16 of the region's width and depth), so a 256 m region gets its cells as they are
    /// and a larger region gets a sample of its finer grid.
    /// </summary>
    /// <returns>wind speed matrix</returns>
    public Vector2[] WindLLClientArray()
    {
        Vector2[] viewer = new Vector2[ViewerCells * ViewerCells];

        // The current snapshot, read once and without a lock.
        Snapshot snapshot = m_snapshot;
        if (snapshot == null)
            return viewer;

        float stepX = _scene.RegionInfo.RegionSizeX / (float)ViewerCells;
        float stepY = _scene.RegionInfo.RegionSizeY / (float)ViewerCells;

        for (int j = 0; j < ViewerCells; j++)
        {
            for (int i = 0; i < ViewerCells; i++)
                viewer[j * ViewerCells + i] = Bilinear(snapshot, snapshot.WindsGround, i * stepX, j * stepY);
        }

        return viewer;
    }

    public string Description
    {
        get
        {
            return "Provides a sun and terrain influenced predominate wind direction that can be adjusted by wind setters.";
        }
    }

    public System.Collections.Generic.Dictionary<string, string> WindParams()
    {
        Dictionary<string, string> Params = new Dictionary<string, string>();

        Params.Add("avgStrength", "average wind strength");
        Params.Add("avgDirection", "average wind direction in degrees");
        Params.Add("varStrength", "allowable variance in wind strength");
        Params.Add("varDirection", "allowable variance in wind direction in +/- degrees");
        Params.Add("rateChangeAloft", "rate of change of winds aloft");
        Params.Add("rateChangeFlutter", "rate of change of wind flutter");

        return Params;
    }

    public void WindParamSet(string param, float value)
    {
        switch (param)
        {
            case "avgStrength":
                 m_avgWindStrength = value;
                 break;
            case "avgDirection":
                 m_avgWindDirection = value;
                 break;
             case "varStrength":
                 m_varWindStrength = value;
                 break;
             case "varDirection":
                 m_varWindDirection = value;
                 break;
             case "rateChangeAloft":
                 m_rateChangeAloft = value;
                 break;
            case "rateChangeFlutter":
                 m_rateChangeFlutter = value;
                 break;
            default:
                 // Nothing changed, so nothing is logged.
                 return;
        }

        LogSettings();
    }

    public float WindParamGet(string param)
    {
        switch (param)
        {
            case "avgStrength":
                return m_avgWindStrength;
            case "avgDirection":
                return m_avgWindDirection;
            case "varStrength":
                return m_varWindStrength;
            case "varDirection":
                return m_varWindDirection;
            case "rateChangeAloft":
                return m_rateChangeAloft;
            case "rateChangeFlutter":
                return m_rateChangeFlutter;
            default:
                m_log.LogInformation("[{0}] Unknown parameter {1}", this.Name, param);
                return 0.0f;
        }
    }

    #endregion

    /// <summary>One line with every setting, written when the settings are read and once for each change.</summary>
    private void LogSettings()
    {
        m_log.LogInformation(
            "[{0}] Settings: average strength {1}, average direction {2}, strength variance {3}, direction variance {4}, rate change aloft {5}, rate change flutter {6}",
            Name, m_avgWindStrength, m_avgWindDirection, m_varWindStrength, m_varWindDirection, m_rateChangeAloft, m_rateChangeFlutter);
    }
}

/// <summary>
///  A running variance and standard deviation class
///  used to gather real time metrics.
/// </summary>
// Borrowed from: http://www.johndcook.com/standard_deviation.html and http://www.johndcook.com/skewness_kurtosis.html
// John D. Cook's terms for that code: "You may use the code however you wish".
internal class RunningStat
{
    private long    n;
    private double  M1, M2, M3, /*M4,*/ M5, M6;

    public RunningStat()
    {
        Clear();
    }

    public void Clear()
    {
        n = 0;
        M1 = M2 = M3 = /*M4 =*/ M5 = M6 = 0.0;
    }

    public void Push(double x)
    {
        double delta, delta_n, term1;
        long n1 = n;

        // See Knuth TAOCP vol 2, 3rd edition, page 232
        n++;
        delta = x - M1;
        delta_n = delta / n;
        term1 = delta * delta_n * n1;
        M1 += delta_n;

        //profiling runs show M4 calculation as hot and we're not using it. so comment out for now
        //M4 += term1 * delta_n2 * (n*n - 3*n + 3) + 6 * delta_n2 * M2 - 4 * delta_n * M3;
        M3 += term1 * delta_n * (n - 2) - 3 * delta_n * M2;
        M2 += term1;

        if (n == 1)
        {
            M5 = M6 = x;
        }
        else
        {
            if (x < M5) M5 = x;
            if (x > M6) M6 = x;
        }

    }

    public long NumDataValues()
    {
        return n;
    }

    public double Min()
    {
        return M5;
    }

    public double Max()
    {
        return M6;
    }

    public double Mean()
    {
        return M1;
    }

    public double Variance()
    {
        if (n == 0) return 0.0;
        return M2/(n-1.0);
    }

    public double StandardDeviation()
    {
        return Math.Sqrt( Variance() );
    }

    public double Skewness()
    {
        double result = Math.Sqrt((double)n) * M3/ Math.Pow(M2, 1.5);
        if (double.IsNaN(result)) result = 0.0;
        return result;
    }

    /*public double Kurtosis()
    {
        return (double)n*M4 / (M2*M2) - 3.0;
    }*/
}
