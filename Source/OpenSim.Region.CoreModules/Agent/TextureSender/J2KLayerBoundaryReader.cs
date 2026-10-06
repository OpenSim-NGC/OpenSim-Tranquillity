/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using CoreJ2K;
using CoreJ2K.j2k.codestream;
using CoreJ2K.j2k.codestream.reader;
using CoreJ2K.j2k.decoder;
using CoreJ2K.j2k.fileformat.reader;
using CoreJ2K.j2k.io;
using CoreJ2K.j2k.util;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Region.CoreModules.Agent.TextureSender;

/// <summary>
/// Reads where each quality layer of a JPEG 2000 codestream starts, as CSJ2K's
/// J2kImage.GetLayerBoundaries did before the texture decoder moved to CoreJ2K.
/// </summary>
/// <remarks>
/// CoreJ2K's FileBitstreamReaderAgent reads every packet head of a tile through its pktDec field
/// when the tile is selected. A PktDecoder subclass put in that field notes, for each layer, the
/// byte offset where its first packet starts (at its SOP marker when the codestream has them).
/// That offset is a layer boundary only when all packets of a layer come before any packet of the
/// next one, so the table is only returned for a single-tile, layer-first codestream whose packet
/// headers are in the packets.
/// </remarks>
public static class J2KLayerBoundaryReader
{
    /// <summary>
    /// The layer table for J2KImage, built from the layer start offsets as the CSJ2K path built it:
    /// each layer runs from its start to the byte before the next layer, and the last one to the
    /// end of the data.
    /// </summary>
    /// <returns>the table, or null when the layer starts are not available</returns>
    public static J2KLayerInfo[] Read(byte[] j2kData)
    {
        int[] starts = ReadLayerStarts(j2kData);
        if (starts == null)
            return null;

        J2KLayerInfo[] layers = new J2KLayerInfo[starts.Length];
        for (int i = 0; i < starts.Length; i++)
        {
            layers[i].Start = i == 0 ? 0 : starts[i];
            layers[i].End = i == starts.Length - 1 ? j2kData.Length : starts[i + 1] - 1;
        }

        return layers;
    }

    /// <summary>
    /// The byte offset in <paramref name="j2kData"/> where the first packet of each quality layer
    /// starts.
    /// </summary>
    /// <returns>
    /// the offsets, or null when the codestream has more than one tile, uses packed packet headers,
    /// is not in a layer-first progression, does not have a start for every layer, or cannot be read
    /// </returns>
    public static int[] ReadLayerStarts(byte[] j2kData)
    {
        try
        {
            // A parameter list with the decoder defaults as its default list
            ParameterList pl = new ParameterList(J2kImage.GetDefaultDecoderParameterList());

            ISRandomAccessIO input = new ISRandomAccessIO(new MemoryStream(j2kData, false));
            FileFormatReader ff = new FileFormatReader(input);
            ff.readFileFormat();
            if (ff.JP2FFUsed)
                input.seek(ff.FirstCodeStreamPos);

            HeaderInfo hi = new HeaderInfo();
            HeaderDecoder hd = new HeaderDecoder(input, pl, hi);
            DecoderSpecs decSpec = hd.DecoderSpecs;
            if (hd.NumTiles != 1)
                return null;

            if (BitstreamReaderAgent.createInstance(input, hd, pl, decSpec, pl.GetBooleanParameter("cdstr_info"), hi)
                is not FileBitstreamReaderAgent reader)
                return null;

            if (decSpec.pphs.GetBoolTileDef(0))
                return null;

            // The same arguments FileBitstreamReaderAgent's constructor gives its own PktDecoder
            LayerStartRecorder recorder = new LayerStartRecorder(decSpec, hd, input, reader,
                !pl.GetBooleanParameter("parsing"), pl.GetIntParameter("ncb_quit"));
            reader.pktDec = recorder;
            reader.SetTile(0, 0);

            if (!recorder.LayerFirst || recorder.Starts.Count != decSpec.nls.GetIntTileDef(0))
                return null;

            return recorder.Starts.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Notes where each layer's first packet starts while the packet heads are read.
    /// </summary>
    private sealed class LayerStartRecorder : PktDecoder
    {
        private readonly RandomAccessIO m_input;
        private int m_sopPosition = -1;
        private int m_lastLayer = -1;

        public readonly List<int> Starts = new List<int>();
        public bool LayerFirst = true;

        public LayerStartRecorder(DecoderSpecs decSpec, HeaderDecoder hd, RandomAccessIO ehs,
            BitstreamReaderAgent src, bool isTruncMode, int maxCB)
            : base(decSpec, hd, ehs, src, isTruncMode, maxCB)
        {
            m_input = ehs;
        }

        public override bool readSOPMarker(int[] nBytes, int p, int c, int r)
        {
            // A packet with an SOP marker starts at the marker
            m_sopPosition = m_input.Pos;
            return base.readSOPMarker(nBytes, p, c, r);
        }

        public override bool readPktHead(int l, int r, int c, int p, CBlkInfo[][][] cbI, int[] nb)
        {
            int start = m_sopPosition >= 0 ? m_sopPosition : m_input.Pos;
            m_sopPosition = -1;

            if (l < m_lastLayer)
                LayerFirst = false;
            else if (l > m_lastLayer)
            {
                if (l == Starts.Count)
                    Starts.Add(start);
                m_lastLayer = l;
            }

            return base.readPktHead(l, r, c, p, cbI, nb);
        }
    }
}
