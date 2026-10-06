/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using CoreJ2K;
using CoreJ2K.j2k.util;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Agent.TextureSender;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using SkiaSharp;
using Xunit;

namespace OpenSim.Region.CoreModules.Tests.Agent.TextureSender;

/// <summary>
/// J2KImage (LindenUDP) turns a requested discard level into a byte cutoff with the layer table
/// J2KDecoderModule returns, so that table has to say where each quality layer starts in the file.
///
/// The codestreams are encoded here with CoreJ2K, in layer-resolution-component-position order and
/// with SOP markers: every packet starts with FF 91 00 04 and all packets of layer n come before any
/// packet of layer n + 1. With 3 components and 5 decomposition levels a layer has 3 x 6 = 18
/// packets, so SOP marker number n x 18 is where layer n starts. That is the oracle; it does not use
/// the code under test. Code-block data never holds FF followed by a byte above 8F, so FF 91 only
/// occurs as a marker. The number of layers is read from the COD marker (FF 52): its 16-bit layer
/// count follows the segment length, the coding style byte and the progression order byte.
/// </summary>
public class J2KLayerBoundaryTests
{
    private const int PacketsPerLayer = 3 * 6;

    [Fact]
    public void ModuleTableIsTheLayerStartsOfALayerFirstCodestream()
    {
        byte[] data = Encode("layer");
        int[] starts = OracleLayerStarts(data);

        J2KDecoderModule module = SetupModule(new DictionaryAssetCache());
        Assert.True(module.Decode(UUID.Random(), data, out J2KLayerInfo[] layers, out int components));

        Assert.Equal(3, components);
        AssertSameLayers(ToLayers(starts, data.Length), layers);
        Assert.NotEqual(GuessedEnds(data.Length), layers.Select(l => l.End).ToArray());
    }

    [Fact]
    public void ModuleWarnsAndGuessesForACodestreamThatIsNotLayerFirst()
    {
        byte[] data = Encode("res");
        UUID id = UUID.Random();
        J2KDecoderModule module = SetupModule(new DictionaryAssetCache());

        J2KLayerInfo[] layers;
        using (CapturedLog log = new CapturedLog())
        {
            Assert.True(module.Decode(id, data, out layers, out _));

            string expected = "Failed to decode layer data for texture " + id + ", guessing sane defaults";
            Assert.Single(log.Warnings, w => w.Contains(expected));
        }

        Assert.Equal(GuessedEnds(data.Length), layers.Select(l => l.End).ToArray());
    }

    [Fact]
    public void GuessedTableCachedUnderTheOldNameIsNotUsed()
    {
        byte[] data = Encode("layer");
        UUID id = UUID.Random();
        DictionaryAssetCache cache = new DictionaryAssetCache();

        // A guessed table in the format the module writes, under the name used for it before
        int[] guessed = GuessedEnds(data.Length);
        AssetBase stale = new AssetBase("j2k" + id, "j2k" + id, (sbyte)AssetType.Notecard, UUID.Zero.ToString());
        stale.Data = Util.UTF8.GetBytes(string.Join("\n", guessed.Select((end, i) => (i == 0 ? 0 : guessed[i - 1] + 1) + "|" + end + "|0")));
        cache.Cache(stale);

        J2KDecoderModule module = SetupModule(cache);
        Assert.True(module.Decode(id, data, out J2KLayerInfo[] layers, out _));

        J2KLayerInfo[] expected = ToLayers(OracleLayerStarts(data), data.Length);
        AssertSameLayers(expected, layers);

        // The recomputed table is stored under the new name and the old entry is left alone.
        // Another module reads the table back without looking at the bytes it is given.
        Assert.True(cache.Check("j2klayers" + id));
        Assert.Same(stale, cache.GetCached("j2k" + id));
        J2KDecoderModule second = SetupModule(cache);
        Assert.True(second.Decode(id, new byte[] { 1, 2, 3 }, out J2KLayerInfo[] cached, out _));
        AssertSameLayers(expected, cached);
    }

    [Fact]
    public void ReaderReturnsTheLayerStartsTheSopMarkersShow()
    {
        byte[] data = Encode("layer");

        Assert.Equal(OracleLayerStarts(data), J2KLayerBoundaryReader.ReadLayerStarts(data));
        AssertSameLayers(ToLayers(OracleLayerStarts(data), data.Length), J2KLayerBoundaryReader.Read(data));
    }

    [Fact]
    public void ReaderGivesOffsetsInTheFileForAJp2File()
    {
        byte[] raw = Encode("layer");
        byte[] jp2 = Encode("layer", fileFormat: true);

        // The JP2 file wraps the same codestream, which starts at its SOC marker FF 4F FF 51
        int offset = jp2.AsSpan().IndexOf(new byte[] { 0xFF, 0x4F, 0xFF, 0x51 });
        Assert.True(offset > 0);
        Assert.Equal(raw, jp2.AsSpan(offset, raw.Length).ToArray());

        Assert.Equal(OracleLayerStarts(raw).Select(s => s + offset).ToArray(), J2KLayerBoundaryReader.ReadLayerStarts(jp2));
    }

    [Theory]
    [InlineData("res")]
    [InlineData("res-pos")]
    [InlineData("pos-comp")]
    [InlineData("comp-pos")]
    public void ReaderHasNoTableForACodestreamThatIsNotLayerFirst(string progression)
    {
        byte[] data = Encode(progression);

        Assert.Null(J2KLayerBoundaryReader.ReadLayerStarts(data));
        Assert.Null(J2KLayerBoundaryReader.Read(data));
    }

    [Fact]
    public void ReaderHasNoTableForMoreThanOneTile()
    {
        byte[] data = Encode("layer", tiles: "64 64");

        Assert.Null(J2KLayerBoundaryReader.ReadLayerStarts(data));
    }

    [Fact]
    public void ReaderThrowsNothingForTruncatedOrGarbageBytes()
    {
        byte[] data = Encode("layer");
        List<byte[]> inputs = new List<byte[]>();
        foreach (int length in new[] { 0, 1, 2, 4, 16, 64, 200, data.Length / 4, data.Length / 2, data.Length - 1 })
            inputs.Add(data.AsSpan(0, length).ToArray());

        Random random = new Random(11);
        for (int i = 0; i < 20; i++)
        {
            byte[] garbage = new byte[random.Next(1, 4000)];
            random.NextBytes(garbage);
            inputs.Add(garbage);

            // A valid main header followed by garbage
            byte[] mixed = (byte[])data.Clone();
            random.NextBytes(mixed.AsSpan(OracleLayerStarts(data)[0]));
            inputs.Add(mixed);
        }

        foreach (byte[] input in inputs)
        {
            Assert.Null(Record.Exception(() => J2KLayerBoundaryReader.ReadLayerStarts(input)));
            Assert.Null(Record.Exception(() => J2KLayerBoundaryReader.Read(input)));
        }
    }

    /// <summary>The byte offset where each layer's first packet starts, from the SOP markers.</summary>
    internal static int[] OracleLayerStarts(byte[] data)
    {
        int layerCount = CodLayerCount(data);
        List<int> sops = FindSops(data);
        Assert.True(layerCount >= 3, $"encoder wrote {layerCount} layers");
        Assert.Equal(layerCount * PacketsPerLayer, sops.Count);

        int[] starts = new int[layerCount];
        for (int n = 0; n < layerCount; n++)
            starts[n] = sops[n * PacketsPerLayer];
        return starts;
    }

    /// <summary>The table the removed CSJ2K path built from layer start offsets.</summary>
    internal static J2KLayerInfo[] ToLayers(int[] starts, int length)
    {
        J2KLayerInfo[] layers = new J2KLayerInfo[starts.Length];
        for (int i = 0; i < starts.Length; i++)
        {
            layers[i].Start = i == 0 ? 0 : starts[i];
            layers[i].End = i == starts.Length - 1 ? length : starts[i + 1] - 1;
        }
        return layers;
    }

    /// <summary>The layer ends CreateDefaultLayers guesses for a file of this length.</summary>
    internal static int[] GuessedEnds(int length)
    {
        return new[]
        {
            (int)(length * 0.02f) - 1,
            (int)(length * 0.05f) - 1,
            (int)(length * 0.20f) - 1,
            (int)(length * 0.50f) - 1,
            length,
        };
    }

    internal static void AssertSameLayers(J2KLayerInfo[] expected, J2KLayerInfo[] actual)
    {
        Assert.Equal(expected.Select(l => (l.Start, l.End)).ToArray(), actual.Select(l => (l.Start, l.End)).ToArray());
    }

    /// <summary>
    /// A 128x128 RGB image with five quality layers in the given progression order, SOP markers on,
    /// as a raw codestream or a JP2 file.
    /// </summary>
    internal static byte[] Encode(string progression, string? tiles = null, bool fileFormat = false)
    {
        using SKBitmap bitmap = new SKBitmap(128, 128, SKColorType.Rgb888x, SKAlphaType.Opaque);
        Random random = new Random(7);
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 128; x++)
                bitmap.SetPixel(x, y, new SKColor((byte)(x ^ y), (byte)random.Next(256), (byte)((x * y) >> 6)));

        ParameterList pl = new ParameterList(J2kImage.GetDefaultEncoderParameterList());
        pl["verbose"] = "off";
        pl["file_format"] = fileFormat ? "on" : "off";
        pl["Alayers"] = "0.05 0.2 0.5 1.0 2.0";
        pl["Aptype"] = progression;
        pl["Psop"] = "on";
        if (tiles != null)
            pl["tiles"] = tiles;

        return J2kImage.ToBytes(bitmap, pl);
    }

    private static int CodLayerCount(byte[] data)
    {
        int cod = data.AsSpan().IndexOf(new byte[] { 0xFF, 0x52 });
        Assert.True(cod > 0);
        return (data[cod + 6] << 8) | data[cod + 7];
    }

    private static List<int> FindSops(byte[] data)
    {
        List<int> result = new List<int>();
        for (int i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] == 0xFF && data[i + 1] == 0x91 && data[i + 2] == 0x00 && data[i + 3] == 0x04)
                result.Add(i);
        }
        return result;
    }

    private static J2KDecoderModule SetupModule(IAssetCache cache)
    {
        Scene scene = new SceneHelpers().SetupScene();
        scene.RegisterModuleInterface<IAssetCache>(cache);
        J2KDecoderModule module = new J2KDecoderModule();
        SceneHelpers.SetupSceneModules(scene, module);
        return module;
    }

    /// <summary>An asset cache that keeps what it is given, so the module's table cache can be read back.</summary>
    private sealed class DictionaryAssetCache : IAssetCache
    {
        private readonly Dictionary<string, AssetBase> m_assets = new Dictionary<string, AssetBase>();

        public void Cache(AssetBase asset, bool replace = false) => m_assets[asset.ID] = asset;
        public void CacheNegative(string id) { }
        public bool Get(string id, out AssetBase asset)
        {
            m_assets.TryGetValue(id, out asset!);
            return true;
        }
        public AssetBase GetCached(string id) => m_assets.TryGetValue(id, out AssetBase? asset) ? asset : null!;
        public bool GetFromMemory(string id, out AssetBase asset) => m_assets.TryGetValue(id, out asset!);
        public bool Check(string id) => m_assets.ContainsKey(id);
        public void Expire(string id) => m_assets.Remove(id);
        public void Clear() => m_assets.Clear();
    }

    /// <summary>Captures what is logged through the ambient logger factory while it is in use.</summary>
    private sealed class CapturedLog : IDisposable, ILoggerFactory
    {
        private readonly ILoggerFactory m_previous;
        private readonly List<(LogLevel Level, string Message)> m_entries = new();

        public CapturedLog()
        {
            m_previous = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = this;
        }

        public List<string> Warnings
        {
            get
            {
                lock (m_entries)
                    return m_entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();
            }
        }

        public void Dispose() => LoggerProvider.LoggerFactory = m_previous;
        ILogger ILoggerFactory.CreateLogger(string categoryName) => new Recorder(this);
        void ILoggerFactory.AddProvider(ILoggerProvider provider) { }

        private sealed class Recorder : ILogger
        {
            private readonly CapturedLog m_owner;
            public Recorder(CapturedLog owner) => m_owner = owner;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (m_owner.m_entries)
                    m_owner.m_entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
